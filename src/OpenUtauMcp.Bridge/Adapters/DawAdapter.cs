using System.Reflection;
using OpenUtau.Core;
using OpenUtau.Core.DawIntegration;
using OpenUtau.Core.Ustx;
using OpenUtauMcp.Bridge.Dispatch;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>
/// daw.*: drives OpenUtau's own DAW link (the OpenUtau plugin inside the DAW).
/// The bridge never talks to the plugin; it only calls DawServerFinder and DawManager, like the DAW window does.
/// </summary>
public static class DawAdapter {
    static List<DawServer> lastScan = [];

    static readonly FieldInfo? lastDawBpm = typeof(DawManager).GetField("lastDawBpm", BindingFlags.Instance | BindingFlags.NonPublic);

    // Track binding (DAW protocol 1.3) is newer than the OpenUtau this bridge is built against, so it
    // is found by reflection and reported as unsupported where it is missing.
    static readonly PropertyInfo? trackId = typeof(UTrack).GetProperty("Id");
    static readonly PropertyInfo? boundTrackId = typeof(DawConnectionInfo).GetProperty("BoundTrackId");
    static readonly PropertyInfo? hostTrackName = typeof(DawConnectionInfo).GetProperty("HostTrackName");
    static readonly PropertyInfo? negotiatedMinor = typeof(DawConnectionInfo).GetProperty("NegotiatedMinor");
    static readonly MethodInfo? bindTrack = typeof(DawManager).GetMethod("BindTrackAsync", [typeof(int), typeof(string), typeof(CancellationToken)]);

    static bool BindingSupported =>
        trackId is not null && boundTrackId is not null && negotiatedMinor is not null && bindTrack is not null;

    public static Task<DawState> GetAsync(CancellationToken ct) => UiDispatcher.RunAsync(State, ct);

    public static async Task<DawState> ScanAsync(CancellationToken ct) {
        var servers = await Task.Run(() => DawServerFinder.Default.Scan(), ct).ConfigureAwait(false);
        lock (typeof(DawAdapter)) {
            lastScan = servers;
        }
        return await UiDispatcher.RunAsync(State, ct).ConfigureAwait(false);
    }

    public static async Task<DawState> ConnectAsync(DawTargetParams p, CancellationToken ct) {
        var servers = await Task.Run(() => DawServerFinder.Default.Scan(), ct).ConfigureAwait(false);
        lock (typeof(DawAdapter)) {
            lastScan = servers;
        }
        var server = Find(servers, p.Server, required: servers.Count != 1);
        if (!server.IsCompatible) {
            throw new BridgeException(ErrorCodes.CapabilityUnavailable,
                $"The plugin '{server.Name}' speaks DAW api {server.Info.ApiVersion}; this OpenUtau speaks {DawApiVersion.CurrentString}.");
        }
        // The DAW window starts the connection from the UI thread; do the same, then wait outside it.
        var connecting = await UiDispatcher.RunAsync(_ => DawManager.Inst.ConnectAsync(server, CancellationToken.None), ct).ConfigureAwait(false);
        try {
            await connecting.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        } catch (TimeoutException) {
            throw new BridgeException(ErrorCodes.Timeout, $"The plugin '{server.Name}' did not finish connecting in 20 s.");
        } catch (Exception e) when (e is not BridgeException and not OperationCanceledException) {
            // MessageCustomizableException carries the friendly text (e.g. "save the project first").
            string message = e is MessageCustomizableException m ? m.SubstanceException?.Message ?? m.Message : e.Message;
            throw new BridgeException(ErrorCodes.IoError, $"OpenUtau could not connect to '{server.Name}': {message}");
        }
        return await UiDispatcher.RunAsync(State, ct).ConfigureAwait(false);
    }

