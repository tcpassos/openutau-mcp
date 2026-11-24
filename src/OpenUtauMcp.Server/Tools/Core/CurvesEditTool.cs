using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Contracts.Protocol;
using OpenUtauMcp.Server.Bridge;

namespace OpenUtauMcp.Server.Tools.Core;

public sealed record CurvePointInput(
    [property: Description("Absolute position, bar:beat, e.g. \"17:1\".")] string At,
    [property: Description("Value in the curve's units (dyn -240..120, pitd cents, genc/velc see ou_catalog_get).")] int Value);

public sealed record CurveOpInput(
    [property: Description("set | shape | transform | copy | clear")] string Op,
    [property: Description("Curve abbreviation: pitd, dyn, genc, velc, ene, cl01, ...")] string Curve,
    [property: Description("Points for set.")] CurvePointInput[]? Points = null,
    [property: Description("set: replace_range (default) | replace_all.")] string Mode = "replace_range",
    [property: Description("Span for set/shape/transform/copy source/clear (default: the points' span, or the whole part).")] RangeInput? Range = null,
    [property: Description("set: linear (default) | step.")] string Interp = "linear",
    [property: Description("shape: const | ramp | arc.")] string? Kind = null,
    [property: Description("shape: value at the start (const: the value).")] double? Start = null,
    [property: Description("shape ramp/arc: value at the end (default = start).")] double? End = null,
    [property: Description("shape arc: value at the peak.")] double? Peak = null,
    [property: Description("shape arc: where the peak is, 0..1 of the range (default 0.5).")] double? PeakAt = null,
    [property: Description("transform: multiply values by this (default 1).")] double? Scale = null,
    [property: Description("transform: then add this (default 0).")] double? Offset = null,
    [property: Description("transform: [min, max] limits (default: the curve's range).")] double[]? Clamp = null,
    [property: Description("copy: absolute bar:beat where the copied span starts.")] string? To = null);

[McpServerToolType]
public static class CurvesEditTool {
    const int Grid = ProtocolInfo.CurveGridTicks;

