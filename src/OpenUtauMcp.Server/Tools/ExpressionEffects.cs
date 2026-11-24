using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Server.Bridge;

namespace OpenUtauMcp.Server.Tools;

/// <summary>EXPRESSION_NO_EFFECT warnings: writing an expression the part's singer ignores (from catalog.singer).</summary>
public static class ExpressionEffects {
    public const string NoEffectCode = "EXPRESSION_NO_EFFECT";

    public static async Task<IReadOnlyList<Warning>> WarningsAsync(IBridgeClient bridge, PartDto part, IEnumerable<string> abbrs,
                                                                   CancellationToken ct) {
        var wanted = abbrs.Distinct().ToList();
        if (wanted.Count == 0 || string.IsNullOrEmpty(part.TrackId)) {
            return [];
        }
        SingerInfo singer;
        try {
            singer = await bridge.CatalogSingerAsync(new CatalogSingerParams { TrackId = part.TrackId }, ct).ConfigureAwait(false);
        } catch (BridgeCallException) {
            return [];  // no singer: nothing useful to say
        }
        return wanted
            .Where(abbr => singer.Effects.TryGetValue(abbr, out var effect) && effect == Effects.NoEffect)
            .Select(abbr => new Warning(NoEffectCode, $"'{abbr}' has no effect with {singer.Name}; the value is stored but the sound will not change.", part.Id))
            .ToList();
    }
}
