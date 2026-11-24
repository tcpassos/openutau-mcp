using System.Text.Json.Nodes;
using System.Threading.Channels;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtauMcp.Contracts.Protocol;
using Serilog;

namespace OpenUtauMcp.Bridge.Events;

/// <summary>
/// Observes OpenUtau commands: keeps a numbered log of
/// ErrorMessageNotifications (OpenUtau reports export/save failures only this way), forwards export
/// progress to the running job, and pushes events to the connected client. OnNext runs on the UI thread
/// under DocManager's lock: it only records and enqueues; a background pump does the writing.
/// </summary>
public sealed class EventHub : ICmdSubscriber {
    const int MaxErrors = 20;
    static readonly TimeSpan ChangeDebounce = TimeSpan.FromMilliseconds(100);

    readonly object gate = new();
    readonly List<(long seq, string message)> errors = new();
    long errorSeq;

    readonly Channel<(string topic, JsonNode? data)> outbox =
        Channel.CreateBounded<(string, JsonNode?)>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest });
    readonly Dictionary<string, int> changedParts = new();
    bool changeFromOther;
    Timer? changeTimer;

    /// <summary>Writes one event to the current client; set by the connection after hello.</summary>
    public Func<string, JsonNode?, Task>? Sink { get; set; }

    /// <summary>Raised (on the UI thread) for every ProgressBarNotification.</summary>
    public event Action<string>? Progress;

    public EventHub() => _ = Task.Run(PumpAsync);

    public long ErrorSeq {
        get { lock (gate) { return errorSeq; } }
    }

    public IReadOnlyList<string> ErrorsSince(long seq) {
        lock (gate) {
            return errors.Where(e => e.seq > seq).Select(e => e.message).ToList();
        }
    }

    public IReadOnlyList<string> RecentErrors(int count) {
        lock (gate) {
            return errors.Skip(Math.Max(0, errors.Count - count)).Select(e => e.message).ToList();
        }
    }

    public void Publish(string topic, JsonNode? data) => outbox.Writer.TryWrite((topic, data));

    public void OnNext(UCommand cmd, bool isUndo) {
        switch (cmd) {
            case ErrorMessageNotification error:
                string message = Describe(error);
                lock (gate) {
                    errors.Add((++errorSeq, message));
                    if (errors.Count > MaxErrors) {
                        errors.RemoveAt(0);
                    }
                }
                Publish(EventTopics.Error, new JsonObject { ["message"] = message });
                return;
            case ProgressBarNotification progress:
                Progress?.Invoke(progress.Info);
                return;
            case LoadProjectNotification load:
                var project = BridgeHost.Ids.Sync(load.project);
                Publish(EventTopics.ProjectLoaded, new JsonObject {
                    ["epoch"] = BridgeHost.Ids.Epoch,
                    ["name"] = project.name,
                    ["filePath"] = string.IsNullOrEmpty(project.FilePath) ? null : project.FilePath,
                });
                return;
            case UNotification:
                return;
            default:
                RecordChange(cmd);
                return;
        }
    }

    void RecordChange(UCommand cmd) {
        UPart? part = cmd switch {
            NoteCommand n => n.Part,
            ExpCommand e => e.Part,
            PartCommand p => p.part,
            _ => null,
        };
        lock (gate) {
            if (part != null) {
                changedParts[BridgeHost.Ids.IdOf(part)] = BridgeHost.Revisions.PartRev(part);
            }
            changeFromOther |= !BridgeHost.ApplyingFromBridge;
            changeTimer ??= new Timer(_ => FlushChanges(), null, ChangeDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    void FlushChanges() {
        JsonObject data;
        lock (gate) {
            var revs = new JsonObject();
            foreach (var (id, rev) in changedParts) {
                revs[id] = rev;
            }
            data = new JsonObject {
                ["partRevs"] = revs,
                ["structureRev"] = BridgeHost.Revisions.StructureRev,
                ["origin"] = changeFromOther ? "other" : "bridge",
            };
            changedParts.Clear();
            changeFromOther = false;
            changeTimer?.Dispose();
            changeTimer = null;
        }
        Publish(EventTopics.ProjectChanged, data);
    }

    async Task PumpAsync() {
        await foreach (var (topic, data) in outbox.Reader.ReadAllAsync().ConfigureAwait(false)) {
            var sink = Sink;
            if (sink is null) {
                continue;  // no client: events are dropped
            }
            try {
                await sink(topic, data).ConfigureAwait(false);
            } catch (Exception e) {
                Log.Debug(e, $"{BridgeHost.LogPrefix} event delivery failed");
            }
        }
    }

    static string Describe(ErrorMessageNotification n) {
        if (n.e is null) {
            return n.message;
        }
        string text = n.e.Message;
        // OpenUtau wraps the real cause in MessageCustomizableException.SubstanceException.
        var cause = n.e is MessageCustomizableException wrapped ? wrapped.SubstanceException : n.e.InnerException;
        if (cause is not null && !text.Contains(cause.Message, StringComparison.Ordinal)) {
            text += $" ({cause.GetType().Name}: {cause.Message})";
        }
        return string.IsNullOrEmpty(n.message) ? text : $"{n.message}: {text}";
    }
}
