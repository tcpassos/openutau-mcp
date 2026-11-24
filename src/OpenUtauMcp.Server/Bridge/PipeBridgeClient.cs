using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Protocol;

namespace OpenUtauMcp.Server.Bridge;

/// <summary>
/// Persistent, multiplexed connection to the bridge over the named pipe "openutau-mcp-&lt;pid&gt;".
/// Connects lazily, performs bridge.hello, and reconnects on the next call
/// after the connection is lost. A request is only retried if it was never written to the pipe.
/// </summary>
public sealed class PipeBridgeClient(ILogger<PipeBridgeClient> log) : IBridgeClient, IAsyncDisposable {
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(40);

    readonly SemaphoreSlim connectLock = new(1, 1);
    readonly SemaphoreSlim writeLock = new(1, 1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<RpcResponse>> pending = new();
    Session? session;
    long nextId;

    sealed class Session(NamedPipeClientStream pipe, StreamWriter writer) {
        public NamedPipeClientStream Pipe { get; } = pipe;
        public StreamWriter Writer { get; } = writer;
        public CancellationTokenSource Closed { get; } = new();
        public HelloResult? Hello { get; set; }
    }

    public async Task<HelloResult> ConnectAsync(CancellationToken ct) => (await EnsureSessionAsync(ct).ConfigureAwait(false)).Hello!;

    public async Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct, TimeSpan? timeout = null) {
        for (int attempt = 0; ; attempt++) {
            var s = await EnsureSessionAsync(ct).ConfigureAwait(false);
            try {
                return await SendAsync(s, method, @params, timeout ?? DefaultTimeout, ct).ConfigureAwait(false);
            } catch (NotSentException) when (attempt == 0) {
                Reset(s, "write failed");
            }
        }
    }

