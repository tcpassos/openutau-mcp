using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Protocol;
using OpenUtauMcp.Server.Bridge;

namespace OpenUtauMcp.Server.Tools.Core;

[McpServerToolType]
public static class CurvesGetTool {
    const int Grid = ProtocolInfo.CurveGridTicks;
    const string FinalPitch = "final_pitch";
    const string RealPrefix = "real:";

    [McpServerTool(Name = "ou_curves_get", Title = "Read expression curves", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Reads expression curves of one part. By default only a summary per curve (points, min, max, mean over the range); " +
                 "points:true adds the points (decimated to maxPoints) as [bar:beat, value]; per=note or per=phrase gives min/max/mean " +
                 "for each note or phrase. Values outside a curve's points are its default. " +
                 "Example: {\"part\":\"p3\",\"curves\":[\"dyn\",\"pitd\"],\"range\":{\"bars\":\"17-24\"},\"per\":\"phrase\"}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge,
        [Description("Part id or exact name.")] string part,
        [Description("Curves to read (default: every curve with data). final_pitch = the pitch sent to the renderer (notes, portamento, vibrato, pitd), per note as cents of deviation; real:<abbr> = the curve the renderer actually produced (after ou_batch_edit refresh_real_curves).")] string[]? curves = null,
        [Description("Only this range.")] RangeInput? range = null,
        [Description("Include the points.")] bool points = false,
        [Description("Maximum points per curve when points=true.")] int maxPoints = 200,
        [Description("none (default) | note | phrase")] string per = "none",
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            if (per is not ("none" or "note" or "phrase")) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"Unknown per '{per}'. Use none, note or phrase.");
            }
            var ctx = await ProjectContext.LoadAsync(bridge, ct).ConfigureAwait(false);
            var p = ctx.ResolvePart(part);
            var (absFrom, absTo) = range is null ? (p.Pos, p.Pos + p.Dur) : NotesGetTool.ResolveRange(ctx.Time, range, p);
            int from = absFrom - p.Pos, to = absTo - p.Pos;
            bool finalPitch = curves?.Contains(FinalPitch) == true;
            var realAbbrs = (curves ?? []).Where(c => c.StartsWith(RealPrefix, StringComparison.Ordinal)).Select(c => c[RealPrefix.Length..]).ToHashSet();
            var plainAbbrs = curves?.Where(c => c != FinalPitch && !c.StartsWith(RealPrefix, StringComparison.Ordinal)).ToHashSet();
            var queried = curves is null ? null : plainAbbrs!.Union(realAbbrs).ToArray();
            var result = queried is { Length: 0 }
                ? new CurvesQueryResult { PartId = p.Id, PartPos = p.Pos, PartRev = p.PartRev, Epoch = ctx.Project.Epoch, Curves = [] }
                : await bridge.CurvesQueryAsync(new CurvesQueryParams { PartId = p.Id, Abbrs = queried, Real = realAbbrs.Count > 0 }, ct)
                    .ConfigureAwait(false);

