using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Server.Bridge;

namespace OpenUtauMcp.Server.Tools.Core;

[McpServerToolType]
public static partial class LyricsSetTool {
    static readonly HashSet<string> breaths = new(StringComparer.OrdinalIgnoreCase) { "AP", "SP", "br", "R" };

    [McpServerTool(Name = "ou_lyrics_set", Title = "Spread lyrics over notes", ReadOnly = false, Idempotent = true, OpenWorld = false, Destructive = true)]
    [Description("Writes text over the selected notes, one syllable per note, in one undo step; breaths (AP, SP, br) are skipped. \"Can-dles\" = \"Candles\" then \"+\"; a lone \"+\" holds the syllable; \"[f o n]\" is a phonetic hint. Count mismatch: onMismatch error (default), truncate or pad. Example: {\"select\":{\"part\":\"Lead\",\"range\":{\"bars\":\"9-12\"}},\"text\":\"Wear your por-ce-lain crown +\"}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge,
        [Description("Notes to write, in time order: {part, range?, ids?, phrases?, lyric?}.")] NoteSelector select,
        [Description("Lyrics text.")] string text,
        [Description("error | truncate | pad.")] string onMismatch = "error",
        [Description("Only validate; change nothing.")] bool dryRun = false,
        [Description("Undo step name (English).")] string? undoName = null,
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            if (onMismatch is not ("error" or "truncate" or "pad")) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"Unknown onMismatch '{onMismatch}'. Use error, truncate or pad.");
            }
            var syllables = Syllables(text);
            if (syllables.Count == 0) {
                throw new ToolInputException(ToolInputException.InvalidParams, "The text has no syllables.");
            }
            var ctx = await ProjectContext.LoadAsync(bridge, ct).ConfigureAwait(false);
            var part = ctx.ResolvePart(select.Part);
            var all = await bridge.AllNotesAsync(part.Id, null, ct).ConfigureAwait(false);
            var notes = NotesGetTool.Filter(all.Notes, part, ctx.Time, select.Range, select.Ids, select.Phrases, select.Lyric)
                .Where(n => !breaths.Contains(n.Lyric)).OrderBy(n => n.Pos).ToList();
            if (notes.Count == 0) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"No singable notes match the selection in part {part.Id}.");
            }

            if (syllables.Count != notes.Count) {
                bool moreSyllables = syllables.Count > notes.Count;
                if (onMismatch == "error" || onMismatch == "pad" && moreSyllables) {
                    int shown = Math.Min(Math.Max(notes.Count, syllables.Count), 40);
                    var alignment = new JsonArray(Enumerable.Range(0, shown).Select(i => (JsonNode?)new JsonArray(
                        i < notes.Count ? notes[i].Id : null,
                        i < notes.Count ? ctx.Time.FormatPosition(part.Pos + notes[i].Pos) : null,
                        i < syllables.Count ? syllables[i] : null)).ToArray());
                    throw new ToolInputException(ToolInputException.LyricsCountMismatch,
                        $"{syllables.Count} syllables for {notes.Count} notes.",
                        moreSyllables
                            ? "Add notes, select more notes, merge syllables, or pass onMismatch:\"truncate\"."
                            : "Split words with hyphens, add \"+\" for held notes, or pass onMismatch:\"pad\" or \"truncate\".",
                        new JsonObject { ["syllables"] = syllables.Count, ["notes"] = notes.Count, ["alignment"] = alignment });
                }
            }
            var ops = new List<EditOp>();
            int assigned = 0;
            for (int i = 0; i < notes.Count; i++) {
                string? lyric = i < syllables.Count ? syllables[i] : onMismatch == "pad" ? "+" : null;
                if (lyric is null) {
                    break;
                }
                assigned++;
                if (lyric != notes[i].Lyric) {
                    ops.Add(new NoteUpdateOp { NoteId = notes[i].Id, Lyric = lyric });
                }
            }
            var data = new JsonObject {
                ["notes"] = notes.Count, ["syllables"] = syllables.Count, ["assigned"] = assigned, ["changed"] = ops.Count,
            };
            if (ops.Count == 0) {
                return ToolResult.Ok($"The lyrics already match; nothing changed in {part.Id}.", data);
            }
            var result = await bridge.EditApplyAsync(new EditApplyParams {
                UndoName = undoName ?? $"MCP: lyrics on {ops.Count} notes",
                IfRev = new Dictionary<string, int> { [part.Id] = all.PartRev },
                DryRun = dryRun,
                Ops = ops,
            }, ct).ConfigureAwait(false);
            string summary = dryRun
                ? $"Dry run: {ops.Count} lyrics would change in {part.Id} ({part.Name}); nothing changed."
                : $"Lyrics written on {ops.Count} notes of {part.Id} ({part.Name}). Undo with ou_history or Ctrl+Z in OpenUtau.";
            return ToolResult.Ok(summary, data, result.Warnings, new JsonObject {
                ["epoch"] = result.Epoch,
                ["partRev"] = new JsonObject(result.PartRevs.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            });
        });

    [GeneratedRegex(@"\[[^\]]*\]|[^\s\[]+")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^[\p{P}-[\-'+]]+|[\p{P}-[\-'+]]+$")]
    private static partial Regex EdgePunctuation();

    /// <summary>Splits text into one lyric per note: "Can-dles" -> "Candles", "+"; "+" stays; "[f o n]" stays whole.</summary>
    internal static List<string> Syllables(string text) {
        var result = new List<string>();
        foreach (Match m in TokenPattern().Matches(text)) {
            string token = m.Value;
            if (token.StartsWith('[')) {
                result.Add(token);
                continue;
            }
            token = EdgePunctuation().Replace(token, "");
            if (token.Length == 0) {
                continue;
            }
            if (token.Trim('+').Length == 0) {
                result.AddRange(Enumerable.Repeat("+", token.Length));
                continue;
            }
            var pieces = token.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length == 0) {
                continue;   // a dash on its own ("word - word") is punctuation, not a syllable
            }
            result.Add(string.Concat(pieces));
            result.AddRange(Enumerable.Repeat("+", pieces.Length - 1));
        }
        return result;
    }
}