    async Task<Session> EnsureSessionAsync(CancellationToken ct) {
        if (session is { Closed.IsCancellationRequested: false } current) {
            return current;
        }
        await connectLock.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (session is { Closed.IsCancellationRequested: false } again) {
                return again;
            }
            return session = await OpenAsync(ct).ConfigureAwait(false);
        } finally {
            connectLock.Release();
        }
    }

    async Task<Session> OpenAsync(CancellationToken ct) {
        string pipeName = FindPipe();
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
        } catch (Exception e) when (e is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException) {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw Unavailable($"Could not connect to the OpenUtau bridge ({pipeName}): {e.Message}");
        }

        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { NewLine = "\n" };
        var temp = new Session(pipe, writer);
        _ = Task.Run(() => ReadLoopAsync(temp));

        var hello = new HelloParams {
            Protocol = new VersionRange(ProtocolInfo.Version, ProtocolInfo.Version),
            Client = new ClientInfo("openutau-mcp", typeof(PipeBridgeClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"),
        };
        JsonNode? result;
        try {
            result = await SendAsync(temp, Methods.Hello, JsonSerializer.SerializeToNode(hello, ContractsJson.Context.HelloParams),
                TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        } catch {
            Reset(temp, "hello failed");
            throw;
        }
        var helloResult = result.Deserialize(ContractsJson.Context.HelloResult)
                          ?? throw new BridgeCallException(ErrorCodes.Internal, "Empty hello result.", null);
        log.LogInformation("Connected to {Pipe}: OpenUtau {Version}, core match {Match}, ready {Ready}",
            pipeName, helloResult.OpenUtau.Version, helloResult.OpenUtau.Match, helloResult.State.Ready);
        temp.Hello = helloResult;
        return temp;
    }

    async Task<JsonNode?> SendAsync(Session s, string method, JsonNode? @params, TimeSpan timeout, CancellationToken ct) {
        long id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        try {
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (@params is not null) {
                request["params"] = @params.DeepClone();
            }
            try {
                await writeLock.WaitAsync(ct).ConfigureAwait(false);
                try {
                    await s.Writer.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);
                    await s.Writer.FlushAsync(ct).ConfigureAwait(false);
                } finally {
                    writeLock.Release();
                }
            } catch (Exception e) when (e is IOException or ObjectDisposedException) {
                throw new NotSentException();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, s.Closed.Token);
            timeoutCts.CancelAfter(timeout);
            RpcResponse response;
            try {
                response = await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                await TrySendCancelAsync(s, id).ConfigureAwait(false);
                throw;
            } catch (OperationCanceledException) when (s.Closed.IsCancellationRequested) {
                throw Unavailable("The connection to OpenUtau was lost (was it closed?).");
            } catch (OperationCanceledException) {
                await TrySendCancelAsync(s, id).ConfigureAwait(false);
                throw new BridgeCallException(ErrorCodes.Timeout, $"OpenUtau did not answer '{method}' within {timeout.TotalSeconds:0} s.", null);
            }
            if (response.Error is { } error) {
                string code = error.Data?["code"]?.GetValue<string>() ?? ErrorCodes.Internal;
                throw new BridgeCallException(code, error.Message, error.Data);
            }
            return response.Result;
        } finally {
            pending.TryRemove(id, out _);
        }
    }

    async Task TrySendCancelAsync(Session s, long id) {
        try {
            var cancel = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = Methods.Cancel, ["params"] = new JsonObject { ["id"] = id } };
            await writeLock.WaitAsync().ConfigureAwait(false);
            try {
                await s.Writer.WriteLineAsync(cancel.ToJsonString()).ConfigureAwait(false);
                await s.Writer.FlushAsync().ConfigureAwait(false);
            } finally {
                writeLock.Release();
            }
        } catch (Exception e) when (e is IOException or ObjectDisposedException) {
            // Connection already gone.
        }
    }

    async Task ReadLoopAsync(Session s) {
        try {
            using var reader = new StreamReader(s.Pipe, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
            while (await reader.ReadLineAsync(s.Closed.Token).ConfigureAwait(false) is { } line) {
                if (line.Length == 0) {
                    continue;
                }
                var node = JsonNode.Parse(line)?.AsObject();
                if (node is null || node["id"] is null) {
                    continue;  // event notifications are not used yet
                }
                var response = node.Deserialize(ContractsJson.Context.RpcResponse);
                if (response?.Id is long id && pending.TryGetValue(id, out var tcs)) {
                    tcs.TrySetResult(response);
                }
            }
        } catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or JsonException) {
            log.LogDebug(e, "Bridge read loop ended");
        } finally {
            Reset(s, "connection closed");
        }
    }

    void Reset(Session s, string reason) {
        if (s.Closed.IsCancellationRequested) {
            return;
        }
        log.LogInformation("Bridge connection reset: {Reason}", reason);
        s.Closed.Cancel();
        try {
            s.Pipe.Dispose();
        } catch (IOException) {
        }
        Interlocked.CompareExchange(ref session, null, s);
    }

    /// <summary>Picks the bridge pipe: the pinned pid, else the most recently started OpenUtau.</summary>
    static string FindPipe() {
        var candidates = new List<(int pid, string name)>();
        foreach (var path in Directory.GetFiles(@"\\.\pipe\")) {
            string name = Path.GetFileName(path);
            if (name.StartsWith(ProtocolInfo.PipePrefix, StringComparison.Ordinal)
                && int.TryParse(name.AsSpan(ProtocolInfo.PipePrefix.Length), out int pid)) {
                candidates.Add((pid, name));
            }
        }
        if (candidates.Count == 0) {
            throw Unavailable("No OpenUtau bridge pipe was found.");
        }
        if (int.TryParse(Environment.GetEnvironmentVariable(ProtocolInfo.PidEnvironmentVariable), out int pinned)) {
            return candidates.FirstOrDefault(c => c.pid == pinned).name
                   ?? throw Unavailable($"No bridge for the pinned OpenUtau pid {pinned}.");
        }
        return candidates.OrderByDescending(c => StartTime(c.pid)).First().name;
    }

    static DateTime StartTime(int pid) {
        try {
            using var p = Process.GetProcessById(pid);
            return p.StartTime;
        } catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) {
            return DateTime.MinValue;
        }
    }

    static BridgeCallException Unavailable(string message) => new(BridgeCallException.Unavailable, message, null);

    sealed class NotSentException : Exception;

    public async ValueTask DisposeAsync() {
        if (session is { } s) {
            Reset(s, "disposed");
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
