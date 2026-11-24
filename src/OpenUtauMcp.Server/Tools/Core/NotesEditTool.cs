using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Server.Bridge;
using OpenUtauMcp.Server.Units;

namespace OpenUtauMcp.Server.Tools.Core;

public sealed record NoteFilter(
    [property: Description("Note ids.")] string[]? Ids = null,
    [property: Description("Notes starting in this range.")] RangeInput? Range = null,
    [property: Description("Phrase numbers (as listed by ou_notes_get).")] int[]? Phrases = null,
    [property: Description("Lyric regex.")] string? Lyric = null);

public sealed record NoteAddInput(
    [property: Description("Start, absolute bar:beat, e.g. \"17:1\".")] string At,
    [property: Description("Duration: \"0.5q\", \"1/8\", \"1/4.\" or ticks.")] string Dur,
    [property: Description("Pitch, e.g. \"A4\".")] string Pitch,
    [property: Description("Lyric (default \"a\"). \"+\" continues the previous word; \"AP\" is a breath.")] string Lyric = "a",
    [property: Description("Fine tuning in cents.")] int? Tuning = null,
    [property: Description("Vibrato {length %, period ms, depth cents, in %, out %, shift %, drift %, volLink %} or \"off\".")] JsonElement? Vibrato = null,
    [property: Description("Pitch points [[x ms, y cents, shape io|l|i|o|sp], ...]; omit for the default portamento.")] JsonElement? PitchPoints = null,
    [property: Description("With pitchPoints: snap the first point to the previous note.")] bool SnapFirst = true,
    [property: Description("Per-note expressions, same value for every phoneme, e.g. {\"clr\":1,\"vel\":120}.")] Dictionary<string, double>? Expr = null);

public sealed record NoteUpdateInput(
    [property: Description("Note id from ou_notes_get, e.g. \"n12\". Or use select.")] string? Id = null,
    [property: Description("Same change for every note matching this filter.")] NoteFilter? Select = null,
    [property: Description("New start position, absolute bar:beat, e.g. \"17:2.5\".")] string? At = null,
    [property: Description("New duration, e.g. \"0.5q\", \"1/8\", \"1/4.\".")] string? Dur = null,
    [property: Description("New pitch, e.g. \"A4\", \"C#5\", or MIDI number.")] string? Pitch = null,
    [property: Description("Transpose by semitones (relative). Ignored when pitch is set.")] int? Transpose = null,
    [property: Description("New lyric. \"+\" continues the previous word; \"AP\" is a breath.")] string? Lyric = null,
    [property: Description("Fine tuning in cents.")] int? Tuning = null,
    [property: Description("Vibrato: partial {length, period, depth, in, out, shift, drift, volLink}, \"off\" or \"default\".")] JsonElement? Vibrato = null,
    [property: Description("[[x ms, y cents, shape], ...] or \"default\".")] JsonElement? PitchPoints = null,
    [property: Description("With pitchPoints: snap the first point to the previous note.")] bool? SnapFirst = null,
    [property: Description("Per-note expressions: {abbr: number | [one per phoneme] | null (default)}.")] Dictionary<string, JsonElement>? Expr = null,
    [property: Description("Phonemizer for this note (id or class name), or \"default\".")] string? Phonemizer = null,
    [property: Description("Phoneme overrides [{index, alias?, offsetTicks?, preutterDeltaMs?, overlapDeltaMs?}] or \"clear\"; not with a lyric change.")] JsonElement? Phonemes = null,
    [property: Description("Reset the note's expressions first.")] bool ResetExprs = false);

[McpServerToolType]
public static class NotesEditTool {
    [McpServerTool(Name = "ou_notes_edit", Title = "Add, update or remove notes (one undo step)", ReadOnly = false, Idempotent = false, OpenWorld = false, Destructive = true)]
    [Description("Adds, updates and removes notes of one part in one undo step; all or nothing. update takes an id or a select filter and changes only the given fields. ifRev refuses if the part changed. Example: {\"part\":\"p3\",\"update\":[{\"id\":\"n12\",\"lyric\":\"crown\",\"vibrato\":{\"depth\":30}}],\"add\":[{\"at\":\"18:1\",\"dur\":\"1q\",\"pitch\":\"D5\",\"lyric\":\"oh\"}]}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge,
        [Description("Part id or exact name.")] string part,
        [Description("Notes to create.")] NoteAddInput[]? add = null,
        [Description("Note updates.")] NoteUpdateInput[]? update = null,
        [Description("Notes to delete.")] NoteFilter? remove = null,
        [Description("Extend the part when an added note ends after it (default true).")] bool growPart = true,
        [Description("error (default): refuse edits that make notes overlap; allow: accept with a warning.")] string overlap = "error",
        [Description("Only validate; change nothing.")] bool dryRun = false,
        [Description("Expected partRev of the part (from ou_notes_get); refuses with CONFLICT if it changed.")] int? ifRev = null,
        [Description("Undo step name (English).")] string? undoName = null,
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            if (add is not { Length: > 0 } && update is not { Length: > 0 } && remove is null) {
                throw new ToolInputException(ToolInputException.InvalidParams, "Nothing to do: give add, update or remove.");
            }
            if (overlap is not ("error" or "allow")) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"Unknown overlap '{overlap}'. Use error or allow.");
            }
            var ctx = await ProjectContext.LoadAsync(bridge, ct).ConfigureAwait(false);
            var p = ctx.ResolvePart(part);

