using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Protocol;
using OpenUtauMcp.Server.Bridge;
using OpenUtauMcp.Server.Safety;

namespace OpenUtauMcp.Server.Tests;

/// <summary>
/// Arguments the SDK cannot bind (wrong shape, missing, unknown) come back as INVALID_PARAMS with the argument,
/// the JSON path and the expected schema, through a real MCP client and server, before the tool or bridge runs.
/// </summary>
public sealed class ToolArgumentsTests : IAsyncLifetime {
    readonly FakeBridge bridge = FakeBridge.WithProject()
        .On(Methods.Status, _ => new StatusResult {
            Project = new StatusProject { Name = "T", ChangesSaved = true, Epoch = 1, StructureRev = 1, Tracks = 1, Parts = 1 },
            Playback = new StatusPlayback("stopped", 0),
            History = new HistoryState { CanUndo = false, CanRedo = false, ChangesSaved = true },
        }, ContractsJson.Context.StatusResult);

    IHost? host;
    McpClient? client;

    public async Task InitializeAsync() {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IBridgeClient>(bridge);
        builder.Services.AddSingleton(new PathPolicy([]));
        Program.AddServer(builder.Services)
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        host = builder.Build();
        await host.StartAsync();
        client = await McpClient.CreateAsync(new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));
    }

    public async Task DisposeAsync() {
        if (client is not null) {
            await client.DisposeAsync();
        }
        if (host is not null) {
            await host.StopAsync();
            host.Dispose();
        }
    }

    async Task<string> Call(string tool, string json) {
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;
        var result = await client!.CallToolAsync(tool, arguments);
        return ((TextContentBlock)result.Content[0]).Text;
    }

    [Fact]
    public async Task WrongShape_NamesTheArgumentAndTheExpectedSchema() {
        string text = await Call("ou_project_set", """{"tempoChanges":[{"at":"9:1","bpm":130}]}""");

        Assert.StartsWith("INVALID_PARAMS: 'tempoChanges' has the wrong shape for ou_project_set: got a list.", text);
        Assert.Contains("Expected: {\"description\":\"Tempo changes: {add:[{at, bpm}], remove:[at]}.\",\"type\":\"object\"", text);
        Assert.DoesNotContain(bridge.Calls, c => c.method == Methods.EditApply);
    }

    [Fact]
    public async Task NestedWrongType_PointsAtTheField() {
        string text = await Call("ou_project_set", """{"tempoChanges":{"add":[{"at":"9:1","bpm":"fast"}]}}""");

        Assert.StartsWith("INVALID_PARAMS: 'tempoChanges.add[0].bpm' has the wrong shape for ou_project_set: got the string \"fast\".", text);
        Assert.Contains("Expected: {\"description\":\"BPM (10..1000).\",\"type\":\"number\"}", text);
        Assert.Contains("\"path\":\"tempoChanges.add[0].bpm\"", text);
    }

    [Fact]
    public async Task MissingRequired_SaysWhichArgument() {
        string text = await Call("ou_render", "{}");

        Assert.StartsWith("INVALID_PARAMS: ou_render needs the argument 'action'.", text);
        Assert.Contains("export | status | wait | cancel", text);
    }

    [Theory]
    [InlineData("""{"select":{"part":"p1"},"waitMs":5}""", "has no argument 'waitMs'; did you mean 'awaitMs'?")]
    [InlineData("""{"Select":{"part":"p1"}}""", "has no argument 'Select'; did you mean 'select'?")]
    [InlineData("""{"select":{"part":"p1"},"banana":1}""", "has no argument 'banana'. Arguments: select, detail")]
    public async Task UnknownArgument_IsRejectedWithASuggestion(string json, string expected) {
        string text = await Call("ou_notes_get", json);

        Assert.StartsWith("INVALID_PARAMS: ou_notes_get " + expected, text);
        Assert.DoesNotContain(bridge.Calls, c => c.method == Methods.NotesQuery);
    }

    [Fact]
    public async Task ValidArguments_ReachTheTool() {
        string text = await Call("ou_project_get", """{"include":["tracks"]}""");

        Assert.StartsWith("Project ", text);
        Assert.Contains(bridge.Calls, c => c.method == Methods.ProjectGet);
    }
}