    [McpServerTool(Name = "ou_curves_edit", Title = "Write expression curves", ReadOnly = false, Idempotent = false, OpenWorld = false, Destructive = true)]
    [Description("Writes expression curves of one part in one undo step. Ops: set (points; replace_range keeps the curve outside the span), shape (const/ramp/arc, e.g. cl01 ramp 100->0 with cl02 0->100), transform (scale/offset), copy (span to another position; reads the curve as before the call), clear. Points snap to 5 ticks; values are clamped (warnings). Unregistered curves need ensure:true. Example: {\"part\":\"p3\",\"ops\":[{\"op\":\"set\",\"curve\":\"dyn\",\"points\":[{\"at\":\"17:1\",\"value\":-30},{\"at\":\"18:3\",\"value\":10}]}]}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge,
        [Description("Part id or exact name.")] string part,
        [Description("Curve operations, applied in order.")] CurveOpInput[] ops,
        [Description("Register suggested curves the project lacks (e.g. velc).")] bool ensure = false,
        [Description("Validate without changing anything.")] bool dryRun = false,
        [Description("Expected partRev of the part; refuses with CONFLICT if it changed.")] int? ifRev = null,
        [Description("Name of the undo step (English).")] string? undoName = null,
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            if (ops is not { Length: > 0 }) {
                throw new ToolInputException(ToolInputException.InvalidParams, "'ops' is empty.");
            }
            var ctx = await ProjectContext.LoadAsync(bridge, ct).ConfigureAwait(false);
            var p = ctx.ResolvePart(part);
            var plan = new List<EditOp>();
            if (ensure) {
                plan.Add(new ProjectExpressionsOp { Ensure = ops.Select(o => o.Curve).Distinct().ToList(), SuggestedFor = p.TrackId });
            }
            var reads = ops.Where(o => o.Op is "transform" or "copy").Select(o => o.Curve).Distinct().ToList();
            var current = reads.Count == 0 ? new Dictionary<string, Contracts.Model.CurveDto>()
                : (await bridge.CurvesQueryAsync(new Contracts.Model.CurvesQueryParams { PartId = p.Id, Abbrs = reads }, ct).ConfigureAwait(false))
                    .Curves.ToDictionary(c => c.Abbr);
            for (int i = 0; i < ops.Length; i++) {
                plan.Add(ToOp(ctx, p, ops[i], i, current));
            }
            var result = await bridge.EditApplyAsync(new EditApplyParams {
                UndoName = undoName ?? $"MCP: edit curve {string.Join(", ", ops.Select(o => o.Curve).Distinct())}",
                IfRev = ifRev is int rev ? new Dictionary<string, int> { [p.Id] = rev } : null,
                DryRun = dryRun,
                Ops = plan,
            }, ct).ConfigureAwait(false);
            var warnings = (result.Warnings ?? []).Concat(
                await ExpressionEffects.WarningsAsync(bridge, p, ops.Select(o => o.Curve), ct).ConfigureAwait(false));
            var extra = new JsonObject {
                ["epoch"] = result.Epoch,
                ["partRev"] = new JsonObject(result.PartRevs.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            };
            string verb = dryRun ? "would be applied (dry run)" : "applied";
            return ToolResult.Ok($"{ops.Length} curve op(s) {verb} in {p.Id} ({p.Name}).",
                new JsonObject { ["changed"] = new JsonObject { ["curveOps"] = ops.Length } }, warnings, extra);
        });

    static EditOp ToOp(ProjectContext ctx, Contracts.Model.PartDto p, CurveOpInput op, int i,
                       IReadOnlyDictionary<string, Contracts.Model.CurveDto> current) {
        string at = $"ops[{i}]";
        (int from, int to)? range = null;
        if (op.Range is { } r) {
            var (absFrom, absTo) = NotesGetTool.ResolveRange(ctx.Time, r, p);
            // Tool ranges are [from, to) (bars "17-24" ends where bar 25 starts); the bridge range is inclusive,
            // so the last grid position inside is to - Grid. A point exactly at the next bar stays untouched.
            range = (absFrom - p.Pos, absTo - p.Pos - Grid);
            if (range.Value.from < 0 || range.Value.to <= range.Value.from) {
                throw new ToolInputException(ToolInputException.OutsidePart, $"{at}: the range must start inside part {p.Id} and end after it starts.");
            }
        }
        switch (op.Op) {
            case "clear":
                return new CurveClearOp { PartId = p.Id, Abbr = op.Curve, FromTick = range?.from, ToTick = range?.to };
            case "set":
                break;
            case "shape":
                return Shape(p, op, at, range);
            case "transform":
                return Transform(p, op, at, range ?? (0, p.Dur - Grid), Read(current, op, at));
            case "copy":
                return Copy(ctx, p, op, at, range, Read(current, op, at));
            default:
                throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: unknown op '{op.Op}'. Use set, shape, transform, copy or clear.");
        }
        if (op.Points is not { Length: > 0 } points) {
            throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: 'points' is required for set.");
        }
        if (op.Interp is not ("linear" or "step")) {
            throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: interp must be linear or step.");
        }
        var pts = points.Select(pt => (x: ctx.Time.ParsePosition(pt.At) - p.Pos, y: pt.Value)).OrderBy(pt => pt.x).ToList();
        if (pts[0].x < 0) {
            throw new ToolInputException(ToolInputException.OutsidePart, $"{at}: a point is before the start of part {p.Id}.");
        }
        if (op.Interp == "step") {
            // Hold each value until one grid step before the next point.
            var stepped = new List<(int x, int y)>();
            for (int k = 0; k < pts.Count; k++) {
                stepped.Add(pts[k]);
                if (k + 1 < pts.Count && pts[k + 1].x - Grid > pts[k].x) {
                    stepped.Add((pts[k + 1].x - Grid, pts[k].y));
                }
            }
            pts = stepped;
        }
        var xs = pts.Select(pt => pt.x).ToList();
        var ys = pts.Select(pt => pt.y).ToList();
        return op.Mode switch {
            "replace_all" => new CurveReplaceOp { PartId = p.Id, Abbr = op.Curve, Xs = xs, Ys = ys },
            "replace_range" => new CurveSetRangeOp {
                PartId = p.Id, Abbr = op.Curve,
                FromTick = range?.from ?? xs[0], ToTick = range?.to ?? Math.Max(xs[^1], xs[0] + Grid),
                Xs = xs, Ys = ys,
            },
            _ => throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: mode must be replace_range or replace_all."),
        };
    }

    static Contracts.Model.CurveDto Read(IReadOnlyDictionary<string, Contracts.Model.CurveDto> current, CurveOpInput op, string at) =>
        current.TryGetValue(op.Curve, out var curve) ? curve
        : throw new ToolInputException(Contracts.Errors.ErrorCodes.UnknownExpression,
            $"{at}: '{op.Curve}' is not registered in the project, so there is nothing to {op.Op}.");

    /// <summary>const / ramp / arc over an inclusive part-relative range, as linear points on the 5-tick grid.</summary>
    internal static CurveSetRangeOp Shape(Contracts.Model.PartDto p, CurveOpInput op, string at, (int from, int to)? range) {
        if (range is not { } r) {
            throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: shape needs 'range'.");
        }
        double start = op.Start ?? throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: shape needs 'start'.");
        double end = op.End ?? start;
        var pts = new List<(int x, int y)>();
        switch (op.Kind) {
            case "const":
                pts.Add((r.from, (int)Math.Round(start)));
                pts.Add((r.to, (int)Math.Round(start)));
                break;
            case "ramp":
                pts.Add((r.from, (int)Math.Round(start)));
                pts.Add((r.to, (int)Math.Round(end)));
                break;
            case "arc": {
                double peak = op.Peak ?? throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: arc needs 'peak'.");
                double peakAt = Math.Clamp(op.PeakAt ?? 0.5, 0.05, 0.95);
                double linearAtPeak = start + (end - start) * peakAt;
                // Linear base plus a half-sine bump that reaches the peak at peakAt; one point every 60 ticks.
                int steps = Math.Max(4, (r.to - r.from) / 60);
                for (int k = 0; k <= steps; k++) {
                    double t = k / (double)steps;
                    int x = (r.from + (int)Math.Round((r.to - r.from) * t)) / Grid * Grid;
                    double bump = t <= peakAt ? Math.Sin(Math.PI / 2 * t / peakAt) : Math.Sin(Math.PI / 2 * (1 - t) / (1 - peakAt));
                    double y = start + (end - start) * t + (peak - linearAtPeak) * bump;
                    if (pts.Count == 0 || pts[^1].x < x) {
                        pts.Add((x, (int)Math.Round(y)));
                    }
                }
                break;
            }
            default:
                throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: shape kind must be const, ramp or arc.");
        }
        return new CurveSetRangeOp {
            PartId = p.Id, Abbr = op.Curve, FromTick = r.from, ToTick = r.to,
            Xs = pts.Select(pt => pt.x).ToList(), Ys = pts.Select(pt => pt.y).ToList(),
        };
    }

    /// <summary>y * scale + offset, clamped, at the range ends and at every existing point inside.</summary>
    internal static CurveSetRangeOp Transform(Contracts.Model.PartDto p, CurveOpInput op, string at, (int from, int to) r,
                                              Contracts.Model.CurveDto curve) {
        double scale = op.Scale ?? 1, offset = op.Offset ?? 0;
        if (op.Scale is null && op.Offset is null) {
            throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: transform needs 'scale' and/or 'offset'.");
        }
        double lo = curve.Min, hi = curve.Max;
        if (op.Clamp is { } c) {
            if (c.Length != 2 || c[0] > c[1]) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: clamp is [min, max].");
            }
            (lo, hi) = (Math.Max(lo, c[0]), Math.Min(hi, c[1]));
        }
        var xs = new List<int> { r.from };
        xs.AddRange(curve.Xs.Where(x => x > r.from && x < r.to));
        xs.Add(r.to);
        var ys = xs.Select(x => (int)Math.Round(Math.Clamp(CurvesGetTool.Sample(curve, x) * scale + offset, lo, hi))).ToList();
        return new CurveSetRangeOp { PartId = p.Id, Abbr = op.Curve, FromTick = r.from, ToTick = r.to, Xs = xs, Ys = ys };
    }

    /// <summary>The source span's shape (ends sampled, inner points kept) written again starting at 'to'.</summary>
    internal static CurveSetRangeOp Copy(ProjectContext ctx, Contracts.Model.PartDto p, CurveOpInput op, string at, (int from, int to)? range,
                                         Contracts.Model.CurveDto curve) {
        if (range is not { } r || op.To is null) {
            throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: copy needs 'range' (the source) and 'to'.");
        }
        int target = (ctx.Time.ParsePosition(op.To) - p.Pos) / Grid * Grid;
        int delta = target - r.from;
        if (target < 0 || r.to + delta > p.Dur) {
            throw new ToolInputException(ToolInputException.OutsidePart, $"{at}: the copy would end outside part {p.Id}.");
        }
        var xs = new List<int> { r.from };
        xs.AddRange(curve.Xs.Where(x => x > r.from && x < r.to));
        xs.Add(r.to);
        var ys = xs.Select(x => CurvesGetTool.Sample(curve, x)).ToList();
        return new CurveSetRangeOp {
            PartId = p.Id, Abbr = op.Curve, FromTick = r.from + delta, ToTick = r.to + delta,
            Xs = xs.Select(x => x + delta).ToList(), Ys = ys,
        };
    }
}