            bool needNotes = remove is not null || (update ?? []).Any(u => u.Select is not null || u.Transpose is not null && u.Pitch is null);
            var notes = needNotes ? (await bridge.AllNotesAsync(p.Id, null, ct).ConfigureAwait(false)).Notes : [];
            var byId = notes.ToDictionary(n => n.Id);

            var ops = new List<EditOp>();
            var exprAbbrs = new List<string>();
            int updatedNotes = 0, removedNotes = 0;

            for (int i = 0; i < (add?.Length ?? 0); i++) {
                ops.Add(ToAddOp(ctx, p, add![i], i, growPart, exprAbbrs));
            }
            for (int i = 0; i < (update?.Length ?? 0); i++) {
                var u = update![i];
                var ids = TargetIds(ctx, p, notes, u, i);
                updatedNotes += ids.Count;
                foreach (var id in ids) {
                    ops.AddRange(ToUpdateOps(ctx, p, u, id, i, byId, exprAbbrs));
                }
            }
            if (remove is not null) {
                var ids = NotesGetTool.Filter(notes, p, ctx.Time, remove.Range, remove.Ids, remove.Phrases, remove.Lyric).Select(n => n.Id).ToList();
                if (remove.Ids is { Length: > 0 } explicitIds && explicitIds.Except(ids).FirstOrDefault() is { } missing) {
                    throw new ToolInputException(ErrorCodes.NotFound, $"remove: note {missing} is not in part {p.Id}.", "Re-read the notes with ou_notes_get.");
                }
                if (ids.Count > 0) {
                    ops.Add(new NoteRemoveOp { NoteIds = ids });
                    removedNotes = ids.Count;
                }
            }
            if (ops.Count == 0) {
                return ToolResult.Ok("No notes matched; nothing changed.", new JsonObject { ["changed"] = new JsonObject() });
            }

            var result = await bridge.EditApplyAsync(new EditApplyParams {
                UndoName = undoName ?? UndoName(add?.Length ?? 0, updatedNotes, removedNotes),
                IfRev = ifRev is int rev ? new Dictionary<string, int> { [p.Id] = rev } : null,
                DryRun = dryRun,
                AllowOverlap = overlap == "allow",
                Ops = ops,
            }, ct).ConfigureAwait(false);

