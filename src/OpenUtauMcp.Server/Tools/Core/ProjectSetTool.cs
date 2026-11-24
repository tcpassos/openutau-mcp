using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Music.Humanize;
using OpenUtauMcp.Server.Bridge;
using OpenUtauMcp.Server.Tools.Music;
using OpenUtauMcp.Server.Units;

namespace OpenUtauMcp.Server.Tools.Core;

public sealed record TempoAtInput(
    [property: Description("Position bar:beat, e.g. \"33:1\".")] string At,
    [property: Description("BPM (10..1000).")] double Bpm);

public sealed record TempoChangesInput(
    [property: Description("Tempo changes to add (after the start of the song).")] TempoAtInput[]? Add = null,
    [property: Description("Positions of tempo changes to remove.")] string[]? Remove = null);

public sealed record ExpressionsEnsureInput(
    [property: Description("Expressions to register, e.g. [\"velc\",\"cl01\"].")] string[] Ensure,
    [property: Description("Track whose renderer suggests the descriptors (default: the first track).")] string? SuggestedFor = null);

[McpServerToolType]
public static class ProjectSetTool {
    static readonly string[] sharpKeys = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    static readonly string[] flatKeys = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];

    [McpServerTool(Name = "ou_project_set", Title = "Set tempo, time signature, key, expressions and sections", ReadOnly = false, Idempotent = true, OpenWorld = false, Destructive = true)]
    [Description("Sets BPM, tempo changes, time signatures, key, registers expressions and stores song sections in the project comment. One undo step, except sections. Example: {\"bpm\":128,\"timeSig\":\"4/4\",\"key\":\"Dm\",\"expressions\":{\"ensure\":[\"velc\"]},\"sections\":[{\"name\":\"verse1\",\"role\":\"verse\",\"bars\":\"9-16\"}]}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge,
        [Description("Initial tempo in BPM (10..1000).")] double? bpm = null,
        [Description("Tempo changes: {add:[{at, bpm}], remove:[at]}.")] TempoChangesInput? tempoChanges = null,
        [Description("\"3/4\" sets the first time signature; {add:[{bar: 1-based, sig: \"6/8\"}], remove:[bar]} edits later changes.")] JsonElement? timeSig = null,
        [Description("Key, e.g. \"D\", \"Bb\", \"F#m\", or 0..11.")] string? key = null,
        [Description("Register expression descriptors: {ensure:[abbr...], suggestedFor?: track}.")] ExpressionsEnsureInput? expressions = null,
        [Description("Song sections stored in the project comment (replaces the stored list; not undoable).")] SectionInput[]? sections = null,
        [Description("Only validate; change nothing.")] bool dryRun = false,
        [Description("Undo step name (English).")] string? undoName = null,
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            var ctx = await ProjectContext.LoadAsync(bridge, ct, "tempo", "tracks", "parts", "comment").ConfigureAwait(false);
            var project = ctx.Project;
            var ops = new List<EditOp>();
            var changes = new List<string>();

            var (first, sigAdd, sigRemove) = ParseTimeSig(timeSig);
            // Positions given in this call refer to the time signatures as they will be after it.
            var signatures = (project.TimeSignatures ?? []).Where(s => !sigRemove.Contains(s.Bar)).ToList();
            if (first is not null) {
                signatures.RemoveAll(s => s.Bar == 0);
                signatures.Add(first);
            }
            signatures.RemoveAll(s => sigAdd.Any(a => a.Bar == s.Bar));
            signatures.AddRange(sigAdd);
            var time = new TimeMap(signatures);

            int? tonic = null;
            string? mode = null;
            if (key is not null) {
                (tonic, mode) = ParseKey(key);
            }

            var update = new ProjectUpdateOp {
                Bpm = bpm,
                TempoAdd = tempoChanges?.Add?.Select(t => new TempoDto(time.ParsePosition(t.At), t.Bpm)).ToList(),
                TempoRemove = tempoChanges?.Remove?.Select(time.ParsePosition).ToList(),
                TimeSig = first,
                TimeSigAdd = sigAdd.Count > 0 ? sigAdd : null,
                TimeSigRemove = sigRemove.Count > 0 ? sigRemove : null,
                Key = tonic,
                Comment = CommentUpdate(project.Comment, sections, key is not null, mode, time),
            };
            if (bpm is not null) changes.Add($"bpm {bpm.Value.ToString(CultureInfo.InvariantCulture)}");
            if (update.TempoAdd is { Count: > 0 } || update.TempoRemove is { Count: > 0 }) changes.Add("tempo changes");
            if (first is not null || sigAdd.Count > 0 || sigRemove.Count > 0) changes.Add("time signature");
            if (tonic is int k) changes.Add($"key {sharpKeys[k]}{(mode == "minor" ? "m" : "")}");
            if (sections is not null) changes.Add($"{sections.Length} sections");
            if (update != new ProjectUpdateOp()) {
                ops.Add(update);
            }
            if (expressions is not null) {
                if (expressions.Ensure is not { Length: > 0 }) {
                    throw new ToolInputException(ToolInputException.InvalidParams, "expressions.ensure needs at least one abbreviation.");
                }
                string? trackId = expressions.SuggestedFor is { } t ? ctx.ResolveTrack(t).Id : project.Tracks?.FirstOrDefault()?.Id;
                ops.Add(new ProjectExpressionsOp { Ensure = expressions.Ensure, SuggestedFor = trackId });
                changes.Add($"expressions {string.Join(",", expressions.Ensure)}");
            }
            if (ops.Count == 0) {
                throw new ToolInputException(ToolInputException.InvalidParams, "Nothing to do: give bpm, tempoChanges, timeSig, key, expressions or sections.");
            }

            var result = await bridge.EditApplyAsync(new EditApplyParams {
                UndoName = undoName ?? "MCP: project settings", DryRun = dryRun, Ops = ops,
            }, ct).ConfigureAwait(false);
            string what = string.Join(", ", changes);
            string summary = dryRun ? $"Dry run: {what} would be set; nothing changed." : $"Project updated: {what}.";
            return ToolResult.Ok(summary, new JsonObject { ["changed"] = new JsonArray(changes.Select(c => (JsonNode?)c).ToArray()) },
                result.Warnings, new JsonObject { ["epoch"] = result.Epoch, ["structureRev"] = result.StructureRev });
        });

    static (TimeSignatureDto? first, List<TimeSignatureDto> add, List<int> remove) ParseTimeSig(JsonElement? input) {
        var add = new List<TimeSignatureDto>();
        var remove = new List<int>();
        if (input is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) {
            return (null, add, remove);
        }
        if (v.ValueKind == JsonValueKind.String) {
            var (num, den) = Sig(v.GetString()!);
            return (new TimeSignatureDto(0, num, den), add, remove);
        }
        if (v.ValueKind != JsonValueKind.Object) {
            throw new ToolInputException(ToolInputException.InvalidParams, "timeSig: use \"3/4\" or {add:[{bar, sig}], remove:[bar]}.");
        }
        foreach (var prop in v.EnumerateObject()) {
            switch (prop.Name) {
                case "add":
                    foreach (var a in prop.Value.EnumerateArray()) {
                        if (!a.TryGetProperty("bar", out var bar) || bar.ValueKind != JsonValueKind.Number
                            || !a.TryGetProperty("sig", out var sig) || sig.ValueKind != JsonValueKind.String) {
                            throw new ToolInputException(ToolInputException.InvalidParams, "timeSig.add: each item is {bar: 1-based bar, sig: \"6/8\"}.");
                        }
                        var (num, den) = Sig(sig.GetString()!);
                        add.Add(new TimeSignatureDto(Bar0(bar.GetInt32()), num, den));
                    }
                    break;
                case "remove":
                    remove.AddRange(prop.Value.EnumerateArray().Select(b => Bar0(b.GetInt32())));
                    break;
                default:
                    throw new ToolInputException(ToolInputException.InvalidParams, $"timeSig: unknown field '{prop.Name}'.");
            }
        }
        return (null, add, remove);
    }

    static int Bar0(int bar) => bar >= 1 ? bar - 1
        : throw new ToolInputException(ToolInputException.InvalidParams, $"Bars are 1-based; got {bar}.");

    static (int num, int den) Sig(string text) {
        var parts = text.Split('/');
        if (parts.Length == 2 && int.TryParse(parts[0], out int num) && int.TryParse(parts[1], out int den)) {
            return (num, den);
        }
        throw new ToolInputException(ToolInputException.InvalidParams, $"Cannot read time signature '{text}'. Use \"4/4\", \"3/4\", \"6/8\".");
    }

    /// <summary>"D" / "Dm" / "Bb" / "F#m" / "0".."11" to (tonic pitch class, mode or null).</summary>
    internal static (int tonic, string? mode) ParseKey(string text) {
        string s = text.Trim();
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) {
            return n is >= 0 and <= 11 ? (n, null)
                : throw new ToolInputException(ToolInputException.InvalidParams, $"Key {n} is out of range 0..11.");
        }
        string? mode = null;
        if (s.EndsWith("min", StringComparison.OrdinalIgnoreCase)) {
            (s, mode) = (s[..^3], "minor");
        } else if (s.EndsWith("maj", StringComparison.OrdinalIgnoreCase)) {
            (s, mode) = (s[..^3], "major");
        } else if (s.Length > 1 && s.EndsWith('m')) {
            (s, mode) = (s[..^1], "minor");
        }
        if (s.Length > 0) {
            s = char.ToUpperInvariant(s[0]) + s[1..];
        }
        int tonic = Array.IndexOf(sharpKeys, s);
        if (tonic < 0) {
            tonic = Array.IndexOf(flatKeys, s);
        }
        return tonic >= 0 ? (tonic, mode)
            : throw new ToolInputException(ToolInputException.InvalidParams, $"Cannot read key '{text}'. Use e.g. \"D\", \"Bb\", \"F#m\" or 0..11.");
    }

    /// <summary>New project comment with the [openutau-mcp] block updated, or null when it does not change.</summary>
    static string? CommentUpdate(string? comment, SectionInput[]? sections, bool keyGiven, string? mode, TimeMap time) {
        if (sections is null && !keyGiven) {
            return null;
        }
        var block = SongComments.Read(comment) ?? new JsonObject();
        if (sections is not null) {
            var list = new JsonArray();
            foreach (var s in sections) {
                if (!Roles.All.Contains(s.Role)) {
                    throw new ToolInputException(ToolInputException.InvalidParams,
                        $"Section '{s.Name}': unknown role '{s.Role}'. Use {string.Join(", ", Roles.All)}.");
                }
                int from, to;
                if (s.Bars is { } bars) {
                    (from, to) = time.ParseBars(bars);
                } else if (s.From is { } f && s.To is { } t) {
                    (from, to) = (time.ParsePosition(f), time.ParsePosition(t));
                } else {
                    throw new ToolInputException(ToolInputException.InvalidParams, $"Section '{s.Name}': give 'bars' or both 'from' and 'to'.");
                }
                if (to <= from) {
                    throw new ToolInputException(ToolInputException.InvalidParams, $"Section '{s.Name}' must end after it starts.");
                }
                list.Add(new JsonObject {
                    ["name"] = s.Name, ["role"] = s.Role, ["from"] = time.FormatPosition(from), ["to"] = time.FormatPosition(to),
                });
            }
            block["sections"] = list;
        }
        if (mode is not null) {
            block["mode"] = mode;
        } else if (keyGiven) {
            block.Remove("mode");  // a plain tonic replaces an earlier minor key
        }
        if (block.Count == 0 && SongComments.Read(comment) is null) {
            return null;
        }
        string updated = SongComments.Write(comment, block);
        return updated == (comment ?? "") ? null : updated;
    }
}
