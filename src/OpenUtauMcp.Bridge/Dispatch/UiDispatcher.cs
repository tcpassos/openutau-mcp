using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtauMcp.Contracts.Errors;

namespace OpenUtauMcp.Bridge.Dispatch;

/// <summary>
/// Runs model access on OpenUtau's UI thread. Delegates are posted in call order.
/// If a delegate has not started within <see cref="StartTimeout"/> it is abandoned (UI_UNRESPONSIVE);
/// a cancelled request that has not started is abandoned too (CANCELLED). Once started it runs to the end.
/// </summary>
public static class UiDispatcher {
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    const int Pending = 0, Started = 1, Abandoned = 2;

    /// <summary>Runs <paramref name="work"/> on the UI thread with the current project (epoch already synced).</summary>
    public static async Task<T> RunAsync<T>(Func<UProject, T> work, CancellationToken ct) {
        if (!BridgeHost.IsReady) {
            throw new BridgeException(ErrorCodes.NotReady, "OpenUtau is still starting up.");
        }
        var doc = DocManager.Inst;
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        int state = Pending;

        doc.PostOnUIThread(() => {
            if (Interlocked.CompareExchange(ref state, Started, Pending) != Pending) {
                return;
            }
            try {
                var project = BridgeHost.Ids.Sync(doc.Project);
                tcs.SetResult(work(project));
            } catch (Exception e) {
                tcs.SetException(e);
            }
        });

        using var cancelReg = ct.Register(() => {
            if (Interlocked.CompareExchange(ref state, Abandoned, Pending) == Pending) {
                tcs.TrySetException(new BridgeException(ErrorCodes.Cancelled, "The request was cancelled before it ran."));
            }
        });

        var first = await Task.WhenAny(tcs.Task, Task.Delay(StartTimeout, CancellationToken.None)).ConfigureAwait(false);
        if (first != tcs.Task && Interlocked.CompareExchange(ref state, Abandoned, Pending) == Pending) {
            throw new BridgeException(ErrorCodes.UiUnresponsive,
                $"OpenUtau's UI did not respond within {StartTimeout.TotalSeconds:0} s.");
        }
        var done = await Task.WhenAny(tcs.Task, Task.Delay(RunTimeout, CancellationToken.None)).ConfigureAwait(false);
        if (done != tcs.Task) {
            throw new BridgeException(ErrorCodes.Timeout, $"The operation did not finish within {RunTimeout.TotalSeconds:0} s.");
        }
        return await tcs.Task.ConfigureAwait(false);
    }
}
