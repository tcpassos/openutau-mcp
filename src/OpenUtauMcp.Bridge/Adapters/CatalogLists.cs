using System.Reflection;
using OpenUtau.Api;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtauMcp.Contracts.Model;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>catalog.singers / catalog.phonemizers / catalog.renderers / g2p.query. UI thread only.</summary>
public static class CatalogLists {
    const int MaxLimit = 500;

    public static SingersCatalog Singers(CatalogListParams p) {
        var all = SingerManager.Inst.Singers.Values
            .Where(s => Matches(p.Query, s.Id, s.Name))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new SingersCatalog {
            Total = all.Count,
            Singers = Page(all, p).Select(s => new SingerSummary(s.Id, s.Name, s.SingerType.ToString(),
                Blank(s.Author), Blank(s.Version), s.Found)).ToList(),
        };
    }

    public static PhonemizersCatalog Phonemizers(CatalogListParams p) {
        var all = PhonemizerFactory.GetAll()
            .Where(f => Matches(p.Query, f.Id, f.name, f.tag, f.language))
            .OrderBy(f => f.language).ThenBy(f => f.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new PhonemizersCatalog {
            Total = all.Count,
            Phonemizers = Page(all, p).Select(f => new PhonemizerSummary(f.Id, f.name, Blank(f.tag), Blank(f.language), Blank(f.author))).ToList(),
        };
    }

    public static RenderersCatalog Renderers() => new() {
        BySingerType = Enum.GetValues<USingerType>().Distinct()
            .ToDictionary(t => t.ToString(), t => (IReadOnlyList<string>)OpenUtau.Core.Render.Renderers.GetSupportedRenderers(t)),
    };

    /// <summary>
    /// How the track's phonemizer reads each word, from the dictionary it loaded (field of type IG2p, found by
    /// reflection: phonemizers keep it private). Phonemizers without a dictionary return Words = null.
    /// </summary>
    public static G2pQueryResult G2p(UProject project, G2pQueryParams p) {
        var track = BridgeHost.Ids.ResolveTrack(project, p.TrackId);
        var phonemizer = track.Phonemizer;
        var g2p = phonemizer is null ? null : FindG2p(phonemizer);
        return new G2pQueryResult {
            TrackId = BridgeHost.Ids.IdOf(track),
            Phonemizer = phonemizer?.GetType().Name,
            Words = g2p is null ? null : p.Words.Select(w => {
                string[]? phonemes = null;
                try {
                    phonemes = g2p.Query(w.ToLowerInvariant());
                } catch (Exception) {
                    // Treated as "not in the dictionary".
                }
                return new G2pEntry(w, phonemes);
            }).ToList(),
        };
    }

    static IG2p? FindG2p(Phonemizer phonemizer) {
        for (var type = phonemizer.GetType(); type is not null && type != typeof(object); type = type.BaseType) {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)) {
                if (typeof(IG2p).IsAssignableFrom(field.FieldType) && field.GetValue(phonemizer) is IG2p g2p) {
                    return g2p;
                }
            }
            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)) {
                if (typeof(IG2p).IsAssignableFrom(prop.PropertyType) && prop.GetIndexParameters().Length == 0
                    && prop.GetValue(phonemizer) is IG2p g2p) {
                    return g2p;
                }
            }
        }
        return null;
    }

    static bool Matches(string? query, params string?[] values) =>
        string.IsNullOrWhiteSpace(query) || values.Any(v => v?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);

    static IEnumerable<T> Page<T>(List<T> all, CatalogListParams p) =>
        all.Skip(Math.Max(0, p.Offset)).Take(p.Limit is > 0 and <= MaxLimit ? p.Limit : 50);

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
