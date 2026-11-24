using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using OpenUtauMcp.Contracts.Protocol;
using OpenUtauMcp.Server.Bridge;

namespace OpenUtauMcp.Server;

/// <summary>
/// openutau-mcp doctor [--json]: checks the installation end to end: OpenUtau found,
/// bridge plugin installed, bridge pipe present, handshake and ping, Core match, folders writable.
/// Exit code 0 = everything OK, 1 = at least one failure (warnings do not fail).
/// </summary>
public static class Doctor {
    const string Ok = "ok", Warn = "warn", Fail = "fail";

    sealed record Check(string Name, string Status, string Detail);

    public static async Task<int> RunAsync(bool json) {
        var checks = new List<Check>();
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        // 1. OpenUtau installation (running process first, then the default install folder).
        string? exe = Process.GetProcessesByName("OpenUtau").Select(p => {
            try {
                return p.MainModule?.FileName;
            } catch (Exception) {
                return null;
            }
        }).FirstOrDefault(f => f is not null);
        exe ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenUtau", "OpenUtau.exe");
        if (File.Exists(exe)) {
            string version = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "?";
            checks.Add(new Check("openutau", Ok, $"{exe} ({version})"));
        } else {
            checks.Add(new Check("openutau", Fail, "OpenUtau.exe not found (not running and not in Program Files\\OpenUtau)."));
        }

        // 2. Bridge plugin files.
        string pluginDir = Path.Combine(docs, "OpenUtau", "Plugins", "McpBridge");
        string bridgeDll = Path.Combine(pluginDir, "OpenUtauMcp.Bridge.dll");
        if (File.Exists(bridgeDll)) {
            var extra = Directory.GetFiles(pluginDir, "*.dll").Where(f => !f.Equals(bridgeDll, StringComparison.OrdinalIgnoreCase)).ToList();
            checks.Add(new Check("bridge.installed", extra.Count == 0 ? Ok : Warn,
                extra.Count == 0 ? $"{bridgeDll} ({File.GetLastWriteTime(bridgeDll):yyyy-MM-dd HH:mm})"
                    : $"{bridgeDll}; unexpected extra DLLs: {string.Join(", ", extra.Select(Path.GetFileName))}"));
        } else {
            checks.Add(new Check("bridge.installed", Fail,
                $"{bridgeDll} not found. Install it: dotnet build src/OpenUtauMcp.Bridge -c Release -t:Install (with OpenUtau closed)."));
        }

        // 3. Pipe, handshake, ping.
        string[] pipes;
        try {
            pipes = Directory.GetFiles(@"\\.\pipe\").Select(Path.GetFileName).OfType<string>()
                .Where(n => n.StartsWith(ProtocolInfo.PipePrefix, StringComparison.Ordinal)).ToArray();
        } catch (Exception) {
            pipes = [];
        }
        if (pipes.Length == 0) {
            checks.Add(new Check("bridge.pipe", Fail, "No openutau-mcp pipe: OpenUtau is not running, or the plugin did not load (see OpenUtau's log)."));
        } else {
            checks.Add(new Check("bridge.pipe", pipes.Length == 1 ? Ok : Warn,
                pipes.Length == 1 ? pipes[0] : $"{pipes.Length} pipes ({string.Join(", ", pipes)}); the newest OpenUtau is used."));
            await using var client = new PipeBridgeClient(NullLogger<PipeBridgeClient>.Instance);
            try {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var hello = await client.ConnectAsync(cts.Token).ConfigureAwait(false);
                checks.Add(new Check("bridge.hello", Ok,
                    $"bridge {hello.Bridge.Version} (started by {hello.Bridge.StartedBy}), protocol {hello.Protocol}, OpenUtau {hello.OpenUtau.Version}"));
                checks.Add(new Check("bridge.coreMatch", hello.OpenUtau.Match ? Ok : Fail, hello.OpenUtau.Match
                    ? $"built for this OpenUtau.Core ({hello.Bridge.BuiltForCore})"
                    : $"built for {hello.Bridge.BuiltForCore}, running {hello.OpenUtau.Version}: rebuild and reinstall the bridge."));
                checks.Add(new Check("bridge.ready", hello.State.Ready ? Ok : Warn, hello.State.Ready ? "ready" : "OpenUtau is still starting."));
                var ping = await client.CallAsync(Methods.Ping, null, cts.Token).ConfigureAwait(false);
                double? latency = ping?["uiLatencyMs"]?.GetValue<double>();
                checks.Add(new Check("bridge.ping", latency is > 1000 ? Warn : Ok,
                    latency is null ? "answered" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"UI thread answered in {latency:0.#} ms")));
            } catch (Exception e) {
                checks.Add(new Check("bridge.hello", Fail, $"handshake failed: {e.Message}"));
            }
        }

        // 4. Folders.
        checks.Add(Writable("folder.backups",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenUtauMcp", "backups")));
        string openUtauDocs = Path.Combine(docs, "OpenUtau");
        checks.Add(Directory.Exists(openUtauDocs)
            ? new Check("folder.openutau", Ok, openUtauDocs)
            : new Check("folder.openutau", Warn, $"{openUtauDocs} not found (singers and plugins live there)."));

        if (json) {
            var array = new JsonArray(checks.Select(c => (JsonNode?)new JsonObject {
                ["check"] = c.Name, ["status"] = c.Status, ["detail"] = c.Detail,
            }).ToArray());
            Console.WriteLine(new JsonObject { ["ok"] = checks.All(c => c.Status != Fail), ["checks"] = array }
                .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        } else {
            foreach (var c in checks) {
                Console.WriteLine($"[{c.Status.ToUpperInvariant(),-4}] {c.Name,-18} {c.Detail}");
            }
        }
        return checks.Any(c => c.Status == Fail) ? 1 : 0;
    }

    static Check Writable(string name, string dir) {
        try {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, $".doctor-{Environment.ProcessId}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new Check(name, Ok, dir);
        } catch (Exception e) {
            return new Check(name, Fail, $"{dir} is not writable: {e.Message}");
        }
    }
}
