using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed record SectionInput(
    [property: Description("Section name, e.g. \"chorus1\".")] string Name,
    [property: Description("intro | verse | pre | chorus | drop | bridge | outro | other")] string Role,
    [property: Description("Bars, e.g. \"21-28\". Or use from/to.")] string? Bars = null,
    [property: Description("Start position bar:beat.")] string? From = null,
    [property: Description("End position bar:beat (exclusive).")] string? To = null);

[McpServerToolType]
public static class HumanizeTool {
    static readonly string[] curveAbbrs = ["pitd", "dyn", "velc", "genc"];

    [McpServerTool(Name = "ou_music_humanize", Title = "Humanize notes (pitch, vibrato, dynamics, breaths, voice color)",
        ReadOnly = false, Idempotent = true, OpenWorld = false, Destructive = true)]
    [Description("Makes a sung part less robotic in one undo step: scoops, portamento, overshoot, drift, falls, vibrato, dynamics, consonants, formant, breaths and voice color by section. Uses whole phrases as context and changes only the selection. Same seed = same result. Presets: natural (lead), backing, chops. Example: {\"select\":{\"part\":\"Lead\",\"range\":{\"bars\":\"17-24\"}},\"preset\":\"natural\",\"sections\":[{\"name\":\"chorus1\",\"role\":\"chorus\",\"bars\":\"21-28\"}]}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge,
        [Description("Notes to humanize (default: the whole part).")] NoteSelector select,
        [Description("natural (default) | backing | chops")] string preset = "natural",
        [Description("0..2, scales depths (default: the preset's).")] double? intensity = null,
        [Description("Random seed; omit for a new one (always returned so the result can be reproduced).")] int? seed = null,
        [Description("Per-layer overrides {layer: false | {param: value}} (layers: ou_catalog_get humanize_presets).")] JsonElement? layers = null,
        [Description("Song sections (role sets dynamics, color, vibrato). Default: those stored in the project.")] SectionInput[]? sections = null,
        [Description("Voice color per role or section, e.g. {\"verse\":\"Soft\"} (name, index or normal/power/soft).")] Dictionary<string, string>? colorBySection = null,
        [Description("Layers to leave untouched; \"pitch\" = all pitch layers.")] string[]? preserve = null,
        [Description("Compute and report without changing anything.")] bool dryRun = false,
        [Description("Name of the undo step (English).")] string? undoName = null,
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            var parameters = Parameters(preset, intensity, layers, preserve);
            int usedSeed = seed ?? Random.Shared.Next(1, int.MaxValue);

            for (int attempt = 0; ; attempt++) {
                try {
                    return await HumanizeOnceAsync(bridge, select, parameters, usedSeed, sections, colorBySection, dryRun, undoName, ct)
                        .ConfigureAwait(false);
                } catch (BridgeCallException e) when (e.Code == ErrorCodes.Conflict && attempt == 0) {
                    // The user edited the part between our read and the write: read again and recompute once.
                }
            }
        });

