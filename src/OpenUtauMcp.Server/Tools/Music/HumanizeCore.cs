using System.Globalization;
using System.Text.Json.Nodes;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Music.Humanize;

namespace OpenUtauMcp.Server.Tools.Music;

/// <summary>
/// Turns a humanization plan into edit ops. Shared by ou_music_humanize (ops on the part itself), ou_music_compose
/// (ops on notes created earlier in the same plan, addressed by refs) and ou_music_variations (ops on a duplicated
/// part, addressed by its note refs). Only what differs from the current state is written.
/// </summary>
public static class HumanizeCore {
    public sealed record Target {
        /// <summary>Part the ops write to: an id, or a ref created earlier in the plan.</summary>
        public required string PartId { get; init; }
        /// <summary>Maps a planned note id to the id/ref the ops use (identity for the part itself).</summary>
        public Func<string, string> NoteRef { get; init; } = id => id;
        public IReadOnlyCollection<string> RegisteredExpressions { get; init; } = [];
        public required string TrackIdForEnsure { get; init; }
        /// <summary>For a track created in the same plan: take expression suggestions from its singer.</summary>
        public string? SingerIdForEnsure { get; init; }
    }

    public sealed record Result {
        public required List<EditOp> Ops { get; init; }
        public required JsonObject Changed { get; init; }
        public required int PitchChanged { get; init; }
        public required int VibratoChanged { get; init; }
        public required int ColorChanged { get; init; }
        public required int BreathsAdded { get; init; }
        public required int CurvesChanged { get; init; }
        public required int From { get; init; }
        public required int To { get; init; }
    }

    /// <param name="current">Current notes (state to compare with); notes created in the same plan have default pitch/vibrato.</param>
    /// <param name="currentCurves">Current curves of the part; empty for a new part.</param>
    public static Result BuildOps(HumanizeResult plan, IReadOnlyList<NoteDto> current, IReadOnlyDictionary<string, CurveDto> currentCurves,
                                  int partDur, bool wholePart, Target target) {
        var byId = current.ToDictionary(n => n.Id);
        var ops = new List<EditOp>();
        int pitchChanged = 0, vibratoChanged = 0, colorChanged = 0;

        foreach (var np in plan.Notes) {
            var note = byId[np.Id];
            string id = target.NoteRef(np.Id);
            if (np.Pitch is { } pitch) {
                bool scoop = pitch.Count > 0 && pitch[0].X == 0 && pitch[0].Y < 0;
                // OpenUtau's snapFirst zeroes the first point after a rest, which would erase the scoop.
                var spec = new PitchSpec {
                    Points = pitch.Select(pt => new PitchPointDto((float)pt.X, (float)pt.Y, pt.Shape)).ToList(),
                    SnapFirst = !scoop,
                };
                if (!SamePitch(note.Pitch, spec)) {
                    ops.Add(new NotePitchOp { NoteIds = [id], Pitch = spec });
                    pitchChanged++;
                }
            }
            if (np.Vibrato is { } v && !SameVibrato(note.Vibrato, v)) {
                ops.Add(new NoteVibratoOp {
                    NoteIds = [id],
                    Vibrato = new VibratoPatch {
                        Length = (float)v.Length, Period = (float)v.Period, Depth = (float)v.Depth, In = (float)v.In,
                        Out = (float)v.Out, Shift = (float)v.Shift,
                    },
                });
                vibratoChanged++;
            }
            if (np.Color is int color && CurrentColor(note) != color) {
                ops.Add(new NoteExprOp { NoteIds = [id], Abbr = "clr", Value = color });
                colorChanged++;
            }
        }

        // Breaths: keep identical ones, replace the rest.
        var existing = plan.BreathsToRemove.Select(id => byId[id]).ToList();
        var toAdd = plan.BreathsToAdd.Where(b => !existing.Any(e => e.Pos == b.Pos && e.Dur == b.Dur && e.Tone == b.Tone && e.Lyric == b.Lyric)).ToList();
        var toRemove = existing.Where(e => !plan.BreathsToAdd.Any(b => e.Pos == b.Pos && e.Dur == b.Dur && e.Tone == b.Tone && e.Lyric == b.Lyric))
            .Select(e => target.NoteRef(e.Id)).ToList();
        if (toRemove.Count > 0) {
            ops.Add(new NoteRemoveOp { NoteIds = toRemove });
        }
        foreach (var b in toAdd) {
            ops.Add(new NoteAddOp { PartId = target.PartId, Pos = b.Pos, Dur = b.Dur, Tone = b.Tone, Lyric = b.Lyric, GrowPart = false });
        }

        // Curves: rewrite the cropped range only where it differs.
        int from = plan.RangeFrom;
        int to = wholePart ? Math.Max(plan.RangeTo, partDur / Humanizer.Grid * Humanizer.Grid) : plan.RangeTo;
        int curvesChanged = 0;
        if (plan.Curves.Count > 0 && to > from) {
            var ensure = plan.Curves.Keys.Where(a => !target.RegisteredExpressions.Contains(a)).ToList();
            if (ensure.Count > 0) {
                ops.Insert(0, target.SingerIdForEnsure is { } singerId
                    ? new ProjectExpressionsOp { Ensure = ensure, SuggestedForSinger = singerId }
                    : new ProjectExpressionsOp { Ensure = ensure, SuggestedFor = target.TrackIdForEnsure });
            }
            foreach (var (abbr, points) in plan.Curves) {
                var now = currentCurves.GetValueOrDefault(abbr);
                var nowInRange = now is null ? [] : now.Xs.Zip(now.Ys).Where(p => p.First >= from && p.First <= to).ToList();
                if (nowInRange.SequenceEqual(points.Select(kv => (kv.Key, kv.Value)))) {
                    continue;
                }
                ops.Add(new CurveSetRangeOp {
                    PartId = target.PartId, Abbr = abbr, FromTick = from, ToTick = to, Xs = points.Keys.ToList(), Ys = points.Values.ToList(),
                });
                curvesChanged++;
            }
        }
        return new Result {
            Ops = ops,
            Changed = new JsonObject {
                ["pitch"] = pitchChanged, ["vibrato"] = vibratoChanged, ["color"] = colorChanged,
                ["breathsAdded"] = toAdd.Count, ["breathsRemoved"] = toRemove.Count, ["curves"] = curvesChanged,
            },
            PitchChanged = pitchChanged, VibratoChanged = vibratoChanged, ColorChanged = colorChanged,
            BreathsAdded = toAdd.Count, CurvesChanged = curvesChanged, From = from, To = to,
        };
    }

