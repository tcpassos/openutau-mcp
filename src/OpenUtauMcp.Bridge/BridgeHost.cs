using System.Reflection;
using System.Security.Cryptography;
using OpenUtau.Core;
using OpenUtauMcp.Bridge.Dispatch;
using OpenUtauMcp.Bridge.Identity;
using OpenUtauMcp.Bridge.Revisions;
using OpenUtauMcp.Bridge.Transport;
using Serilog;

namespace OpenUtauMcp.Bridge;

/// <summary>Process-wide bridge state: pipe server, identity registry, revision tracker.</summary>
public static class BridgeHost {
    public const string StartedByEarlyStart = "earlyStart";
    public const string StartedByBatchEdit = "batchEdit";
    public const string LogPrefix = "[McpBridge]";

    static readonly object gate = new();
    static PipeServer? server;
    static bool subscribed;

    public static string StartedBy { get; private set; } = "";
    public static IdRegistry Ids { get; } = new();
    public static RevisionTracker Revisions { get; } = new();
    public static Events.EventHub Events { get; } = new();
    public static Jobs.JobManager Jobs { get; } = new(Events);

    /// <summary>True while the bridge executes its own undo group (event origin "bridge"). UI thread.</summary>
    public static bool ApplyingFromBridge { get; set; }

    /// <summary>Epoch for which the automatic before-first-edit backup was written.</summary>
    public static int AutoBackupEpoch { get; set; }

    public static string BridgeVersion =>
        typeof(BridgeHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static string OpenUtauVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "unknown";

    static readonly Lazy<string> runningCoreSha256 = new(() => {
        try {
            using var stream = File.OpenRead(typeof(DocManager).Assembly.Location);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        } catch (Exception e) {
            Log.Warning(e, $"{LogPrefix} could not hash OpenUtau.Core.dll");
            return "unknown";
        }
    });

    public static string RunningCoreSha256 => runningCoreSha256.Value;

    /// <summary>True when this bridge was compiled against the OpenUtau.Core.dll that is running.</summary>
    public static bool CoreMatches => RunningCoreSha256 == CoreIdentity.Sha256;

    /// <summary>OpenUtau finished initializing: the UI dispatcher and a project exist.</summary>
    public static bool IsReady {
        get {
            var doc = DocManager.Inst;
            bool ready = doc.PostOnUIThread != null && doc.Project != null;
            if (ready && !subscribed) {
                Subscribe(doc);
            }
            return ready;
        }
    }

    /// <summary>Idempotent: may be called from the attribute constructor and the batch edit constructor.</summary>
    public static void Start(string startedBy) {
        lock (gate) {
            if (server != null) {
                return;
            }
            try {
                StartedBy = startedBy;
                server = new PipeServer();
                server.Start();
                Log.Information($"{LogPrefix} started by {startedBy}, pipe {server.PipeName}, core match {CoreMatches}");
            } catch (Exception e) {
                server = null;
                Log.Error(e, $"{LogPrefix} failed to start");
            }
        }
    }

    public static void Restart() {
        lock (gate) {
            server?.Dispose();
            server = null;
        }
        Start(string.IsNullOrEmpty(StartedBy) ? StartedByBatchEdit : StartedBy);
    }

    static void Subscribe(DocManager doc) {
        lock (gate) {
            if (subscribed) {
                return;
            }
            subscribed = true;
        }
        // Order matters: revisions are updated before events read them.
        doc.AddSubscriber(Revisions);
        doc.AddSubscriber(Events);
    }
}
