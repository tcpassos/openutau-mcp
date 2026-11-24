using System.IO.Pipes;
using OpenUtauMcp.Contracts.Protocol;
using Serilog;

namespace OpenUtauMcp.Bridge.Transport;

/// <summary>
/// Named pipe server "openutau-mcp-&lt;pid&gt;", current user only.
/// One active client at a time: a new connection replaces the previous one. Two pipe instances
/// exist so a new client can connect while the old one is still open.
/// </summary>
public sealed class PipeServer : IDisposable {
    readonly CancellationTokenSource cts = new();
    Connection? current;

    public string PipeName { get; } = ProtocolInfo.PipePrefix + Environment.ProcessId;

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    async Task AcceptLoopAsync() {
        while (!cts.IsCancellationRequested) {
            NamedPipeServerStream? pipe = null;
            try {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);
                var connection = new Connection(pipe);
                pipe = null;
                Interlocked.Exchange(ref current, connection)?.Dispose();
                _ = connection.RunAsync(cts.Token);
            } catch (OperationCanceledException) {
                pipe?.Dispose();
                return;
            } catch (Exception e) {
                pipe?.Dispose();
                Log.Warning(e, $"{BridgeHost.LogPrefix} pipe accept failed; retrying");
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
    }

    public void Dispose() {
        cts.Cancel();
        Interlocked.Exchange(ref current, null)?.Dispose();
    }
}