    /// <summary>The [openutau-mcp] history block written to part.comment.</summary>
    public static JsonObject History(HumanizeParams p, int seed, JsonObject? extra = null) {
        var block = new JsonObject {
            ["v"] = 1,
            ["humanize"] = new JsonObject { ["preset"] = p.Preset, ["seed"] = seed, ["intensity"] = p.Intensity, ["version"] = Humanizer.Version },
        };
        foreach (var (key, value) in extra ?? []) {
            block[key] = value?.DeepClone();
        }
        return block;
    }

    /// <summary>"PARTIAL_PHRASE: ..." -> PARTIAL_PHRASE; free text -> HUMANIZE.</summary>
    public static string WarningCode(string message) {
        int colon = message.IndexOf(':');
        string head = colon > 0 ? message[..colon] : "";
        return head.Length > 0 && head.All(c => char.IsUpper(c) || c == '_') ? head : "HUMANIZE";
    }

    /// <summary>Maps abstract color roles (normal/power/soft) and explicit overrides to the singer's color indexes.</summary>
    public static Dictionary<string, int> ColorMap(SingerInfo singer, Dictionary<string, string>? overrides) {
        var colors = singer.Colors ?? [];
        var map = new Dictionary<string, int>();
        foreach (var role in new[] { ColorRoles.Normal, ColorRoles.Power, ColorRoles.Soft }) {
            int index = colors.ToList().FindIndex(c => c.Contains(role, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) {
                map[role] = index;
            }
        }
        foreach (var (key, value) in overrides ?? []) {
            int index = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n
                : map.TryGetValue(value.ToLowerInvariant(), out int byRole) ? byRole
                : colors.ToList().FindIndex(c => c.Contains(value, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index >= colors.Count) {
                throw new ToolInputException(ToolInputException.InvalidParams,
                    $"colorBySection.{key}: no voice color '{value}' for {singer.Name}. Colors: {string.Join(", ", colors.Select((c, i) => $"{i} {c}"))}.");
            }
            map[key] = index;
        }
        return map;
    }

    public static HumanizeParams DisableIneffective(HumanizeParams p, SingerInfo singer, Dictionary<string, int> colors, List<Warning> warnings, string partId) {
        bool Effective(string abbr) => singer.Effects.TryGetValue(abbr, out var e) && e == Effects.Effective;
        if (p.Consonants.Enabled && !Effective("velc")) {
            p = p with { Consonants = p.Consonants with { Enabled = false } };
            warnings.Add(new Warning("LAYER_SKIPPED", $"consonants: 'velc' has no effect with {singer.Name}.", partId));
        }
        if (p.Formant.Enabled && !Effective("genc")) {
            p = p with { Formant = p.Formant with { Enabled = false } };
            warnings.Add(new Warning("LAYER_SKIPPED", $"formant: 'genc' has no effect with {singer.Name}.", partId));
        }
        if (p.Color.Enabled && (!Effective("clr") || colors.Count == 0)) {
            p = p with { Color = p.Color with { Enabled = false } };
            warnings.Add(new Warning("LAYER_SKIPPED", $"color: {singer.Name} has no voice colors to switch between.", partId));
        }
        return p;
    }

    static bool SamePitch(PitchDto? current, PitchSpec planned) =>
        current is not null && current.SnapFirst == planned.SnapFirst && current.Points.Count == planned.Points.Count
        && current.Points.Zip(planned.Points).All(p => Math.Abs(p.First.X - p.Second.X) < 1e-3 && Math.Abs(p.First.Y - p.Second.Y) < 1e-3
                                                       && p.First.Shape == p.Second.Shape);

    static bool SameVibrato(VibratoDto? current, Vibrato planned) {
        if (current is null) {
            return false;
        }
        if (planned.Length == 0) {
            return current.Length == 0;
        }
        return current.Length == planned.Length && current.Period == planned.Period && current.Depth == planned.Depth
               && current.In == planned.In && current.Out == planned.Out && current.Shift == planned.Shift;
    }

    static int CurrentColor(NoteDto note) =>
        (int)(note.Exprs?.FirstOrDefault(e => e.Abbr == "clr" && (e.Index ?? 0) == 0)?.Value
              ?? note.Exprs?.FirstOrDefault(e => e.Abbr == "clr")?.Value ?? 0);
}
