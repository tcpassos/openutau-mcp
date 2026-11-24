using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Server.Bridge;
using OpenUtauMcp.Server.Units;

namespace OpenUtauMcp.Server.Tools.Core;

[McpServerToolType]
public static class ProjectGetTool {
    static readonly string[] keyNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    [McpServerTool(Name = "ou_project_get", Title = "Read project structure", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Reads the open project: tempo and time signatures, tracks (singer, phonemizer, mix, voice colors) and " +
                 "parts (id, track, start/end as bar:beat, note count, partRev). Use part ids from here in other tools. " +
                 "Example: {\"include\":[\"tempo\",\"tracks\",\"parts\",\"expressions\"]}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge,
        [Description("Sections to include: tempo, tracks, parts, expressions, comment. Default: tempo, tracks, parts.")] string[]? include = null,
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            var wanted = include is { Length: > 0 } ? include : ["tempo", "tracks", "parts"];
            // Tempo is needed to format positions, tracks and parts for names and the summary line.
            var needed = wanted.Union(["tempo", "tracks", "parts"]).ToArray();
            var ctx = await ProjectContext.LoadAsync(bridge, ct, needed).ConfigureAwait(false);
            var p = ctx.Project;
            var data = new JsonObject { ["name"] = p.Name, ["filePath"] = p.FilePath, ["epoch"] = p.Epoch };

            if (wanted.Contains("tempo")) {
                data["tempo"] = new JsonObject {
                    ["bpm"] = p.Tempos?.FirstOrDefault()?.Bpm,
                    ["changes"] = new JsonArray((p.Tempos ?? []).Skip(1)
                        .Select(t => (JsonNode?)new JsonArray(ctx.Time.FormatPosition(t.Tick), t.Bpm)).ToArray()),
                    ["timeSigs"] = new JsonArray((p.TimeSignatures ?? [])
                        .Select(s => (JsonNode?)new JsonArray(s.Bar + 1, $"{s.BeatPerBar}/{s.BeatUnit}")).ToArray()),
                    ["key"] = keyNames[((p.Key % 12) + 12) % 12],
                };
            }
            if (wanted.Contains("tracks") && p.Tracks is { } tracks) {
                data["tracks"] = Table(["id", "name", "singer", "phonemizer", "renderer", "volumeDb", "pan", "mute", "solo", "colors"],
                    tracks.Select(t => new JsonNode?[] {
                        t.Id, t.Name, t.SingerFound ? t.Singer : $"{t.Singer} (missing)", ShortName(t.Phonemizer), t.Renderer,
                        t.VolumeDb, t.Pan, t.Mute, t.Solo,
                        t.VoiceColors is { } c ? new JsonArray(c.Select(x => (JsonNode?)x).ToArray()) : null,
                    }));
            }
            if (wanted.Contains("parts") && p.Parts is { } parts) {
                data["parts"] = Table(["id", "name", "track", "type", "from", "to", "notes", "partRev"],
                    parts.Select(x => new JsonNode?[] {
                        x.Id, x.Name, ctx.TrackName(x.TrackId), x.Type,
                        ctx.Time.FormatPosition(x.Pos), ctx.Time.FormatPosition(x.Pos + x.Dur), x.NoteCount, x.PartRev,
                    }));
            }
            if (wanted.Contains("expressions") && p.Expressions is { } exps) {
                data["expressions"] = Table(["abbr", "name", "type", "min", "max", "default"],
                    exps.Select(e => new JsonNode?[] { e.Abbr, e.Name, e.Type, e.Min, e.Max, e.DefaultValue }));
            }
            if (wanted.Contains("comment")) {
                data["comment"] = p.Comment;
            }
            int partCount = p.Parts?.Count ?? 0, trackCount = p.Tracks?.Count ?? 0;
            return ToolResult.Ok($"Project '{p.Name}': {trackCount} tracks, {partCount} parts.", data);
        });

    internal static JsonObject Table(string[] cols, IEnumerable<JsonNode?[]> rows) => new() {
        ["cols"] = new JsonArray(cols.Select(c => (JsonNode?)c).ToArray()),
        ["rows"] = new JsonArray(rows.Select(r => (JsonNode?)new JsonArray(r)).ToArray()),
    };

    static string? ShortName(string? typeName) => typeName?[(typeName.LastIndexOf('.') + 1)..];
}
