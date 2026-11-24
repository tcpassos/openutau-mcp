using System.Reflection;
using System.Text.Json.Nodes;
using OpenUtau.Core;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;
using Serilog;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>
/// catalog.singer / catalog.expressions (catalog sections 4 and 6). Tells which expressions actually change
/// the sound for a singer, so the client can warn instead of tuning curves that do nothing.
/// DiffSinger details come from the singer's dsConfig, read by reflection because DiffSingerSinger is internal.
/// </summary>
public static class Catalog {
    // Variance curves only matter with the matching embed AND a variance model.
    static readonly Dictionary<string, string> varianceEmbeds = new() {
        ["ene"] = "useEnergyEmbed", ["brec"] = "useBreathinessEmbed", ["voic"] = "useVoicingEmbed", ["tenc"] = "useTensionEmbed",
    };
    static readonly Dictionary<string, string> directEmbeds = new() { ["genc"] = "useKeyShiftEmbed", ["velc"] = "useSpeedEmbed" };
    static readonly string[] embedFields = [
        "useKeyShiftEmbed", "useSpeedEmbed", "useEnergyEmbed", "useBreathinessEmbed", "useVoicingEmbed", "useTensionEmbed",
    ];

    public static USinger FindSinger(string id) {
        var singers = SingerManager.Inst.Singers;
        if (singers.TryGetValue(id, out var singer)) {
            return singer;
        }
        var byName = singers.Values.Where(s => string.Equals(s.Name, id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) {
            return byName[0];
        }
        var suggestions = singers.Values
            .OrderByDescending(s => s.Name.Contains(id, StringComparison.OrdinalIgnoreCase) || s.Id.Contains(id, StringComparison.OrdinalIgnoreCase))
            .Take(5).Select(s => (JsonNode?)new JsonObject { ["id"] = s.Id, ["name"] = s.Name }).ToArray();
        throw new BridgeException(ErrorCodes.SingerNotFound, $"No installed singer '{id}'.",
            new JsonObject { ["suggestions"] = new JsonArray(suggestions) });
    }

    public static SingerInfo Singer(UProject project, CatalogSingerParams p) {
        USinger singer;
        IRenderer? renderer = null;
        if (p.TrackId is { } trackId) {
            var track = BridgeHost.Ids.ResolveTrack(project, trackId);
            singer = track.Singer ?? throw new BridgeException(ErrorCodes.NoSinger, $"Track {trackId} has no singer.");
            renderer = track.RendererSettings?.Renderer;
        } else if (p.Id is { } id) {
            singer = FindSinger(id);
        } else {
            throw BridgeException.InvalidParams("Give 'id' or 'trackId'.");
        }
        if (singer.Found && !singer.Loaded) {
            singer.EnsureLoaded();
        }
        renderer ??= singer.Found ? Renderers.GetOrCreate(Renderers.GetDefaultRenderer(singer.SingerType)) : null;
        var ds = DiffSingerDetails(singer);
        return new SingerInfo {
            Id = singer.Id,
            Name = singer.Name,
            Type = singer.SingerType.ToString(),
            Found = singer.Found,
            DefaultPhonemizer = string.IsNullOrEmpty(singer.DefaultPhonemizer) ? null : singer.DefaultPhonemizer,
            Renderers = singer.Found ? Renderers.GetSupportedRenderers(singer.SingerType) : null,
            Colors = Colors(singer),
            DiffSinger = ds,
            Effects = project.expressions.Values.Concat(renderer is null ? [] : SuggestedFor(singer, renderer))
                .DistinctBy(d => d.abbr)
                .ToDictionary(d => d.abbr, d => Effect(d, singer, renderer, ds)),
        };
    }

    public static ExpressionsCatalog Expressions(UProject project, CatalogExpressionsParams p) {
        var track = BridgeHost.Ids.ResolveTrack(project, p.TrackId);
        var singer = track.Singer;
        var renderer = track.RendererSettings?.Renderer;
        var ds = singer is null ? null : DiffSingerDetails(singer);
        var suggested = Suggested(track).ToDictionary(d => d.abbr);
        var all = project.expressions.Values.Concat(suggested.Values.Where(d => !project.expressions.ContainsKey(d.abbr)));
        return new ExpressionsCatalog {
            TrackId = p.TrackId,
            Renderer = track.RendererSettings?.renderer,
            Expressions = all.Select(d => new ExpressionInfo {
                Abbr = d.abbr,
                Name = d.name,
                Type = TypeName(d.type),
                Min = d.min,
                Max = d.max,
                DefaultValue = d.defaultValue,
                Registered = project.expressions.ContainsKey(d.abbr),
                Suggested = suggested.ContainsKey(d.abbr),
                SupportedByRenderer = renderer?.SupportsExpression(d) ?? false,
                Effect = singer is null ? Effects.Unknown : Effect(d, singer, renderer, ds),
            }).ToList(),
        };
    }

    /// <summary>Expressions the track's renderer suggests for its singer (e.g. velc, ene, pexp, cl01..).</summary>
    public static IEnumerable<UExpressionDescriptor> Suggested(UTrack track) =>
        track.Singer is { Found: true } singer && track.RendererSettings?.Renderer is { } renderer
            ? SuggestedFor(singer, renderer)
            : [];

    /// <summary>Expressions a singer's default renderer suggests (for a track that does not exist yet).</summary>
    public static IEnumerable<UExpressionDescriptor> SuggestedForSinger(USinger singer) =>
        singer.Found && Renderers.GetOrCreate(Renderers.GetDefaultRenderer(singer.SingerType)) is { } renderer
            ? SuggestedFor(singer, renderer)
            : [];

    static IEnumerable<UExpressionDescriptor> SuggestedFor(USinger singer, IRenderer renderer) {
        try {
            if (!singer.Loaded) {
                singer.EnsureLoaded();
            }
            return renderer.GetSuggestedExpressions(singer, new URenderSettings { renderer = renderer.ToString() ?? string.Empty }) ?? [];
        } catch (Exception e) {
            Log.Warning(e, $"{BridgeHost.LogPrefix} could not read suggested expressions for {singer.Id}");
            return [];
        }
    }

    static string Effect(UExpressionDescriptor d, USinger singer, IRenderer? renderer, DiffSingerInfo? ds) {
        if (!singer.Found) {
            return Effects.Unknown;
        }
        if (renderer is not null && !renderer.SupportsExpression(d)) {
            return Effects.NoEffect;
        }
        if (d.abbr == Format_CLR) {
            return (Colors(singer)?.Count ?? 0) > 1 ? Effects.Effective : Effects.NoEffect;
        }
        if (ds is null) {
            return renderer is null ? Effects.Unknown : Effects.Effective;
        }
        if (directEmbeds.TryGetValue(d.abbr, out var embed)) {
            return ds.Embeds.GetValueOrDefault(embed) ? Effects.Effective : Effects.NoEffect;
        }
        if (varianceEmbeds.TryGetValue(d.abbr, out embed)) {
            if (!ds.Embeds.GetValueOrDefault(embed)) {
                return Effects.NoEffect;
            }
            return ds.HasVariancePredictor ? Effects.Effective : Effects.Unknown;
        }
        if (d.abbr == "pexp") {
            return ds.HasPitchPredictor ? Effects.Unknown : Effects.NoEffect;
        }
        if (d.abbr.StartsWith("cl", StringComparison.Ordinal) && int.TryParse(d.abbr.AsSpan(2), out _)) {
            return (ds.Speakers?.Count ?? 0) > 1 ? Effects.Effective : Effects.NoEffect;
        }
        return Effects.Effective;
    }

    const string Format_CLR = "clr";

    static IReadOnlyList<string>? Colors(USinger singer) {
        var colors = singer.Subbanks?.Select(s => s.Color).Where(c => c != null).Distinct().OrderBy(c => c).ToList();
        return colors is { Count: > 0 } ? colors : null;
    }

    static DiffSingerInfo? DiffSingerDetails(USinger singer) {
        if (singer.SingerType != USingerType.DiffSinger || !singer.Found) {
            return null;
        }
        try {
            var type = singer.GetType();
            var config = type.GetField("dsConfig", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(singer);
            if (config is null) {
                return null;
            }
            var configType = config.GetType();
            var embeds = embedFields.ToDictionary(f => f,
                f => configType.GetField(f)?.GetValue(config) is true);
            var speakers = configType.GetField("speakers")?.GetValue(config) as IEnumerable<string>;
            bool Has(string property) => type.GetProperty(property)?.GetValue(singer) is true;
            return new DiffSingerInfo {
                Speakers = speakers?.ToList(),
                Embeds = embeds,
                HasPitchPredictor = Has("HasPitchPredictor"),
                HasVariancePredictor = Has("HasVariancePredictor"),
            };
        } catch (Exception e) {
            Log.Warning(e, $"{BridgeHost.LogPrefix} could not read DiffSinger config of {singer.Id}");
            return null;
        }
    }

    public static string TypeName(UExpressionType type) => type switch {
        UExpressionType.Numerical => "numerical",
        UExpressionType.Options => "options",
        UExpressionType.Curve => "curve",
        UExpressionType.MaskedCurve => "maskedCurve",
        _ => type.ToString().ToLowerInvariant(),
    };
}
