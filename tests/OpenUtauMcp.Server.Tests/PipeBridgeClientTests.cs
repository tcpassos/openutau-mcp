using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Protocol;
using OpenUtauMcp.Server.Bridge;

namespace OpenUtauMcp.Server.Tests;

/// <summary>PipeBridgeClient against a test pipe server that speaks the bridge protocol.</summary>
[Collection(PipeCollection.Name)]
public class PipeBridgeClientTests {
    // A pid that cannot be a real OpenUtau process: the client is pinned to it via the environment.
    const int FakePid = 999_001;

    sealed class TestBridge : IAsyncDisposable {
        readonly CancellationTokenSource cts = new();
        readonly Task loop;
        public int Connections;

        public TestBridge(Func<string, JsonNode?, JsonNode> handle) =>
            loop = Task.Run(async () => {
                while (!cts.IsCancellationRequested) {
                    await using var pipe = new NamedPipeServerStream(ProtocolInfo.PipePrefix + FakePid, PipeDirection.InOut, 2,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    try {
                        await pipe.WaitForConnectionAsync(cts.Token);
                    } catch (OperationCanceledException) {
                        return;
                    }
                    Interlocked.Increment(ref Connections);
                    using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                    await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\n" };
                    try {
                        while (await reader.ReadLineAsync(cts.Token) is { } line) {
                            var req = JsonNode.Parse(line)!.AsObject();
                            string method = req["method"]!.GetValue<string>();
                            if (method == "drop") {
                                break;  // simulate OpenUtau closing
                            }
                            var response = handle(method, req["params"]);
                            response["jsonrpc"] = "2.0";
                            response["id"] = req["id"]!.GetValue<long>();
                            await writer.WriteLineAsync(response.ToJsonString());
                            await writer.FlushAsync();
                        }
                    } catch (Exception e) when (e is IOException or OperationCanceledException) {
                    }
                }
            });

        public async ValueTask DisposeAsync() {
            cts.Cancel();
            await loop;
        }
    }

    static JsonNode Handle(string method, JsonNode? _) => method switch {
        Methods.Hello => new JsonObject {
            ["result"] = JsonSerializer.SerializeToNode(new HelloResult {
                Protocol = 1,
                Bridge = new BridgeInfo { Version = "test", BuiltForCore = "x", BuiltForCoreSha256 = "x", StartedBy = "earlyStart" },
                OpenUtau = new OpenUtauInfo { Version = "0.1.577.11", CoreSha256 = "x", Match = true },
                Capabilities = [], Limits = new BridgeLimits(),
                State = new BridgeState { Ready = true, Epoch = 1, StructureRev = 0 },
            }, ContractsJson.Context.HelloResult),
        },
        Methods.Ping => new JsonObject { ["result"] = new JsonObject { ["ready"] = true, ["epoch"] = 1 } },
        _ => new JsonObject {
            ["error"] = new JsonObject {
                ["code"] = -32010, ["message"] = "Unknown note id 'n9'.",
                ["data"] = new JsonObject { ["code"] = "NOT_FOUND", ["retryable"] = false },
            },
        },
    };

    [Fact]
    public async Task Call_SuccessAndErrorAndReconnect() {
        Environment.SetEnvironmentVariable(ProtocolInfo.PidEnvironmentVariable, FakePid.ToString());
        try {
            await using var bridge = new TestBridge(Handle);
            await WaitForPipeAsync();
            await using var client = new PipeBridgeClient(NullLogger<PipeBridgeClient>.Instance);

            var hello = await client.ConnectAsync(CancellationToken.None);
            Assert.Equal("earlyStart", hello.Bridge.StartedBy);

            var ping = await client.CallAsync(Methods.Ping, null, CancellationToken.None);
            Assert.True(ping!["ready"]!.GetValue<bool>());

            var error = await Assert.ThrowsAsync<BridgeCallException>(() => client.CallAsync("notes.query", null, CancellationToken.None));
            Assert.Equal("NOT_FOUND", error.Code);

            // The bridge drops the connection; the call in flight fails, the next one reconnects.
            await Assert.ThrowsAsync<BridgeCallException>(() => client.CallAsync("drop", null, CancellationToken.None, TimeSpan.FromSeconds(5)));
            var again = await client.CallAsync(Methods.Ping, null, CancellationToken.None);
            Assert.NotNull(again);
            Assert.Equal(2, bridge.Connections);
        } finally {
            Environment.SetEnvironmentVariable(ProtocolInfo.PidEnvironmentVariable, null);
        }
    }

    static async Task WaitForPipeAsync() {
        string name = ProtocolInfo.PipePrefix + FakePid;
        for (int i = 0; i < 100 && !Directory.GetFiles(@"\\.\pipe\").Any(p => p.EndsWith(name, StringComparison.Ordinal)); i++) {
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Connect_NoBridge_IsBridgeUnavailable() {
        Environment.SetEnvironmentVariable(ProtocolInfo.PidEnvironmentVariable, "999002");
        try {
            await using var client = new PipeBridgeClient(NullLogger<PipeBridgeClient>.Instance);

            var e = await Assert.ThrowsAsync<BridgeCallException>(() => client.ConnectAsync(CancellationToken.None));

            Assert.Equal(BridgeCallException.Unavailable, e.Code);
        } finally {
            Environment.SetEnvironmentVariable(ProtocolInfo.PidEnvironmentVariable, null);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PipeCollection {
    public const string Name = "pipes";
}
