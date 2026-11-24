using OpenUtau.Core.Ustx;
using OpenUtauMcp.Contracts.Protocol;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>Curve point helpers: 5-tick grid, clamping, and range splicing that keeps values outside the range.</summary>
public static class CurveMath {
    const int Grid = ProtocolInfo.CurveGridTicks;

    public static int Snap(int x) => (int)Math.Round(x / (double)Grid, MidpointRounding.ToEven) * Grid;

    /// <summary>Snaps x to the grid (last value wins on duplicates) and clamps y to the descriptor range.</summary>
    public static SortedDictionary<int, int> Normalize(IReadOnlyList<int> xs, IReadOnlyList<int> ys, UExpressionDescriptor d,
                                                       out bool snapped, out bool clamped, string at) {
        int min = (int)Math.Ceiling(d.min), max = (int)Math.Floor(d.max);
        var points = new SortedDictionary<int, int>();
        snapped = clamped = false;
        for (int i = 0; i < xs.Count; i++) {
            if (xs[i] < 0) {
                throw BridgeException.OutOfRange($"{at}.xs[{i}]", xs[i], 0, int.MaxValue);
            }
            int x = Snap(xs[i]);
            int y = Math.Clamp(ys[i], min, max);
            snapped |= x != xs[i];
            clamped |= y != ys[i];
            points[x] = y;
        }
        return points;
    }

    /// <summary>
    /// Replaces the points in [from, to] with <paramref name="inside"/>, keeping every point outside. Anchors one
    /// grid step outside each edge hold the old value there, so the curve outside the range does not move.
    /// </summary>
    public static (int[] xs, int[] ys) Splice(UCurve? curve, int from, int to, SortedDictionary<int, int> inside, int defaultValue) {
        var result = new SortedDictionary<int, int>();
        if (curve is not null) {
            for (int i = 0; i < curve.xs.Count; i++) {
                if (curve.xs[i] < from || curve.xs[i] > to) {
                    result[curve.xs[i]] = curve.ys[i];
                }
            }
            bool hasLeft = curve.xs.Any(x => x < from);
            bool hasRight = curve.xs.Any(x => x > to);
            if (hasLeft && from - Grid >= 0) {
                result.TryAdd(from - Grid, Sample(curve, from - Grid, defaultValue));
            }
            if (hasRight) {
                result.TryAdd(to + Grid, Sample(curve, to + Grid, defaultValue));
            }
        }
        foreach (var (x, y) in inside) {
            result[x] = y;
        }
        return (result.Keys.ToArray(), result.Values.ToArray());
    }

    /// <summary>Value of the curve at x as OpenUtau reads it: linear between points, the default outside them.</summary>
    public static int Sample(UCurve curve, int x, int defaultValue) {
        int idx = curve.xs.BinarySearch(x);
        if (idx >= 0) {
            return curve.ys[idx];
        }
        idx = ~idx;
        if (idx == 0 || idx == curve.xs.Count) {
            return defaultValue;
        }
        double t = (x - curve.xs[idx - 1]) / (double)(curve.xs[idx] - curve.xs[idx - 1]);
        return (int)Math.Round(curve.ys[idx - 1] + t * (curve.ys[idx] - curve.ys[idx - 1]));
    }
}
