using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Protocol;
using OpenUtauMcp.Server.Bridge;

namespace OpenUtauMcp.Server.Tools.Audio;

/// <summary>OpenUtau's DAW link. The server only drives OpenUtau; it never talks to the plugin.</summary>
[McpServerToolType]
public static class DawTool {
    [McpServerTool(Name = "ou_daw", Title = "DAW bridge status and connection", ReadOnly = false, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Links OpenUtau to its plugin (VST3/CLAP) in a DAW such as Bitwig, which plays the vocals live; one plugin instance plays one track. status: connections and the track each plays, plugin installed, DAW vs project BPM. scan: plugins running now. connect: one plugin (server = name or port; optional when only one runs). disconnect: one, or all without server. bind: a connected instance plays a track (server + track) and follows it when tracks move (DAW protocol 1.3). Save the project first. Example: {\"action\":\"bind\",\"server\":\"55704\",\"track\":\"Lead\"}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge,
        [Description("status | scan | connect | disconnect | bind")] string action,
        [Description("Plugin name or port from scan or status (connect/disconnect/bind).")] string? server = null,
        [Description("bind: track id, name or #position.")] string? track = null,
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            string method = action switch {
                "status" => Methods.DawGet,
                "scan" => Methods.DawScan,
                "connect" => Methods.DawConnect,
                "disconnect" => Methods.DawDisconnect,
                "bind" => Methods.DawBind,
                _ => throw new ToolInputException(ToolInputException.InvalidParams, $"Unknown action '{action}'. Use status, scan, connect, disconnect or bind."),
            };
            string? trackId = null;
            if (action == "bind") {
                if (string.IsNullOrWhiteSpace(track)) {
                    throw new ToolInputException(ToolInputException.InvalidParams, "bind needs track (id, name or #position).");
                }
                var ctx = await ProjectContext.LoadAsync(bridge, ct, "tracks").ConfigureAwait(false);
                trackId = ctx.ResolveTrack(track).Id;
            }
            var state = await bridge.DawAsync(method, server, ct, trackId).ConfigureAwait(false);
            var warnings = new List<Warning>();
            if (state.DawBpm is double daw && Math.Abs(daw - state.ProjectBpm) >= 0.5) {
                warnings.Add(new Warning("BPM_DIFFERS",
                    $"The DAW runs at {daw.ToString("0.##", CultureInfo.InvariantCulture)} BPM and the project at {state.ProjectBpm.ToString("0.##", CultureInfo.InvariantCulture)}: " +
                    "the vocals will drift. Set the same tempo in the DAW or with ou_project_set bpm."));
            }
            foreach (var shared in state.Connections.Where(c => c.TrackId is not null).GroupBy(c => c.TrackId).Where(g => g.Count() > 1)) {
                warnings.Add(new Warning("DUPLICATE_BINDING",
                    $"{shared.Count()} plugin instances play '{shared.First().TrackName}' ({string.Join(", ", shared.Select(c => c.Name))}), so it sounds louder in the DAW.",
                    shared.Key));
            }
            foreach (var missing in state.Connections.Where(c => c.Binding == "missing")) {
                warnings.Add(new Warning("TRACK_MISSING",
                    $"'{missing.Name}' plays a track this project does not have (deleted, or another project), so it is silent. Undo the deletion or bind it to another track."));
            }
            if (!state.Vst3Installed && !state.ClapInstalled) {
                warnings.Add(new Warning("PLUGIN_NOT_INSTALLED",
                    "The OpenUtau DAW plugin is not installed: install it from OpenUtau (DAW integration window), then load it on a track in the DAW."));
            }
            var data = new JsonObject {
                ["state"] = state.State,
                ["connections"] = new JsonArray(state.Connections.Select(c => (JsonNode?)Connection(c)).ToArray()),
                ["bindingSupported"] = state.BindingSupported,
                ["plugin"] = new JsonObject { ["vst3"] = state.Vst3Installed, ["clap"] = state.ClapInstalled },
                ["projectBpm"] = state.ProjectBpm,
                ["dawBpm"] = state.DawBpm,
            };
            if (action == "scan" || state.Servers is { Count: > 0 }) {
                data["servers"] = new JsonArray((state.Servers ?? []).Select(s => (JsonNode?)new JsonObject {
                    ["name"] = s.Name, ["port"] = s.Port, ["api"] = s.ApiVersion, ["compatible"] = s.Compatible,
                }).ToArray());
            }
            string summary = action switch {
                "scan" => state.Servers is { Count: > 0 } servers
                    ? $"{servers.Count} plugin(s) running: {string.Join(", ", servers.Select(s => $"{s.Name} (port {s.Port})"))}."
                    : "No OpenUtau plugin is running in a DAW. Load it on a track in the DAW, then scan again.",
                "connect" => $"Connected: {string.Join(", ", state.Connections.Select(c => $"{c.Name} ({c.State})"))}. Edits now reach the DAW without exporting.",
                "disconnect" => state.Connections.Count == 0 ? "Disconnected from the DAW." : $"Still connected: {string.Join(", ", state.Connections.Select(c => c.Name))}.",
                "bind" => BindSummary(state, server),
                _ => state.Connections.Count == 0 ? $"Not connected to a DAW ({state.State})." : $"{state.State}: {string.Join(", ", state.Connections.Select(Line))}.",
            };
            return ToolResult.Ok(summary, data, warnings);
        });

    static JsonObject Connection(DawConnection c) {
        var json = new JsonObject { ["name"] = c.Name, ["port"] = c.Port, ["state"] = c.State };
        if (c.Binding is not null) {
            json["binding"] = c.Binding;
        }
        if (c.TrackId is not null) {
            json["track"] = new JsonObject { ["id"] = c.TrackId, ["name"] = c.TrackName };
        }
        if (c.HostTrackName is not null) {
            json["dawTrack"] = c.HostTrackName;
        }
        return json;
    }

    static string Line(DawConnection c) => c.Binding switch {
        "bound" => $"{c.Name} port {c.Port} plays {c.TrackName}",
        "missing" => $"{c.Name} port {c.Port} (its track is gone)",
        "unbound" => $"{c.Name} port {c.Port} (no track)",
        _ => $"{c.Name} port {c.Port}",
    };

    static string BindSummary(DawState state, string? server) {
        var bound = state.Connections.FirstOrDefault(c => server is not null
            && (string.Equals(c.Name, server, StringComparison.OrdinalIgnoreCase) || c.Port.ToString(CultureInfo.InvariantCulture) == server))
            ?? (state.Connections.Count == 1 ? state.Connections[0] : null);
        return bound?.Binding == "bound"
            ? $"'{bound.Name}' now plays {bound.TrackName}, and follows it when tracks are reordered."
            : "The binding was sent; the plugin has not confirmed it yet (check ou_daw status).";
    }
}
