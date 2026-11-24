using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Protocol;
using OpenUtauMcp.Server.Bridge;

namespace OpenUtauMcp.Server.Tests;

/// <summary>Scriptable bridge: each method returns a canned result or throws; every call is recorded.</summary>
public sealed class FakeBridge : IBridgeClient {
    readonly Dictionary<string, Func<JsonNode?, JsonNode?>> handlers = new();

    public List<(string method, JsonNode? @params)> Calls { get; } = new();

    public FakeBridge On<T>(string method, Func<JsonNode?, T> handler, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) {
        handlers[method] = p => JsonSerializer.SerializeToNode(handler(p), type);
        return this;
    }

    public FakeBridge Fail(string method, string code, string message) {
        handlers[method] = _ => throw new BridgeCallException(code, message, new JsonObject { ["code"] = code });
        return this;
    }

    public Task<HelloResult> ConnectAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct, TimeSpan? timeout = null) {
        Calls.Add((method, @params?.DeepClone()));
        return handlers.TryGetValue(method, out var handler)
            ? Task.FromResult(handler(@params))
            : throw new InvalidOperationException($"FakeBridge: no handler for {method}");
    }

    /// <summary>A 4/4 project with one voice part "Lead" (p1) starting at bar 9.</summary>
    public static FakeBridge WithProject() => new FakeBridge().On(Methods.ProjectGet, _ => new ProjectDto {
        Epoch = 1, StructureRev = 1, Name = "Test", Resolution = 480,
        Tempos = [new TempoDto(0, 120)],
        TimeSignatures = [new TimeSignatureDto(0, 4, 4)],
        Tracks = [new TrackDto { Id = "t1", Index = 0, Name = "Vocal" }],
        Parts = [new PartDto { Id = "p1", Name = "Lead", TrackId = "t1", Type = "voice", Pos = 15360, Dur = 7680, NoteCount = 2 }],
    }, ContractsJson.Context.ProjectDto);
}
