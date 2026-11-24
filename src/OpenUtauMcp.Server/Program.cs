using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Server.Bridge;

namespace OpenUtauMcp.Server;

/// <summary>
/// openutau-mcp: MCP server that edits the open OpenUtau project through the McpBridge plugin.
///   openutau-mcp [serve]                 MCP over stdio (default)
///   openutau-mcp call &lt;tool&gt; '&lt;json&gt;'   calls one tool through the same MCP path and prints the result
///   openutau-mcp doctor [--json]         checks the installation (OpenUtau, bridge, pipe, handshake, folders)
///   openutau-mcp install-bridge          builds and installs the bridge from this checkout (OpenUtau closed)
/// </summary>
public static class Program {
    const string Instructions =
        "Controls the OpenUtau editor that is open on this computer. Call ou_status first. " +
        "Positions are absolute bar:beat strings (\"17:2.5\"), durations are quarters (\"0.5q\") or fractions (\"1/8\"), " +
        "pitches are note names (\"A4\"). Note ids (\"n12\") stay valid while the project is open; part ids look like \"p3\". " +
        "Every edit tool call is one undo step in OpenUtau. Read with ou_notes_get before editing.";

    public static async Task<int> Main(string[] args) {
        // Singer and lyric names are not ASCII-only; the default console code page would garble them.
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string command = args.Length > 0 ? args[0] : "serve";
        var allowDirs = new List<string>();
        var rest = new List<string>();
        for (int i = 1; i < args.Length; i++) {
            if (args[i] == "--allow-dir" && i + 1 < args.Length) {
                allowDirs.Add(args[++i]);
            } else {
                rest.Add(args[i]);
            }
        }
        return command switch {
            "serve" => await ServeAsync(rest.ToArray(), allowDirs).ConfigureAwait(false),
            "doctor" => await Doctor.RunAsync(rest.Contains("--json")).ConfigureAwait(false),
            "install-bridge" => await InstallBridge.RunAsync(rest).ConfigureAwait(false),
            "call" when rest.Count is 1 or 2 => await CallAsync(rest[0], rest.Count == 2 ? rest[1] : "{}", allowDirs).ConfigureAwait(false),
            _ => Usage(),
        };
    }

    static int Usage() {
        Console.Error.WriteLine("usage: openutau-mcp [serve] [--allow-dir <folder>]... | openutau-mcp call <tool> '<json arguments>' [--allow-dir <folder>]... | openutau-mcp doctor [--json] | openutau-mcp install-bridge [--openutau-dir <folder>]");
        return 2;
    }

    static void ConfigureServices(IServiceCollection services, IEnumerable<string> allowDirs) {
        services.AddSingleton<PipeBridgeClient>();
        services.AddSingleton<IBridgeClient>(sp => sp.GetRequiredService<PipeBridgeClient>());
        services.AddSingleton(new Safety.PathPolicy(allowDirs));
    }

    internal static IMcpServerBuilder AddServer(IServiceCollection services) {
        // After WithToolsFromAssembly has registered the tools.
        services.PostConfigure<McpServerOptions>(o => SchemaCompactor.Compact(o.ToolCollection ?? []));
        return services.AddMcpServer(o => {
            o.ServerInfo = new Implementation { Name = "openutau", Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" };
            o.ServerInstructions = Instructions;
            // Wrong-shaped, missing or unknown arguments become INVALID_PARAMS instead of the SDK's generic error.
            o.Filters.Request.CallToolFilters.Add(Tools.ToolArguments.Filter);
        }).WithToolsFromAssembly().WithPromptsFromAssembly().WithResourcesFromAssembly();
    }

    static async Task<int> ServeAsync(string[] args, IEnumerable<string> allowDirs) {
        var builder = Host.CreateApplicationBuilder(args);
        // stdout carries the MCP protocol: all logs go to stderr.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        ConfigureServices(builder.Services, allowDirs);
        AddServer(builder.Services).WithStdioServerTransport();
        await builder.Build().RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>Runs the MCP server in-process over in-memory pipes and calls one tool with a real MCP client.</summary>
    static async Task<int> CallAsync(string tool, string json, IEnumerable<string> allowDirs) {
        Dictionary<string, object?> arguments;
        try {
            arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)?
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value) ?? new();
        } catch (JsonException e) {
            Console.Error.WriteLine($"Invalid JSON arguments: {e.Message}");
            return 2;
        }
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        ConfigureServices(builder.Services, allowDirs);
        AddServer(builder.Services).WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        using var host = builder.Build();
        await host.StartAsync().ConfigureAwait(false);

        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream())).ConfigureAwait(false);
        var result = await client.CallToolAsync(tool, arguments).ConfigureAwait(false);
        foreach (var block in result.Content) {
            Console.WriteLine(block is TextContentBlock text ? text.Text : JsonSerializer.Serialize(block));
        }
        await host.StopAsync().ConfigureAwait(false);
        return result.IsError == true ? 1 : 0;
    }
}