    public static async Task<DawState> DisconnectAsync(DawTargetParams p, CancellationToken ct) {
        var disconnecting = await UiDispatcher.RunAsync(_ => {
            if (p.Server is null) {
                return DawManager.Inst.DisconnectAsync();
            }
            var connection = DawManager.Inst.Connections.FirstOrDefault(c => Matches(c.Name, c.Port, p.Server))
                             ?? throw new BridgeException(ErrorCodes.NotFound, $"No DAW connection '{p.Server}'.");
            return DawManager.Inst.DisconnectAsync(connection.Port);
        }, ct).ConfigureAwait(false);
        try {
            await disconnecting.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        } catch (TimeoutException) {
            throw new BridgeException(ErrorCodes.Timeout, "The DAW connection did not close in 10 s; check ou_daw status.");
        }
        return await UiDispatcher.RunAsync(State, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// daw.bind: asks a connected plugin instance to play a track, by the track's stable id, as if it had been
    /// picked in the plugin. Waits briefly for the plugin to confirm, so the returned state shows the binding.
    /// </summary>
    public static async Task<DawState> BindAsync(DawTargetParams p, CancellationToken ct) {
        if (!BindingSupported) {
            throw new BridgeException(ErrorCodes.CapabilityUnavailable,
                "This OpenUtau cannot bind DAW plugin instances to tracks (it needs DAW protocol 1.3).");
        }
        if (p.TrackId is null) {
            throw BridgeException.InvalidParams("daw.bind needs trackId.", "trackId");
        }
        var (port, name, stableId, binding) = await UiDispatcher.RunAsync(project => {
            var connections = DawManager.Inst.Connections.Where(c => c.State == DawConnectionState.Connected).ToList();
            DawConnectionInfo connection;
            if (p.Server is null) {
                connection = connections.Count == 1 ? connections[0] : throw BridgeException.InvalidParams(
                    connections.Count == 0
                        ? "No plugin is connected; connect one with daw.connect first."
                        : $"{connections.Count} plugins are connected; pass server (one of {string.Join(", ", connections.Select(c => $"'{c.Name}' port {c.Port}"))}).",
                    "server");
            } else {
                connection = connections.FirstOrDefault(c => Matches(c.Name, c.Port, p.Server))
                             ?? throw new BridgeException(ErrorCodes.NotFound, $"No connected plugin '{p.Server}'.");
            }
            if ((int)(negotiatedMinor!.GetValue(connection) ?? 0) < 3) {
                throw new BridgeException(ErrorCodes.CapabilityUnavailable,
                    $"The plugin '{connection.Name}' binds tracks by position only (DAW api 1.2); pick the track in its window or parameter.");
            }
            var track = BridgeHost.Ids.ResolveTrack(project, p.TrackId);
            string id = (string)trackId!.GetValue(track)!;
            var task = (Task)bindTrack!.Invoke(DawManager.Inst, [connection.Port, id, CancellationToken.None])!;
            return (connection.Port, connection.Name, id, task);
        }, ct).ConfigureAwait(false);
        try {
            await binding.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        } catch (TimeoutException) {
            throw new BridgeException(ErrorCodes.Timeout, $"The plugin '{name}' did not answer the binding in 15 s.");
        } catch (Exception e) when (e is not BridgeException and not OperationCanceledException) {
            throw new BridgeException(ErrorCodes.IoError, $"The plugin '{name}' could not bind the track: {e.Message}");
        }
        // The plugin confirms with a notification after its answer; give it a moment so the state shows it.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline) {
            var info = DawManager.Inst.Connections.FirstOrDefault(c => c.Port == port);
            if (info is null || (string?)boundTrackId!.GetValue(info) == stableId) {
                break;
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        return await UiDispatcher.RunAsync(State, ct).ConfigureAwait(false);
    }

    /// <summary>The connection with its track, when this OpenUtau and the plugin both report bindings.</summary>
    static DawConnection Describe(DawConnectionInfo c, OpenUtau.Core.Ustx.UProject project) {
        var connection = new DawConnection(c.Name, c.Port, c.State.ToString());
        if (!BindingSupported || c.State != DawConnectionState.Connected || (int)(negotiatedMinor!.GetValue(c) ?? 0) < 3) {
            return connection;
        }
        string? host = (string?)hostTrackName?.GetValue(c);
        if (boundTrackId!.GetValue(c) is not string id) {
            return connection with { Binding = "unbound", HostTrackName = host };
        }
        var track = project.tracks.FirstOrDefault(t => (string?)trackId!.GetValue(t) == id);
        return track is null
            ? connection with { Binding = "missing", HostTrackName = host }
            : connection with { Binding = "bound", TrackId = BridgeHost.Ids.IdOf(track), TrackName = track.TrackName, HostTrackName = host };
    }

    static DawServer Find(List<DawServer> servers, string? target, bool required) {
        if (target is null) {
            if (servers.Count == 0) {
                throw new BridgeException(ErrorCodes.NotFound,
                    "No OpenUtau plugin is running in a DAW. Load the OpenUtau plugin (VST3/CLAP) on a track in the DAW, then scan again.");
            }
            if (required) {
                throw BridgeException.InvalidParams(
                    $"{servers.Count} plugins are running; pass server (one of {string.Join(", ", servers.Select(s => $"'{s.Name}' port {s.Port}"))}).", "server");
            }
            return servers[0];
        }
        return servers.FirstOrDefault(s => Matches(s.Name, s.Port, target))
               ?? throw new BridgeException(ErrorCodes.NotFound, $"No running plugin '{target}'.",
                   new() { ["servers"] = new System.Text.Json.Nodes.JsonArray(servers.Select(s => (System.Text.Json.Nodes.JsonNode?)s.Name).ToArray()) });
    }

    static bool Matches(string name, int port, string target) =>
        string.Equals(name, target, StringComparison.OrdinalIgnoreCase) || port.ToString(System.Globalization.CultureInfo.InvariantCulture) == target;

    static DawState State(OpenUtau.Core.Ustx.UProject project) {
        var manager = DawManager.Inst;
        double? dawBpm = lastDawBpm?.GetValue(manager) is double bpm && !double.IsNaN(bpm) ? bpm : null;
        List<DawServer> scan;
        lock (typeof(DawAdapter)) {
            scan = lastScan;
        }
        return new DawState {
            State = manager.State.ToString(),
            Connections = manager.Connections.Select(c => Describe(c, project)).ToList(),
            Servers = scan.Select(s => new DawServerDto(s.Name, s.Port, s.Info.ApiVersion, s.IsCompatible)).ToList(),
            Vst3Installed = DawBridgeInstaller.FindInstalled(DawBridgeFormat.Vst3) is not null,
            ClapInstalled = DawBridgeInstaller.FindInstalled(DawBridgeFormat.Clap) is not null,
            DawBpm = dawBpm,
            ProjectBpm = project.tempos.Count > 0 ? project.tempos[0].bpm : 120,
            BindingSupported = BindingSupported,
        };
    }
}