            IReadOnlyList<NoteDto> notes = [];
            if (per != "none" || finalPitch) {
                notes = (await bridge.AllNotesAsync(p.Id, null, ct).ConfigureAwait(false)).Notes
                    .Where(n => n.Pos >= from && n.Pos < to && !Phrases.IsBreath(n)).ToList();
            }
            // real:<abbr> = what the renderer produced (OpenUtau's "real curves"), read like a user curve.
            var shown = new List<(string name, CurveDto curve)>();
            foreach (var curve in result.Curves) {
                if (plainAbbrs is null || plainAbbrs.Contains(curve.Abbr)) {
                    shown.Add((curve.Abbr, curve));
                }
                if (realAbbrs.Contains(curve.Abbr)) {
                    shown.Add((RealPrefix + curve.Abbr, curve with { Xs = curve.RealXs ?? [], Ys = curve.RealYs ?? [] }));
                }
            }
            var list = new JsonArray();
            foreach (var (name, curve) in shown) {
                if (name.StartsWith(RealPrefix, StringComparison.Ordinal) && curve.Xs.Count == 0) {
                    list.Add(new JsonObject {
                        ["curve"] = name, ["points"] = 0,
                        ["note"] = "No real curve yet: run ou_batch_edit refresh_real_curves on this part (the renderer must support it).",
                    });
                    continue;
                }
                var entry = new JsonObject {
                    ["curve"] = name,
                    ["default"] = curve.DefaultValue,
                    ["range"] = new JsonArray(curve.Min, curve.Max),
                    ["points"] = curve.Xs.Count(x => x >= from && x <= to),
                };
                var stats = Stats(curve, from, to);
                entry["min"] = stats.min;
                entry["max"] = stats.max;
                entry["mean"] = stats.mean;
                if (points) {
                    var inRange = Enumerable.Range(0, curve.Xs.Count).Where(i => curve.Xs[i] >= from && curve.Xs[i] <= to).ToList();
                    int step = Math.Max(1, (int)Math.Ceiling(inRange.Count / (double)Math.Clamp(maxPoints, 2, 5000)));
                    entry["values"] = new JsonArray(inRange.Where((_, k) => k % step == 0)
                        .Select(i => (JsonNode?)new JsonArray(ctx.Time.FormatPosition(p.Pos + curve.Xs[i]), curve.Ys[i])).ToArray());
                }
                if (per == "note") {
                    entry["perNote"] = new JsonArray(notes.Select(n => {
                        var s = Stats(curve, n.Pos, n.Pos + n.Dur);
                        return (JsonNode?)new JsonArray(n.Id, n.Lyric, s.min, s.max, s.mean);
                    }).ToArray());
                    entry["perNoteCols"] = new JsonArray("id", "lyric", "min", "max", "mean");
                } else if (per == "phrase") {
                    var (phrases, _) = Phrases.Detect(notes);
                    entry["perPhrase"] = new JsonArray(phrases.Select(ph => {
                        var s = Stats(curve, ph.Notes[0].Pos, ph.Notes[^1].Pos + ph.Notes[^1].Dur);
                        return (JsonNode?)new JsonArray(ph.Number, ph.Text, s.min, s.max, s.mean);
                    }).ToArray());
                    entry["perPhraseCols"] = new JsonArray("n", "text", "min", "max", "mean");
                }
                list.Add(entry);
            }
            IReadOnlyList<Contracts.Errors.Warning>? warnings = null;
            if (finalPitch) {
                var phrases = await bridge.PhrasesQueryAsync(new PhrasesQueryParams {
                    PartId = p.Id, Range = new TickRange(absFrom, absTo), Series = ["pitch"], AwaitMs = 10000,
                }, ct).ConfigureAwait(false);
                warnings = phrases.Warnings;
                list.Add(FinalPitchEntry(ctx, p, phrases, notes, absFrom, absTo, points, maxPoints, per));
            }
            var data = new JsonObject {
                ["part"] = p.Id, ["partRev"] = result.PartRev,
                ["from"] = ctx.Time.FormatPosition(absFrom), ["to"] = ctx.Time.FormatPosition(absTo),
                ["curves"] = list,
            };
            string names = shown.Count == 0 ? "no curves with data" : string.Join(", ", shown.Select(s => s.name));
            if (finalPitch) {
                names = shown.Count == 0 ? FinalPitch : $"{names}, {FinalPitch}";
            }
            return ToolResult.Ok($"{p.Id} ({p.Name}), {ctx.Time.FormatPosition(absFrom)} to {ctx.Time.FormatPosition(absTo)}: {names}.", data, warnings);
        });

    /// <summary>
    /// The pitch OpenUtau sends to the renderer (notes, portamento, vibrato, pitd), from its render phrases.
    /// Per note: deviation in cents from the written pitch (tone + tuning): mean, min, max, at the start and at the end.
    /// </summary>
    static JsonObject FinalPitchEntry(ProjectContext ctx, PartDto part, PhrasesQueryResult phrases, IReadOnlyList<NoteDto> notes,
                                      int absFrom, int absTo, bool points, int maxPoints, string per) {
        float? At(int tick) {
            foreach (var ph in phrases.Phrases) {
                if (!ph.Series.TryGetValue("pitch", out var s) || tick < ph.SeriesStartTick) {
                    continue;
                }
                int i = (tick - ph.SeriesStartTick) / phrases.StepTicks;
                if (i < s.Count && tick < ph.EndTick) {
                    return s[i];
                }
            }
            return null;
        }
        var entry = new JsonObject {
            ["curve"] = FinalPitch,
            ["unit"] = "cents (MIDI x 100)",
            ["phrases"] = phrases.Phrases.Count,
        };
        if (points) {
            var samples = new List<(int tick, float cents)>();
            foreach (var ph in phrases.Phrases) {
                var s = ph.Series.GetValueOrDefault("pitch") ?? [];
                for (int i = 0; i < s.Count; i++) {
                    int tick = ph.SeriesStartTick + i * phrases.StepTicks;
                    if (tick >= absFrom && tick < absTo && tick >= ph.StartTick && tick < ph.EndTick) {
                        samples.Add((tick, s[i]));
                    }
                }
            }
            int step = Math.Max(1, (int)Math.Ceiling(samples.Count / (double)Math.Clamp(maxPoints, 2, 5000)));
            entry["values"] = new JsonArray(samples.Where((_, k) => k % step == 0)
                .Select(x => (JsonNode?)new JsonArray(ctx.Time.FormatPosition(x.tick), Math.Round(x.cents, 1))).ToArray());
        }
        if (per != "phrase") {
            var rows = new JsonArray();
            foreach (var n in notes) {
                int start = part.Pos + n.Pos, end = start + n.Dur;
                double target = n.Tone * 100 + n.Tuning;
                var devs = new List<double>();
                for (int t = start; t < end; t += Grid) {
                    if (At(t) is float c) {
                        devs.Add(c - target);
                    }
                }
                if (devs.Count == 0) {
                    rows.Add(new JsonArray(n.Id, n.Lyric, null, null, null, null, null));
                    continue;
                }
                int edge = Math.Max(1, devs.Count / 10);
                rows.Add(new JsonArray(n.Id, n.Lyric, Math.Round(devs.Average()), Math.Round(devs.Min()), Math.Round(devs.Max()),
                    Math.Round(devs.Take(edge).Average()), Math.Round(devs.TakeLast(edge).Average())));
            }
            entry["perNoteCols"] = new JsonArray("id", "lyric", "meanDev", "minDev", "maxDev", "startDev", "endDev");
            entry["perNote"] = rows;
        } else {
            entry["perPhraseCols"] = new JsonArray("from", "to", "notes", "lowest", "highest");
            entry["perPhrase"] = new JsonArray(phrases.Phrases.Select(ph => {
                var s = ph.Series.GetValueOrDefault("pitch") ?? [];
                int skip = Math.Max(0, (ph.StartTick - ph.SeriesStartTick) / phrases.StepTicks);
                var body = s.Skip(skip).ToList();
                return (JsonNode?)new JsonArray(ctx.Time.FormatPosition(Math.Max(0, ph.StartTick)), ctx.Time.FormatPosition(ph.EndTick), ph.NoteIds.Count,
                    body.Count > 0 ? CentsName(body.Min()) : null, body.Count > 0 ? CentsName(body.Max()) : null);
            }).ToArray());
        }
        return entry;
    }

    static string CentsName(float cents) {
        int midi = (int)Math.Round(cents / 100);
        int rest = (int)Math.Round(cents - midi * 100);
        return rest == 0 ? Units.Pitches.Format(midi) : $"{Units.Pitches.Format(midi)}{rest:+0;-0}c";
    }

    /// <summary>Min/max/mean of the curve over [from, to), sampled on the 5-tick grid as OpenUtau reads it.</summary>
    internal static (int min, int max, double mean) Stats(CurveDto curve, int from, int to) {
        int min = int.MaxValue, max = int.MinValue;
        long sum = 0;
        int count = 0;
        int step = Math.Max(Grid, (to - from) / 400 / Grid * Grid);
        for (int x = from / Grid * Grid; x < Math.Max(to, from + 1); x += step) {
            int y = Sample(curve, x);
            min = Math.Min(min, y);
            max = Math.Max(max, y);
            sum += y;
            count++;
        }
        return count == 0 ? (0, 0, 0) : (min, max, Math.Round(sum / (double)count, 1));
    }

    internal static int Sample(CurveDto curve, int x) {
        int idx = curve.Xs is List<int> list ? list.BinarySearch(x) : curve.Xs.ToList().BinarySearch(x);
        if (idx >= 0) {
            return curve.Ys[idx];
        }
        idx = ~idx;
        if (idx == 0 || idx == curve.Xs.Count) {
            return (int)curve.DefaultValue;
        }
        double t = (x - curve.Xs[idx - 1]) / (double)(curve.Xs[idx] - curve.Xs[idx - 1]);
        return (int)Math.Round(curve.Ys[idx - 1] + t * (curve.Ys[idx] - curve.Ys[idx - 1]));
    }
}
