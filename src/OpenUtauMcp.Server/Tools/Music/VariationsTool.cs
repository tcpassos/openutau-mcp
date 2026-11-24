using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Music.Humanize;
using OpenUtauMcp.Music.Randomness;
using OpenUtauMcp.Server.Bridge;
using OpenUtauMcp.Server.Tools.Core;

namespace OpenUtauMcp.Server.Tools.Music;

public sealed record VariantInput(
    [property: Description("Short ASCII label, e.g. \"B\" or \"soft\"; the track is named \"<track> [<label>]\".")] string Label,
    [property: Description("Humanize preset: natural (default) | backing | chops.")] string? Preset = null,
    [property: Description("0..2 (default: the preset's).")] double? Intensity = null,
    [property: Description("Random seed (default: new).")] int? Seed = null,
    [property: Description("Per-layer overrides, as in ou_music_humanize.")] JsonElement? Layers = null);

/// <summary>A/B variations of a part on parallel tracks. No server state: all in the project.</summary>
[McpServerToolType]
public static partial class VariationsTool {
    const int MaxPerBase = 6;

    [GeneratedRegex(@"^(?<base>.*) \[(?<label>[^\]]+)\]$")]
    private static partial Regex VariationName();

    [GeneratedRegex(@"^[A-Za-z0-9 _.-]{1,24}$")]
    private static partial Regex LabelPattern();

    [McpServerTool(Name = "ou_music_variations", Title = "Create and compare A/B variations", ReadOnly = false, Idempotent = false, OpenWorld = false, Destructive = true)]
    [Description("Compares humanizations of a part on parallel tracks named \"<track> [<label>]\". create: one muted copy per variant, each humanized with its own preset/seed (one undo step; muting is not undoable). list: variations of the part. activate: unmute one (variant label, or \"base\" for the original) and mute the others. promote: keep that variation only, renamed to the original track (needs confirm). remove: delete one variation. Example: {\"action\":\"create\",\"part\":\"Lead\",\"variants\":[{\"label\":\"B\",\"seed\":11},{\"label\":\"C\",\"preset\":\"natural\",\"intensity\":1.4}]}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge,
        [Description("create | list | activate | promote | remove.")] string action,
        [Description("The original part (or any of its variations' parts).")] string part,
        [Description("create: the variants.")] VariantInput[]? variants = null,
        [Description("activate/promote/remove: variant label (\"base\" = the original for activate).")] string? variant = null,
        [Description("promote: confirm removing the original and the other variations.")] bool confirm = false,
        McpServer? server = null,
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            var ctx = await ProjectContext.LoadAsync(bridge, ct, "tempo", "tracks", "parts", "expressions", "comment").ConfigureAwait(false);
            var source = ctx.ResolvePart(part);
            var group = Group(ctx, source);
            return action switch {
                "create" => await CreateAsync(bridge, ctx, group, variants, ct).ConfigureAwait(false),
                "list" => List(group),
                "activate" => await ActivateAsync(bridge, ctx, group, Require(variant, "activate"), ct).ConfigureAwait(false),
                "promote" => await PromoteAsync(bridge, server, ctx, group, Require(variant, "promote"), confirm, ct).ConfigureAwait(false),
                "remove" => await RemoveAsync(bridge, group, Require(variant, "remove"), ct).ConfigureAwait(false),
                _ => throw new ToolInputException(ToolInputException.InvalidParams, $"Unknown action '{action}'. Use create, list, activate, promote or remove."),
            };
        });

    sealed record Member(string Label, TrackDto Track, PartDto? Part);

    sealed record VariationGroup(string BaseName, TrackDto? BaseTrack, PartDto BasePart, List<Member> Variations);

    static VariationGroup Group(ProjectContext ctx, PartDto part) {
        var tracks = ctx.Project.Tracks ?? [];
        var parts = ctx.Project.Parts ?? [];
        var track = tracks.First(t => t.Id == part.TrackId);
        string baseName = VariationName().Match(track.Name) is { Success: true } m ? m.Groups["base"].Value : track.Name;
        var baseTrack = tracks.FirstOrDefault(t => t.Name == baseName);
        // The original part: the given one if it is on the base track, else the base track's part at the same position.
        var basePart = baseTrack is null ? part
            : parts.FirstOrDefault(x => x.TrackId == baseTrack.Id && x.Pos == part.Pos) ?? part;
        var variations = tracks
            .Select(t => (t, m: VariationName().Match(t.Name)))
            .Where(x => x.m.Success && x.m.Groups["base"].Value == baseName)
            .Select(x => new Member(x.m.Groups["label"].Value, x.t, parts.FirstOrDefault(p => p.TrackId == x.t.Id && p.Pos == basePart.Pos)))
            .ToList();
        return new VariationGroup(baseName, baseTrack, basePart, variations);
    }

