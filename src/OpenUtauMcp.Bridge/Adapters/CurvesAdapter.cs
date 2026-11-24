using OpenUtau.Core.Ustx;
using OpenUtauMcp.Contracts.Model;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>curves.query (user curves). UI thread only.</summary>
public static class CurvesAdapter {
    public static CurvesQueryResult Query(UProject project, CurvesQueryParams p) {
        var part = BridgeHost.Ids.ResolveVoicePart(project, p.PartId);
        var wanted = p.Abbrs is { Count: > 0 } abbrs ? new HashSet<string>(abbrs) : null;
        var curves = new List<CurveDto>();
        foreach (var curve in part.curves) {
            bool hasData = curve.xs.Count > 0 || p.Real && curve.realXs.Count > 0;
            if (wanted is not null && !wanted.Contains(curve.abbr) || !hasData && wanted is null) {
                continue;
            }
            project.expressions.TryGetValue(curve.abbr, out var descriptor);
            var xs = new List<int>();
            var ys = new List<int>();
            for (int i = 0; i < curve.xs.Count; i++) {
                if (p.Range is { } r && (curve.xs[i] < r.FromTick || curve.xs[i] > r.ToTick)) {
                    continue;
                }
                xs.Add(curve.xs[i]);
                ys.Add(curve.ys[i]);
            }
            float min = descriptor?.min ?? 0, max = descriptor?.max ?? 0;
            List<int>? realXs = null, realYs = null;
            if (p.Real && curve.realXs.Count > 0) {
                // Stored as 0..1000 of the lane height, -1 = gap (as ExpressionCanvas draws them).
                realXs = [];
                realYs = [];
                for (int i = 0; i < curve.realXs.Count; i++) {
                    int x = curve.realXs[i];
                    if (curve.realYs[i] < 0 || p.Range is { } rr && (x < rr.FromTick || x > rr.ToTick)) {
                        continue;
                    }
                    realXs.Add(x);
                    realYs.Add((int)Math.Round(min + curve.realYs[i] / 1000.0 * (max - min)));
                }
            }
            curves.Add(new CurveDto {
                Abbr = curve.abbr, Xs = xs, Ys = ys,
                Min = min, Max = max, DefaultValue = descriptor?.defaultValue ?? 0,
                RealXs = realXs, RealYs = realYs,
            });
        }
        // Requested curves without data are reported empty so the caller knows their default.
        if (wanted is not null) {
            foreach (var abbr in wanted.Where(a => curves.All(c => c.Abbr != a))) {
                if (project.expressions.TryGetValue(abbr, out var d)) {
                    curves.Add(new CurveDto { Abbr = abbr, Xs = [], Ys = [], Min = d.min, Max = d.max, DefaultValue = d.defaultValue });
                }
            }
        }
        return new CurvesQueryResult {
            PartId = BridgeHost.Ids.IdOf(part),
            PartPos = part.position,
            PartRev = BridgeHost.Revisions.PartRev(part),
            Epoch = BridgeHost.Ids.Epoch,
            Curves = curves,
        };
    }
}
