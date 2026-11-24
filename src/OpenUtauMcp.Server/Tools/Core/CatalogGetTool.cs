using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Server.Bridge;

namespace OpenUtauMcp.Server.Tools.Core;

[McpServerToolType]
public static class CatalogGetTool {
    [McpServerTool(Name = "ou_catalog_get", Title = "Look up singers, expressions and other catalogs", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Catalog lookups. singer (id or track): voice colors (clr index), DiffSinger embeds, which expressions affect the sound. expressions (track): registered and suggested expressions with range and effect. singers, phonemizers (query, paging), renderers, batch_edits, humanize_presets. g2p (track, words): how the phonemizer reads each word (its dictionary or its own guess; null = no answer). Example: {\"kind\":\"expressions\",\"track\":\"Lead D\"}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge,
        [Description("singer | expressions | singers | phonemizers | renderers | batch_edits | humanize_presets | g2p")] string kind,
        [Description("Singer id (kind=singer).")] string? id = null,
        [Description("Track id or exact name.")] string? track = null,
        [Description("singers / phonemizers: substring filter.")] string? query = null,
        [Description("singers / phonemizers: max entries (default 50).")] int limit = 50,
        [Description("singers / phonemizers: cursor from a previous page (page.next).")] string? cursor = null,
        [Description("g2p: words to look up, e.g. [\"porcelain\",\"crown\"].")] string[]? words = null,
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            string? trackId = null;
            if (track is not null) {
                var ctx = await ProjectContext.LoadAsync(bridge, ct, "tracks").ConfigureAwait(false);
                trackId = ctx.ResolveTrack(track).Id;
            }
            switch (kind) {
                case "singer": {
                    if (id is null && trackId is null) {
                        throw new ToolInputException(ToolInputException.InvalidParams, "Give 'id' or 'track'.");
                    }
                    var s = await bridge.CatalogSingerAsync(new CatalogSingerParams { Id = id, TrackId = trackId }, ct).ConfigureAwait(false);
                    var data = new JsonObject {
                        ["id"] = s.Id, ["name"] = s.Name, ["type"] = s.Type, ["found"] = s.Found,
                        ["defaultPhonemizer"] = s.DefaultPhonemizer,
                        ["colors"] = s.Colors is { } colors ? new JsonArray(colors.Select((c, i) => (JsonNode?)new JsonArray(i, c)).ToArray()) : null,
                        ["effective"] = Abbrs(s.Effects, Effects.Effective),
                        ["noEffect"] = Abbrs(s.Effects, Effects.NoEffect),
                        ["unknown"] = Abbrs(s.Effects, Effects.Unknown),
                    };
                    if (s.DiffSinger is { } ds) {
                        data["diffSinger"] = new JsonObject {
                            ["embeds"] = new JsonObject(ds.Embeds.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                            ["speakers"] = ds.Speakers is { } sp ? new JsonArray(sp.Select(x => (JsonNode?)x).ToArray()) : null,
                            ["pitchPredictor"] = ds.HasPitchPredictor,
                            ["variancePredictor"] = ds.HasVariancePredictor,
                        };
                    }
                    return ToolResult.Ok($"{s.Name} ({s.Type}{(s.Found ? "" : ", not installed")}): effective {string.Join(", ", Effective(s.Effects))}.", data);
                }
                case "expressions": {
                    if (trackId is null) {
                        throw new ToolInputException(ToolInputException.InvalidParams, "kind=expressions needs 'track'.");
                    }
                    var c = await bridge.CatalogExpressionsAsync(trackId, ct).ConfigureAwait(false);
                    var data = new JsonObject {
                        ["track"] = c.TrackId, ["renderer"] = c.Renderer,
                        ["cols"] = new JsonArray("abbr", "name", "type", "min", "max", "default", "registered", "effect"),
                        ["rows"] = new JsonArray(c.Expressions.Select(e => (JsonNode?)new JsonArray(
                            e.Abbr, e.Name, e.Type, e.Min, e.Max, e.DefaultValue,
                            e.Registered ? "yes" : (e.Suggested ? "suggested" : "no"), e.Effect)).ToArray()),
                    };
                    int effective = c.Expressions.Count(e => e.Effect == Effects.Effective);
                    return ToolResult.Ok($"{c.Expressions.Count} expressions for track {c.TrackId} ({effective} effective).", data);
                }
                case "singers": {
                    int offset = Cursor(cursor);
                    var s = await bridge.CatalogSingersAsync(new CatalogListParams { Query = query, Offset = offset, Limit = limit }, ct).ConfigureAwait(false);
                    var data = Paged(new JsonArray("id", "name", "type", "author", "version", "found"),
                        s.Singers.Select(x => new JsonNode?[] { x.Id, x.Name, x.Type, x.Author, x.Version, x.Found }), s.Total, offset);
                    return ToolResult.Ok($"{s.Singers.Count} of {s.Total} singers.", data);
                }
                case "phonemizers": {
                    int offset = Cursor(cursor);
                    var ph = await bridge.CatalogPhonemizersAsync(new CatalogListParams { Query = query, Offset = offset, Limit = limit }, ct).ConfigureAwait(false);
                    var data = Paged(new JsonArray("id", "name", "tag", "language"),
                        ph.Phonemizers.Select(x => new JsonNode?[] { x.Id, x.Name, x.Tag, x.Language }), ph.Total, offset);
                    return ToolResult.Ok($"{ph.Phonemizers.Count} of {ph.Total} phonemizers. Use the id (or the class name) in ou_tracks_edit / ou_notes_edit.", data);
                }
                case "renderers": {
                    var r = await bridge.CatalogRenderersAsync(ct).ConfigureAwait(false);
                    var data = new JsonObject(r.BySingerType.Select(kv => KeyValuePair.Create(kv.Key,
                        (JsonNode?)new JsonArray(kv.Value.Select(v => (JsonNode?)v).ToArray()))));
                    return ToolResult.Ok("Renderers per singer type (the first is the default).", data);
                }
                case "batch_edits":
                    return ToolResult.Ok($"{BatchNames.Sync.Length + BatchNames.Async.Length} batch edits for ou_batch_edit.", new JsonObject {
                        ["sync"] = new JsonArray(BatchNames.Sync.Select(n => (JsonNode?)n).ToArray()),
                        ["jobs"] = new JsonArray(BatchNames.Async.Select(n => (JsonNode?)n).ToArray()),
                        ["args"] = new JsonObject { ["quantize"] = "grid (default 1/16)", ["transpose"] = "semitones", ["add_breath"] = "lyric (default AP)",
                            ["add_tail"] = "lyric (default -)", ["remove_tail"] = "lyric (default -)" },
                    });
                case "humanize_presets":
                    return ToolResult.Ok("Presets of ou_music_humanize.", new JsonObject {
                        ["natural"] = "lead vocal: all layers, breaths, role-based dynamics and voice color (default)",
                        ["backing"] = "harmonies: intensity 0.7, no breaths, softer dynamics (-10), brighter formant, short falls",
                        ["chops"] = "vocal chops: no breaths, power color, darker formant, long falls (-250 cents)",
                        ["layers"] = new JsonArray(OpenUtauMcp.Music.Humanize.HumanizeParams.LayerNames.Select(n => (JsonNode?)n).ToArray()),
                    });
                case "g2p": {
                    if (trackId is null || words is not { Length: > 0 }) {
                        throw new ToolInputException(ToolInputException.InvalidParams, "kind=g2p needs 'track' and 'words'.");
                    }
                    var g = await bridge.G2pQueryAsync(new G2pQueryParams { TrackId = trackId, Words = words }, ct).ConfigureAwait(false);
                    if (g.Words is null) {
                        return ToolResult.Ok($"The track's phonemizer ({g.Phonemizer}) has no dictionary to query; read the phonemes of real notes with ou_notes_get fields:[\"phonemes\"].",
                            new JsonObject { ["phonemizer"] = g.Phonemizer });
                    }
                    var data = new JsonObject {
                        ["phonemizer"] = g.Phonemizer,
                        ["words"] = new JsonObject(g.Words.Select(w => KeyValuePair.Create(w.Word,
                            w.Phonemes is { } p ? (JsonNode?)string.Join(" ", p) : null))),
                    };
                    int missing = g.Words.Count(w => w.Phonemes is null);
                    return ToolResult.Ok($"{g.Words.Count - missing} of {g.Words.Count} words have phonemes in {g.Phonemizer} (dictionary or guess).", data);
                }
                default:
                    throw new ToolInputException(ToolInputException.InvalidParams,
                        $"Unknown kind '{kind}'. Use singer, expressions, singers, phonemizers, renderers, batch_edits, humanize_presets or g2p.");
            }
        });

    static int Cursor(string? cursor) =>
        cursor is null ? 0
        : cursor.StartsWith("o:", StringComparison.Ordinal) && int.TryParse(cursor.AsSpan(2), out int o) && o >= 0 ? o
        : throw new ToolInputException(ToolInputException.InvalidParams, $"Invalid cursor '{cursor}'. Pass page.next from a previous call.");

    static JsonObject Paged(JsonArray cols, IEnumerable<JsonNode?[]> rows, int total, int offset) {
        var list = rows.Select(r => (JsonNode?)new JsonArray(r)).ToArray();
        var page = new JsonObject { ["total"] = total };
        if (offset + list.Length < total) {
            page["next"] = $"o:{offset + list.Length}";
        }
        return new JsonObject { ["cols"] = cols, ["rows"] = new JsonArray(list), ["page"] = page };
    }

    static IEnumerable<string> Effective(IReadOnlyDictionary<string, string> effects) =>
        effects.Where(kv => kv.Value == Effects.Effective).Select(kv => kv.Key).Order();

    static JsonArray Abbrs(IReadOnlyDictionary<string, string> effects, string effect) =>
        new(effects.Where(kv => kv.Value == effect).Select(kv => kv.Key).Order().Select(a => (JsonNode?)a).ToArray());
}