    static async Task<CallToolResult> HumanizeOnceAsync(IBridgeClient bridge, NoteSelector select, HumanizeParams parameters, int seed,
        SectionInput[]? sectionInputs, Dictionary<string, string>? colorBySection, bool dryRun, string? undoName, CancellationToken ct) {
        var ctx = await ProjectContext.LoadAsync(bridge, ct, "tempo", "tracks", "parts", "expressions", "comment").ConfigureAwait(false);
        var part = ctx.ResolvePart(select.Part);
        if (part.Type != "voice") {
            throw new ToolInputException("WRONG_KIND", $"Part {part.Id} is an audio part.");
        }
        var warnings = new List<Warning>();
        var read = await bridge.AllNotesAsync(part.Id, ["pitch", "vibrato", "exprs"], ct).ConfigureAwait(false);
        var notes = read.Notes;
        bool filtered = select.Range is not null || select.Ids is { Length: > 0 } || select.Phrases is { Length: > 0 } || select.Lyric is not null;
        IReadOnlySet<string>? selection = filtered
            ? NotesGetTool.Filter(notes, part, ctx.Time, select.Range, select.Ids, select.Phrases, select.Lyric).Select(n => n.Id).ToHashSet()
            : null;

        var sections = sectionInputs is { Length: > 0 }
            ? sectionInputs.Select(s => ToSection(ctx, part, s)).ToList()
            : SongComments.Sections(ctx.Project.Comment, ctx.Time);
        if (sections.Count == 0) {
            warnings.Add(new Warning("NO_SECTIONS", "No sections given or stored: every note is treated as role 'other' (no voice color, flat dynamics base).", part.Id));
        }
        if ((ctx.Project.Tempos?.Count ?? 0) > 1) {
            warnings.Add(new Warning("TEMPO_CHANGES", "The project has tempo changes; humanization uses the first tempo for ms-to-tick conversion.", part.Id));
        }

        var singer = await bridge.CatalogSingerAsync(new CatalogSingerParams { TrackId = part.TrackId }, ct).ConfigureAwait(false);
        var colors = HumanizeCore.ColorMap(singer, colorBySection);
        parameters = HumanizeCore.DisableIneffective(parameters, singer, colors, warnings, part.Id);

        var input = new HumanizeInput {
            Notes = notes.Select(n => new HNote(n.Id, n.Pos, n.Dur, n.Tone, n.Lyric)).ToList(),
            PartPos = part.Pos,
            Bpm = ctx.Project.Tempos?.FirstOrDefault()?.Bpm ?? 120,
            Sections = sections,
            Selection = selection,
            Colors = colors,
        };
        var plan = Humanizer.Plan(input, parameters, new SubstreamRandom(seed));
        warnings.AddRange(plan.Warnings.Select(w => new Warning(HumanizeCore.WarningCode(w), w, part.Id)));

        var currentCurves = plan.Curves.Count == 0 ? new Dictionary<string, CurveDto>()
            : (await bridge.CurvesQueryAsync(new CurvesQueryParams { PartId = part.Id, Abbrs = plan.Curves.Keys.ToList() }, ct).ConfigureAwait(false))
                .Curves.ToDictionary(c => c.Abbr);
        var built = HumanizeCore.BuildOps(plan, notes, currentCurves, part.Dur, wholePart: selection is null, new HumanizeCore.Target {
            PartId = part.Id,
            RegisteredExpressions = (ctx.Project.Expressions ?? []).Select(e => e.Abbr).ToHashSet(),
            TrackIdForEnsure = part.TrackId,
        });
        var ops = built.Ops;

        var data = new JsonObject {
            ["part"] = part.Id,
            ["seed"] = seed,
            ["preset"] = parameters.Preset,
            ["intensity"] = parameters.Intensity,
            ["notes"] = plan.Notes.Count,
            ["from"] = ctx.Time.FormatPosition(part.Pos + built.From),
            ["to"] = ctx.Time.FormatPosition(part.Pos + Math.Min(built.To, part.Dur)),
            ["layers"] = new JsonObject(plan.LayerCounts.Where(kv => kv.Value > 0).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["changed"] = built.Changed,
        };
        if (ops.All(op => op is ProjectExpressionsOp)) {
            return ToolResult.Ok($"{part.Id} ({part.Name}) already has this humanization (seed {seed}); nothing changed.", data, warnings);
        }
        if (dryRun) {
            return ToolResult.Ok($"Dry run: humanizing {plan.Notes.Count} notes of {part.Id} ({part.Name}) with {parameters.Preset}, seed {seed}, would make {ops.Count} change(s); nothing changed.",
                data, warnings);
        }

        ops.Add(new PartCommentOp { PartId = part.Id, Comment = SongComments.Write(part.Comment, HumanizeCore.History(parameters, seed)) });

        var result = await bridge.EditApplyAsync(new EditApplyParams {
            UndoName = undoName ?? $"MCP: humanize {part.Name} ({parameters.Preset}, seed {seed})",
            IfRev = new Dictionary<string, int> { [part.Id] = read.PartRev },
            Ops = ops,
        }, ct).ConfigureAwait(false);
        // The history comment is expected to be non-undoable; do not repeat that warning.
        warnings.AddRange((result.Warnings ?? []).Where(w => w.Code != WarningCodes.NotUndoable));
        return ToolResult.Ok(
            $"Humanized {plan.Notes.Count} notes of {part.Id} ({part.Name}) with {parameters.Preset}, seed {seed}: " +
            $"{built.PitchChanged} pitch, {built.VibratoChanged} vibrato, {built.ColorChanged} color, {built.BreathsAdded} breaths added, {built.CurvesChanged} curves. One undo step.",
            data, warnings, new JsonObject {
                ["epoch"] = result.Epoch,
                ["partRev"] = new JsonObject(result.PartRevs.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            });
    }

    internal static Section ToSection(ProjectContext ctx, PartDto part, SectionInput s) {
        if (!Roles.All.Contains(s.Role)) {
            throw new ToolInputException(ToolInputException.InvalidParams, $"Section '{s.Name}': unknown role '{s.Role}'. Use {string.Join(", ", Roles.All)}.");
        }
        var (from, to) = NotesGetTool.ResolveRange(ctx.Time, new RangeInput(s.From, s.To, s.Bars), part);
        return new Section(s.Name, s.Role, from, to);
    }

    /// <summary>Humanization parameters from a preset, intensity and per-layer overrides (shared with variations).</summary>
    internal static HumanizeParams Parameters(string preset, double? intensity, JsonElement? layers, string[]? preserve) {
        try {
            var parameters = HumanizeParams.FromPreset(preset);
            if (intensity is double i) {
                if (i is < 0 or > 2) {
                    throw new ArgumentException("intensity must be between 0 and 2.");
                }
                parameters = parameters with { Intensity = i };
            }
            var overrides = layers is { ValueKind: JsonValueKind.Object } l ? JsonNode.Parse(l.GetRawText())!.AsObject() : null;
            return parameters.With(overrides, preserve);
        } catch (ArgumentException e) {
            throw new ToolInputException(ToolInputException.InvalidParams, e.Message);
        }
    }
}