    static string Require(string? variant, string action) =>
        variant ?? throw new ToolInputException(ToolInputException.InvalidParams, $"{action} needs 'variant' (a label from action list).");

    static Member Find(VariationGroup g, string label) =>
        g.Variations.FirstOrDefault(v => string.Equals(v.Label, label, StringComparison.OrdinalIgnoreCase) || v.Track.Id == label)
        ?? throw new ToolInputException(ErrorCodes.NotFound, $"No variation '{label}' of '{g.BaseName}'.", "List them with action list.",
            new JsonObject { ["variants"] = new JsonArray(g.Variations.Select(v => (JsonNode?)v.Label).ToArray()) });

    static async Task<CallToolResult> CreateAsync(IBridgeClient bridge, ProjectContext ctx, VariationGroup g, VariantInput[]? variants, CancellationToken ct) {
        if (variants is not { Length: > 0 }) {
            throw new ToolInputException(ToolInputException.InvalidParams, "create needs 'variants'.");
        }
        if (g.BaseTrack is null) {
            throw new ToolInputException(ToolInputException.InvalidParams, $"The original track '{g.BaseName}' no longer exists.");
        }
        if (g.Variations.Count + variants.Length > MaxPerBase) {
            throw new ToolInputException(ErrorCodes.LimitExceeded,
                $"'{g.BaseName}' would have {g.Variations.Count + variants.Length} variations; the limit is {MaxPerBase} (each DiffSinger track costs rendering).",
                "Remove or promote some first.");
        }
        foreach (var v in variants) {
            if (!LabelPattern().IsMatch(v.Label) || v.Label.Equals("base", StringComparison.OrdinalIgnoreCase)) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"Label '{v.Label}': use 1-24 ASCII letters, digits, space, _ . - (not \"base\").");
            }
            if (g.Variations.Any(x => string.Equals(x.Label, v.Label, StringComparison.OrdinalIgnoreCase))
                || variants.Count(x => string.Equals(x.Label, v.Label, StringComparison.OrdinalIgnoreCase)) > 1) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"Label '{v.Label}' is already used.");
            }
        }
        var part = g.BasePart;
        var read = await bridge.AllNotesAsync(part.Id, ["pitch", "vibrato", "exprs"], ct).ConfigureAwait(false);
        var curveAbbrs = new[] { "pitd", "dyn", "velc", "genc" };
        var currentCurves = (await bridge.CurvesQueryAsync(new CurvesQueryParams { PartId = part.Id, Abbrs = curveAbbrs }, ct).ConfigureAwait(false))
            .Curves.ToDictionary(c => c.Abbr);
        var singer = await bridge.CatalogSingerAsync(new CatalogSingerParams { TrackId = g.BaseTrack.Id }, ct).ConfigureAwait(false);
        var sections = SongComments.Sections(ctx.Project.Comment, ctx.Time);
        var warnings = new List<Warning>();
        if (sections.Count == 0) {
            warnings.Add(new Warning("NO_SECTIONS", "No sections stored in the project: every note is treated as role 'other'."));
        }
        var ops = new List<EditOp>();
        var report = new JsonArray();
        var registered = (ctx.Project.Expressions ?? []).Select(e => e.Abbr).ToHashSet();
        for (int i = 0; i < variants.Length; i++) {
            var v = variants[i];
            string t = $"@t{i}", p = $"@p{i}", prefix = $"@d{i}:";
            var parameters = HumanizeTool.Parameters(v.Preset ?? "natural", v.Intensity, v.Layers, null);
            int seed = v.Seed ?? Random.Shared.Next(1, int.MaxValue);
            var colors = HumanizeCore.ColorMap(singer, null);
            parameters = HumanizeCore.DisableIneffective(parameters, singer, colors, warnings, v.Label);
            var plan = Humanizer.Plan(new HumanizeInput {
                Notes = read.Notes.Select(n => new HNote(n.Id, n.Pos, n.Dur, n.Tone, n.Lyric)).ToList(),
                PartPos = part.Pos,
                Bpm = ctx.Project.Tempos?.FirstOrDefault()?.Bpm ?? 120,
                Sections = sections,
                Colors = colors,
            }, parameters, new SubstreamRandom(seed));
            // The copy starts identical to the original, so the diff against the original is the diff for the copy.
            var built = HumanizeCore.BuildOps(plan, read.Notes, currentCurves, part.Dur, wholePart: true, new HumanizeCore.Target {
                PartId = p, NoteRef = id => prefix + id, RegisteredExpressions = registered, TrackIdForEnsure = g.BaseTrack.Id,
            });
            foreach (var op in built.Ops.OfType<ProjectExpressionsOp>()) {
                registered.UnionWith(op.Ensure);
            }
            ops.Add(new TrackAddOp { CopyFrom = g.BaseTrack.Id, Name = $"{g.BaseName} [{v.Label}]", Ref = t });
            ops.Add(new TrackMixOp { TrackId = t, Mute = true });
            ops.Add(new PartDuplicateOp { PartId = part.Id, TrackId = t, Name = $"{part.Name} [{v.Label}]", Ref = p, NoteRefPrefix = prefix });
            ops.AddRange(built.Ops);
            var meta = HumanizeCore.History(parameters, seed, new JsonObject { ["variationOf"] = g.BaseTrack.Id, ["label"] = v.Label });
            ops.Add(new PartCommentOp { PartId = p, Comment = SongComments.Write(part.Comment, meta) });
            report.Add(new JsonObject { ["label"] = v.Label, ["preset"] = parameters.Preset, ["seed"] = seed, ["changed"] = built.Changed });
        }
        var result = await bridge.EditApplyAsync(new EditApplyParams {
            UndoName = $"MCP: {variants.Length} variation(s) of {g.BaseName}", Ops = ops,
        }, ct).ConfigureAwait(false);
        for (int i = 0; i < variants.Length; i++) {
            report[i]!["track"] = result.Created?.GetValueOrDefault($"@t{i}");
            report[i]!["part"] = result.Created?.GetValueOrDefault($"@p{i}");
        }
        warnings.Add(new Warning(WarningCodes.NotUndoable, "The variations are created muted; undoing removes them, the mute itself is not an undo step."));
        return ToolResult.Ok($"Created {variants.Length} muted variation(s) of '{g.BaseName}' in one undo step. " +
                             "Listen with ou_music_variations activate (or ou_playback with track).",
            new JsonObject { ["base"] = g.BaseTrack.Id, ["variants"] = report }, warnings, new JsonObject { ["epoch"] = result.Epoch });
    }

    static CallToolResult List(VariationGroup g) {
        var rows = g.Variations.Select(v => {
            var meta = SongComments.Read(v.Part?.Comment);
            return new JsonNode?[] {
                v.Label, v.Track.Id, v.Part?.Id, !v.Track.Mute,
                meta?["humanize"]?["preset"]?.DeepClone(), meta?["humanize"]?["seed"]?.DeepClone(),
            };
        }).ToList();
        var data = ProjectGetTool.Table(["label", "track", "part", "audible", "preset", "seed"], rows);
        data["base"] = new JsonObject { ["track"] = g.BaseTrack?.Id, ["part"] = g.BasePart.Id, ["audible"] = g.BaseTrack is { Mute: false } };
        return ToolResult.Ok($"'{g.BaseName}' has {g.Variations.Count} variation(s).", data);
    }

    static async Task<CallToolResult> ActivateAsync(IBridgeClient bridge, ProjectContext ctx, VariationGroup g, string label, CancellationToken ct) {
        bool toBase = label.Equals("base", StringComparison.OrdinalIgnoreCase);
        var chosen = toBase ? null : Find(g, label);
        var ops = new List<EditOp>();
        if (g.BaseTrack is { } baseTrack) {
            ops.Add(new TrackMixOp { TrackId = baseTrack.Id, Mute = !toBase });
        }
        ops.AddRange(g.Variations.Select(v => new TrackMixOp { TrackId = v.Track.Id, Mute = v != chosen }));
        await bridge.EditApplyAsync(new EditApplyParams { UndoName = $"MCP: activate {label}", Ops = ops }, ct).ConfigureAwait(false);
        var warnings = new List<Warning>();
        if (!toBase && g.BaseTrack is { } bt && (ctx.Project.Parts ?? []).Any(p => p.TrackId == bt.Id && p.Id != g.BasePart.Id)) {
            warnings.Add(new Warning("TRACK_MUTED", $"Muting is per track: the other parts of '{bt.Name}' are muted too until you activate \"base\" or promote."));
        }
        return ToolResult.Ok($"'{(toBase ? g.BaseName : chosen!.Track.Name)}' is now the audible one; the others in the group are muted (not undoable).",
            new JsonObject { ["audible"] = toBase ? g.BaseTrack?.Id : chosen!.Track.Id }, warnings);
    }

    static async Task<CallToolResult> PromoteAsync(IBridgeClient bridge, McpServer? server, ProjectContext ctx, VariationGroup g, string label, bool confirm,
                                                   CancellationToken ct) {
        var chosen = Find(g, label);
        if (chosen.Part is null) {
            throw new ToolInputException(ErrorCodes.NotFound, $"Variation '{chosen.Label}' has no part at the original part's position.",
                "List them with action list.");
        }
        var parts = ctx.Project.Parts ?? [];
        bool OnlyPart(TrackDto t, PartDto p) => parts.All(x => x.TrackId != t.Id || x.Id == p.Id);
        var ops = new List<EditOp>();
        var removed = new List<string>();
        void Drop(TrackDto t, PartDto p) {
            if (OnlyPart(t, p)) {
                ops.Add(new TrackRemoveOp { TrackId = t.Id });
                removed.Add($"track '{t.Name}'");
            } else {
                ops.Add(new PartRemoveOp { PartId = p.Id });
                removed.Add($"part '{p.Name}' of '{t.Name}'");
            }
        }
        string keptTrack;
        if (g.BaseTrack is { } baseTrack && !OnlyPart(baseTrack, g.BasePart)) {
            // The original track holds other parts: swap only this part and keep the track with its other parts.
            ops.Add(new PartRemoveOp { PartId = g.BasePart.Id });
            removed.Add($"part '{g.BasePart.Name}' of '{baseTrack.Name}'");
            ops.Add(new PartUpdateOp { PartId = chosen.Part.Id, TrackId = baseTrack.Id, Name = g.BasePart.Name });
            if (OnlyPart(chosen.Track, chosen.Part)) {
                ops.Add(new TrackRemoveOp { TrackId = chosen.Track.Id });
            }
            ops.Add(new TrackMixOp { TrackId = baseTrack.Id, Mute = false });
            keptTrack = baseTrack.Id;
        } else {
            if (g.BaseTrack is { } b) {
                ops.Add(new TrackRemoveOp { TrackId = b.Id });
                removed.Add($"track '{b.Name}'");
            }
            ops.Add(new TrackUpdateOp { TrackId = chosen.Track.Id, Name = g.BaseName });
            ops.Add(new TrackMixOp { TrackId = chosen.Track.Id, Mute = false });
            keptTrack = chosen.Track.Id;
        }
        foreach (var v in g.Variations.Where(v => v != chosen && v.Part is not null)) {
            Drop(v.Track, v.Part!);
        }
        await Confirmation.RequireAsync(server, confirm,
            $"Promote '{chosen.Track.Name}' to '{g.BaseName}': remove {string.Join(", ", removed)}? (Undo restores them.)",
            "Ask the user, then pass confirm:true.", ct).ConfigureAwait(false);
        var result = await bridge.EditApplyAsync(new EditApplyParams { UndoName = $"MCP: promote {chosen.Track.Name}", Ops = ops }, ct).ConfigureAwait(false);
        return ToolResult.Ok($"Promoted '{chosen.Label}': it is now '{g.BaseName}'; removed {string.Join(", ", removed)}. One undo step.",
            new JsonObject {
                ["track"] = keptTrack, ["part"] = chosen.Part.Id,
                ["removed"] = new JsonArray((result.Removed ?? []).Select(r => (JsonNode?)r).ToArray()),
            },
            (result.Warnings ?? []).Where(w => w.Code != WarningCodes.NotUndoable));
    }

    static async Task<CallToolResult> RemoveAsync(IBridgeClient bridge, VariationGroup g, string label, CancellationToken ct) {
        var chosen = Find(g, label);
        await bridge.EditApplyAsync(new EditApplyParams { UndoName = $"MCP: remove {chosen.Track.Name}", Ops = [new TrackRemoveOp { TrackId = chosen.Track.Id }] }, ct)
            .ConfigureAwait(false);
        return ToolResult.Ok($"Removed variation '{chosen.Label}' ({chosen.Track.Id}). One undo step.", new JsonObject { ["removed"] = chosen.Track.Id });
    }
}