            var warnings = (result.Warnings ?? []).Concat(await ExpressionEffects.WarningsAsync(bridge, p, exprAbbrs, ct).ConfigureAwait(false));
            var extra = new JsonObject {
                ["epoch"] = result.Epoch,
                ["partRev"] = new JsonObject(result.PartRevs.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            };
            var created = (result.Created ?? new Dictionary<string, string>())
                .Where(kv => kv.Key.StartsWith("@n", StringComparison.Ordinal))
                .OrderBy(kv => int.Parse(kv.Key.AsSpan(2), CultureInfo.InvariantCulture))
                .Select(kv => (JsonNode?)kv.Value).ToArray();
            var changed = new JsonObject();
            if (created.Length > 0 || dryRun && add is { Length: > 0 }) {
                changed["created"] = dryRun ? add!.Length : new JsonArray(created);
            }
            if (updatedNotes > 0) {
                changed["updated"] = updatedNotes;
            }
            if (removedNotes > 0) {
                changed["removed"] = removedNotes;
            }
            string what = string.Join(", ", new[] {
                add is { Length: > 0 } ? $"{add.Length} added" : null,
                updatedNotes > 0 ? $"{updatedNotes} updated" : null,
                removedNotes > 0 ? $"{removedNotes} removed" : null,
            }.Where(s => s is not null));
            string summary = dryRun
                ? $"Dry run: {what} in {p.Id} ({p.Name}) would succeed; nothing changed."
                : $"Notes {what} in {p.Id} ({p.Name}). Undo with ou_history or Ctrl+Z in OpenUtau.";
            return ToolResult.Ok(summary, dryRun ? new JsonObject { ["plan"] = new JsonObject { ["ops"] = ops.Count, ["changes"] = changed } }
                : new JsonObject { ["changed"] = changed }, warnings, extra);
        });

    static string UndoName(int added, int updated, int removed) {
        var parts = new[] {
            added > 0 ? $"add {added}" : null, updated > 0 ? $"edit {updated}" : null, removed > 0 ? $"remove {removed}" : null,
        }.Where(s => s is not null);
        int total = added + updated + removed;
        return $"MCP: {string.Join(", ", parts)} note{(total == 1 ? "" : "s")}";
    }

    static NoteAddOp ToAddOp(ProjectContext ctx, PartDto part, NoteAddInput a, int i, bool growPart, List<string> exprAbbrs) {
        int abs = ctx.Time.ParsePosition(a.At);
        int pos = abs - part.Pos;
        if (pos < 0) {
            throw new ToolInputException(ToolInputException.OutsidePart,
                $"add[{i}]: {a.At} is before the start of part {part.Id} ({ctx.Time.FormatPosition(part.Pos)}).");
        }
        VibratoPatch? vibrato = null;
        if (a.Vibrato is { } v) {
            vibrato = ParseVibrato(v, $"add[{i}].vibrato", out bool reset);
            if (reset) {
                vibrato = null;  // "default" on a new note = what CreateNote already gives
            }
        }
        var exprs = a.Expr?.ToDictionary(kv => kv.Key, kv => (float)kv.Value);
        if (exprs is not null) {
            exprAbbrs.AddRange(exprs.Keys);
        }
        return new NoteAddOp {
            PartId = part.Id,
            Pos = pos,
            Dur = Durations.Parse(a.Dur),
            Tone = Pitches.Parse(a.Pitch),
            Lyric = a.Lyric,
            Tuning = a.Tuning,
            Ref = $"@n{i}",
            Vibrato = vibrato,
            Pitch = a.PitchPoints is { } pts ? ParsePitch(pts, a.SnapFirst, $"add[{i}].pitchPoints") : null,
            Exprs = exprs,
            GrowPart = growPart,
        };
    }

    static List<string> TargetIds(ProjectContext ctx, PartDto part, IReadOnlyList<NoteDto> notes, NoteUpdateInput u, int i) {
        if ((u.Id is null) == (u.Select is null)) {
            throw new ToolInputException(ToolInputException.InvalidParams, $"update[{i}]: give exactly one of 'id' or 'select'.");
        }
        if (u.Id is { } id) {
            return [id];
        }
        var s = u.Select!;
        return NotesGetTool.Filter(notes, part, ctx.Time, s.Range, s.Ids, s.Phrases, s.Lyric).Select(n => n.Id).ToList();
    }

    static IEnumerable<EditOp> ToUpdateOps(ProjectContext ctx, PartDto part, NoteUpdateInput u, string id, int i,
                                           Dictionary<string, NoteDto> current, List<string> exprAbbrs) {
        string at = $"update[{i}]";
        int? pos = null;
        if (u.At is { } position) {
            int abs = ctx.Time.ParsePosition(position);
            pos = abs - part.Pos;
            if (pos < 0 || abs >= part.Pos + part.Dur) {
                throw new ToolInputException(ToolInputException.OutsidePart,
                    $"{at}: position {position} is outside part {part.Id} ({ctx.Time.FormatPosition(part.Pos)} to {ctx.Time.FormatPosition(part.Pos + part.Dur)}).",
                    "Choose a position inside the part.");
            }
        }
        int? tone = u.Pitch is { } pitch ? Pitches.Parse(pitch) : null;
        if (tone is null && u.Transpose is int semitones && semitones != 0) {
            if (!current.TryGetValue(id, out var note)) {
                throw new ToolInputException(ErrorCodes.NotFound, $"{at}: note {id} is not in part {part.Id}.", "Re-read the notes with ou_notes_get.");
            }
            tone = note.Tone + semitones;
            if (tone is < 0 or > 127) {
                throw new UnitException(UnitException.BadPitch, $"{at}: transposing {id} by {semitones} leaves the MIDI range.");
            }
        }
        int? dur = u.Dur is { } d ? Durations.Parse(d) : null;
        if (pos is not null || dur is not null || tone is not null || u.Lyric is not null || u.Tuning is not null) {
            yield return new NoteUpdateOp { NoteId = id, Pos = pos, Dur = dur, Tone = tone, Lyric = u.Lyric, Tuning = u.Tuning };
        }
        if (u.ResetExprs) {
            yield return new NoteResetExprsOp { NoteIds = [id] };
        }
        if (u.Phonemizer is { } phonemizer) {
            yield return new NotePhonemizerOp { NoteIds = [id], Phonemizer = phonemizer == "default" ? null : phonemizer };
        }
        if (u.Phonemes is { } phonemes) {
            yield return new NotePhonemesOp { NoteId = id, Overrides = ParsePhonemes(phonemes, $"{at}.phonemes") };
        }
        if (u.Vibrato is { } v) {
            var patch = ParseVibrato(v, $"{at}.vibrato", out bool reset);
            yield return new NoteVibratoOp { NoteIds = [id], Vibrato = reset ? null : patch, Reset = reset };
        }
        if (u.PitchPoints is { } pts) {
            yield return new NotePitchOp {
                NoteIds = [id],
                Pitch = pts.ValueKind == JsonValueKind.String && pts.GetString() == "default"
                    ? null : ParsePitch(pts, u.SnapFirst ?? true, $"{at}.pitchPoints"),
            };
        }
        foreach (var (abbr, value) in u.Expr ?? []) {
            exprAbbrs.Add(abbr);
            yield return value.ValueKind switch {
                JsonValueKind.Number => new NoteExprOp { NoteIds = [id], Abbr = abbr, Value = (float)value.GetDouble() },
                JsonValueKind.Null => new NoteExprOp { NoteIds = [id], Abbr = abbr },
                JsonValueKind.Array => new NoteExprOp {
                    NoteIds = [id], Abbr = abbr,
                    Values = value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Null ? (float?)null : (float)x.GetDouble()).ToList(),
                },
                _ => throw new ToolInputException(ToolInputException.InvalidParams, $"{at}.expr.{abbr}: use a number, a list of numbers or null."),
            };
        }
    }

    /// <summary>"clear" = null (remove overrides); array = overrides by phoneme index.</summary>
    static List<PhonemeOverrideDto>? ParsePhonemes(JsonElement v, string at) {
        if (v.ValueKind == JsonValueKind.String && v.GetString() == "clear") {
            return null;
        }
        if (v.ValueKind != JsonValueKind.Array) {
            throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: use [{{index, alias?, offsetTicks?, preutterDeltaMs?, overlapDeltaMs?}}] or \"clear\".");
        }
        var known = new HashSet<string> { "index", "alias", "offsetTicks", "preutterDeltaMs", "overlapDeltaMs" };
        var list = new List<PhonemeOverrideDto>();
        foreach (var item in v.EnumerateArray()) {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: each item needs an integer 'index'.");
            }
            foreach (var prop in item.EnumerateObject()) {
                if (!known.Contains(prop.Name)) {
                    throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: unknown field '{prop.Name}'.");
                }
            }
            float? F(string name) => item.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number ? (float)x.GetDouble() : null;
            list.Add(new PhonemeOverrideDto {
                Index = index.GetInt32(),
                Alias = item.TryGetProperty("alias", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null,
                Offset = item.TryGetProperty("offsetTicks", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : null,
                PreutterDeltaMs = F("preutterDeltaMs"),
                OverlapDeltaMs = F("overlapDeltaMs"),
            });
        }
        return list;
    }

    /// <summary>"off" = length 0; "default" = reset; object = partial patch.</summary>
    static VibratoPatch ParseVibrato(JsonElement v, string at, out bool reset) {
        reset = false;
        if (v.ValueKind == JsonValueKind.String) {
            switch (v.GetString()) {
                case "off": return new VibratoPatch { Length = 0 };
                case "default": reset = true; return new VibratoPatch();
            }
        }
        if (v.ValueKind != JsonValueKind.Object) {
            throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: use an object, \"off\" or \"default\".");
        }
        float? F(string name) => v.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number ? (float)x.GetDouble() : null;
        var known = new HashSet<string> { "length", "period", "depth", "in", "out", "shift", "drift", "volLink" };
        foreach (var prop in v.EnumerateObject()) {
            if (!known.Contains(prop.Name)) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: unknown field '{prop.Name}'.");
            }
        }
        return new VibratoPatch {
            Length = F("length"), Period = F("period"), Depth = F("depth"), In = F("in"), Out = F("out"),
            Shift = F("shift"), Drift = F("drift"), VolLink = F("volLink"),
        };
    }

    /// <summary>[[x ms, y cents, shape?], ...] to protocol points (y in tenths of a semitone).</summary>
    static PitchSpec ParsePitch(JsonElement points, bool snapFirst, string at) {
        if (points.ValueKind != JsonValueKind.Array) {
            throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: use [[x ms, y cents, shape], ...] or \"default\".");
        }
        var list = new List<PitchPointDto>();
        foreach (var p in points.EnumerateArray()) {
            if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() is < 2 or > 3
                || p[0].ValueKind != JsonValueKind.Number || p[1].ValueKind != JsonValueKind.Number) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: each point is [x ms, y cents] or [x ms, y cents, shape].");
            }
            string shape = p.GetArrayLength() == 3 ? p[2].GetString() ?? "io" : "io";
            list.Add(new PitchPointDto((float)p[0].GetDouble(), (float)(p[1].GetDouble() / 10), shape));
        }
        return new PitchSpec { Points = list, SnapFirst = snapFirst };
    }
}
