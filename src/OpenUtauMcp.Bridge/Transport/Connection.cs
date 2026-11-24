using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUtauMcp.Bridge.Dispatch;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Protocol;
using Serilog;

namespace OpenUtauMcp.Bridge.Transport;

/// <summary>
/// One client connection: NDJSON JSON-RPC 2.0. The first message must be bridge.hello (within 5 s).
/// Requests are dispatched in arrival order (UI work is posted synchronously before the first await),
/// so the UI executes them in that order; responses may complete out of order and carry the request id.
/// </summary>
public sealed class Connection : IDisposable {
    static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);

    readonly NamedPipeServerStream pipe;
    readonly StreamWriter writer;
    readonly SemaphoreSlim writeLock = new(1, 1);
    readonly ConcurrentDictionary<long, CancellationTokenSource> inFlight = new();
    readonly CancellationTokenSource closed = new();
    bool helloDone;

    public Connection(NamedPipeServerStream pipe) {
        this.pipe = pipe;
        writer = new StreamWriter(pipe, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = false };
    }

    public async Task RunAsync(CancellationToken serverToken) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, closed.Token);
        try {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
            while (!linked.IsCancellationRequested) {
                string? line;
                if (!helloDone) {
                    using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    helloCts.CancelAfter(HelloTimeout);
                    line = await reader.ReadLineAsync(helloCts.Token).ConfigureAwait(false);
                } else {
                    line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
                }
                if (line is null) {
                    break;
                }
                if (line.Length > ProtocolInfo.MaxMessageBytes) {
                    await SendErrorAsync(null, ErrorCodes.PayloadTooLarge, "Message exceeds the size limit.", null).ConfigureAwait(false);
                    break;
                }
                if (line.Length == 0) {
                    continue;
                }
                Dispatch(line);
            }
        } catch (OperationCanceledException) {
            // Closed, replaced, or no hello in time.
        } catch (IOException) {
            // Client went away.
        } catch (Exception e) {
            Log.Warning(e, $"{BridgeHost.LogPrefix} connection error");
        } finally {
            Dispose();
        }
    }

    void Dispatch(string line) {
        RpcRequest request;
        try {
            request = JsonSerializer.Deserialize(line, ContractsJson.Context.RpcRequest)
                      ?? throw new JsonException("empty request");
        } catch (JsonException e) {
            _ = SendRpcErrorAsync(null, RpcErrorNumbers.ParseError, $"Invalid JSON: {e.Message}", ErrorCodes.InvalidParams);
            return;
        }

        if (request.Method == Methods.Cancel) {
            var cancel = request.Params?.Deserialize(ContractsJson.Context.CancelParams);
            if (cancel != null && inFlight.TryGetValue(cancel.Id, out var target)) {
                target.Cancel();
            }
            return;
        }
        if (!helloDone && request.Method != Methods.Hello) {
            _ = SendErrorAsync(request.Id, ErrorCodes.InvalidParams, "The first message must be bridge.hello.", null);
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
        if (request.Id is long id) {
            inFlight[id] = cts;
        }
        // Runs synchronously up to the first await, which posts UI work in arrival order.
        _ = HandleAsync(request, cts);
    }

    async Task HandleAsync(RpcRequest request, CancellationTokenSource cts) {
        try {
            JsonNode? result = await Router.DispatchAsync(request.Method, request.Params, cts.Token).ConfigureAwait(false);
            if (request.Method == Methods.Hello) {
                helloDone = true;
            }
            if (request.Id is not null) {
                await SendAsync(new RpcResponse { Id = request.Id, Result = result }).ConfigureAwait(false);
            }
            if (request.Method == Methods.Hello) {
                // Events start after the hello response so the client sees the handshake first.
                BridgeHost.Events.Sink = SendEventAsync;
            }
        } catch (BridgeException e) {
            if (request.Id is not null) {
                await SendErrorAsync(request.Id, e.Code, e.Message, e.Details).ConfigureAwait(false);
            }
        } catch (Exception e) {
            string errorId = Guid.NewGuid().ToString("N")[..8];
            Log.Error(e, $"{BridgeHost.LogPrefix} unexpected error {errorId} in {request.Method}");
            if (request.Id is not null) {
                await SendErrorAsync(request.Id, ErrorCodes.Internal,
                    $"Unexpected bridge error (id {errorId}); details are in the OpenUtau log.",
                    new JsonObject { ["errorId"] = errorId }).ConfigureAwait(false);
            }
        } finally {
            if (request.Id is long id) {
                inFlight.TryRemove(id, out _);
            }
            cts.Dispose();
        }
    }

    Task SendErrorAsync(long? id, string code, string message, JsonObject? details) {
        var data = details?.DeepClone().AsObject() ?? new JsonObject();
        data["code"] = code;
        data["retryable"] = ErrorCodes.IsRetryable(code);
        data["epoch"] ??= BridgeHost.Ids.Epoch;
        int number = code == ErrorCodes.MethodNotFound ? RpcErrorNumbers.MethodNotFound : RpcErrorNumbers.Application;
        return SendAsync(new RpcResponse { Id = id, Error = new RpcError { Code = number, Message = message, Data = data } });
    }

    Task SendRpcErrorAsync(long? id, int number, string message, string code) =>
        SendAsync(new RpcResponse {
            Id = id,
            Error = new RpcError { Code = number, Message = message, Data = new JsonObject { ["code"] = code, ["retryable"] = false } },
        });

    async Task SendAsync(RpcResponse response) {
        string json = JsonSerializer.Serialize(response, ContractsJson.Context.RpcResponse);
        await writeLock.WaitAsync().ConfigureAwait(false);
        try {
            await writer.WriteLineAsync(json).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        } catch (Exception e) when (e is IOException or ObjectDisposedException) {
            // Client disconnected; nothing to report to.
        } finally {
            writeLock.Release();
        }
    }

    async Task SendEventAsync(string topic, JsonNode? data) {
        var notification = new RpcNotification { Method = Methods.Event, Params = new EventParams { Topic = topic, Data = data } };
        string json = JsonSerializer.Serialize(notification, ContractsJson.Context.RpcNotification);
        await writeLock.WaitAsync().ConfigureAwait(false);
        try {
            await writer.WriteLineAsync(json).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        } catch (Exception e) when (e is IOException or ObjectDisposedException) {
            // Client disconnected.
        } finally {
            writeLock.Release();
        }
    }

    public void Dispose() {
        if (closed.IsCancellationRequested) {
            return;
        }
        closed.Cancel();
        if (BridgeHost.Events.Sink == SendEventAsync) {
            BridgeHost.Events.Sink = null;
        }
        try {
            pipe.Dispose();
        } catch (Exception e) {
            Log.Debug(e, $"{BridgeHost.LogPrefix} pipe dispose failed");
        }
    }
}
