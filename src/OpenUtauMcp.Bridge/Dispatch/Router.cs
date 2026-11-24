using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using OpenUtauMcp.Bridge.Adapters;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Protocol;

namespace OpenUtauMcp.Bridge.Dispatch;

/// <summary>Maps protocol methods to adapters. Only bridge.* answers when the Core version does not match.</summary>
public static class Router {
    static readonly string[] capabilities = [
        Capabilities.Edit, Capabilities.History, Capabilities.EarlyStart, Capabilities.Files,
        Capabilities.Playback, Capabilities.JobsExport, Capabilities.Events, Capabilities.Catalog, Capabilities.StructureOps, Capabilities.Derived, Capabilities.JobsBatch, Capabilities.FilesExtra,
        Capabilities.ExportCustom, Capabilities.Daw,
    ];

    static ContractsJsonContext Json => ContractsJson.Context;

    public static async Task<JsonNode?> DispatchAsync(string method, JsonElement? @params, CancellationToken ct) {
        if (!method.StartsWith("bridge.", StringComparison.Ordinal) && !BridgeHost.CoreMatches) {
            throw new BridgeException(ErrorCodes.VersionMismatch,
                "The bridge was built for a different OpenUtau version. Rebuild it: dotnet build src/OpenUtauMcp.Bridge -c Release -t:Install");
        }
        return method switch {
            Methods.Hello => Serialize(Hello(Read(@params, Json.HelloParams)), Json.HelloResult),
            Methods.Ping => Serialize(await PingAsync(ct).ConfigureAwait(false), Json.PingResult),
            Methods.Status => Serialize(await UiDispatcher.RunAsync(ProjectAdapter.Status, ct).ConfigureAwait(false), Json.StatusResult),
            Methods.ProjectGet => Serialize(await UiDispatcher.RunAsync(
                p => ProjectAdapter.Get(p, ReadOptional(@params, Json.ProjectGetParams) ?? new()), ct).ConfigureAwait(false), Json.ProjectDto),
            Methods.NotesQuery => Serialize(await DerivedAdapter.NotesQueryAsync(
                Read(@params, Json.NotesQueryParams), ct).ConfigureAwait(false), Json.NotesQueryResult),
            Methods.ProjectImportTracks => Serialize(await FileAdapter.ImportAsync(
                Read(@params, Json.ProjectImportParams), ct).ConfigureAwait(false), Json.ProjectImportResult),
            Methods.ProjectExport => Serialize(await FileAdapter.ExportAsync(
                Read(@params, Json.ProjectExportParams), ct).ConfigureAwait(false), Json.ProjectExportResult),
            Methods.CatalogSingers => Serialize(await UiDispatcher.RunAsync(
                _ => CatalogLists.Singers(ReadOptional(@params, Json.CatalogListParams) ?? new()), ct).ConfigureAwait(false), Json.SingersCatalog),
            Methods.CatalogPhonemizers => Serialize(await UiDispatcher.RunAsync(
                _ => CatalogLists.Phonemizers(ReadOptional(@params, Json.CatalogListParams) ?? new()), ct).ConfigureAwait(false), Json.PhonemizersCatalog),
            Methods.CatalogRenderers => Serialize(await UiDispatcher.RunAsync(_ => CatalogLists.Renderers(), ct).ConfigureAwait(false), Json.RenderersCatalog),
            Methods.G2pQuery => Serialize(await UiDispatcher.RunAsync(
                p => CatalogLists.G2p(p, Read(@params, Json.G2pQueryParams)), ct).ConfigureAwait(false), Json.G2pQueryResult),
            Methods.DawGet => Serialize(await DawAdapter.GetAsync(ct).ConfigureAwait(false), Json.DawState),
            Methods.DawScan => Serialize(await DawAdapter.ScanAsync(ct).ConfigureAwait(false), Json.DawState),
            Methods.DawConnect => Serialize(await DawAdapter.ConnectAsync(
                ReadOptional(@params, Json.DawTargetParams) ?? new(), ct).ConfigureAwait(false), Json.DawState),
            Methods.DawDisconnect => Serialize(await DawAdapter.DisconnectAsync(
                ReadOptional(@params, Json.DawTargetParams) ?? new(), ct).ConfigureAwait(false), Json.DawState),
            Methods.DawBind => Serialize(await DawAdapter.BindAsync(
                ReadOptional(@params, Json.DawTargetParams) ?? new(), ct).ConfigureAwait(false), Json.DawState),
            Methods.BatchRun => Serialize(await UiDispatcher.RunAsync(
                p => BatchAdapter.Run(p, Read(@params, Json.BatchRunParams)), ct).ConfigureAwait(false), Json.BatchRunResult),
            Methods.PhrasesQuery => Serialize(await DerivedAdapter.PhrasesQueryAsync(
                Read(@params, Json.PhrasesQueryParams), ct).ConfigureAwait(false), Json.PhrasesQueryResult),
            Methods.EditApply => Serialize(await UiDispatcher.RunAsync(
                p => EditAdapter.Apply(p, Read(@params, Json.EditApplyParams)), ct).ConfigureAwait(false), Json.EditApplyResult),
            Methods.HistoryGet => Serialize(await UiDispatcher.RunAsync(_ => HistoryAdapter.Get(), ct).ConfigureAwait(false), Json.HistoryState),
            Methods.HistoryUndo or Methods.HistoryRedo => Serialize(await UiDispatcher.RunAsync(_ => {
                BridgeHost.Jobs.EnsureIdle();
                return HistoryAdapter.Step(method == Methods.HistoryUndo, ReadOptional(@params, Json.HistoryStepParams) ?? new());
            }, ct).ConfigureAwait(false), Json.HistoryStepResult),
            Methods.ProjectOpen => Serialize(await FileAdapter.OpenAsync(
                ReadOptional(@params, Json.ProjectOpenParams) ?? new(), ct).ConfigureAwait(false), Json.ProjectOpenResult),
            Methods.ProjectSave => Serialize(await FileAdapter.SaveAsync(
                ReadOptional(@params, Json.ProjectSaveParams) ?? new(), ct).ConfigureAwait(false), Json.ProjectSaveResult),
            Methods.ProjectBackup => Serialize(await FileAdapter.BackupAsync(
                ReadOptional(@params, Json.ProjectBackupParams) ?? new(), ct).ConfigureAwait(false), Json.BackupResult),
            Methods.PlaybackGet => Serialize(await UiDispatcher.RunAsync(_ => PlaybackAdapter.Get(), ct).ConfigureAwait(false), Json.PlaybackStateDto),
            Methods.PlaybackControl => Serialize(await UiDispatcher.RunAsync(
                p => PlaybackAdapter.Control(p, Read(@params, Json.PlaybackControlParams)), ct).ConfigureAwait(false), Json.PlaybackStateDto),
            Methods.JobsStart => Serialize(BridgeHost.Jobs.Start(Read(@params, Json.JobStartParams)), Json.JobDto),
            Methods.JobsGet => Serialize(BridgeHost.Jobs.Get(Read(@params, Json.JobIdParams).JobId), Json.JobDto),
            Methods.JobsWait => Serialize(await WaitJobAsync(Read(@params, Json.JobWaitParams), ct).ConfigureAwait(false), Json.JobDto),
            Methods.JobsCancel => Serialize(BridgeHost.Jobs.Cancel(Read(@params, Json.JobIdParams).JobId), Json.JobDto),
            Methods.ProjectNew => Serialize(await FileAdapter.NewAsync(
                ReadOptional(@params, Json.ProjectNewParams) ?? new(), ct).ConfigureAwait(false), Json.ProjectOpenResult),
            Methods.CurvesQuery => Serialize(await UiDispatcher.RunAsync(
                p => CurvesAdapter.Query(p, Read(@params, Json.CurvesQueryParams)), ct).ConfigureAwait(false), Json.CurvesQueryResult),
            Methods.CatalogSinger => Serialize(await UiDispatcher.RunAsync(
                p => Catalog.Singer(p, Read(@params, Json.CatalogSingerParams)), ct).ConfigureAwait(false), Json.SingerInfo),
            Methods.CatalogExpressions => Serialize(await UiDispatcher.RunAsync(
                p => Catalog.Expressions(p, Read(@params, Json.CatalogExpressionsParams)), ct).ConfigureAwait(false), Json.ExpressionsCatalog),
            _ => throw new BridgeException(ErrorCodes.MethodNotFound, $"Unknown method '{method}'."),
        };
    }

    static Task<Contracts.Model.JobDto> WaitJobAsync(Contracts.Model.JobWaitParams p, CancellationToken ct) =>
        BridgeHost.Jobs.WaitAsync(p.JobId, p.TimeoutMs, ct);

    static HelloResult Hello(HelloParams p) {
        if (p.Protocol.Min > ProtocolInfo.Version || p.Protocol.Max < ProtocolInfo.Version) {
            throw new BridgeException(ErrorCodes.ProtocolMismatch,
                $"No common protocol version: client supports {p.Protocol.Min}..{p.Protocol.Max}, bridge supports {ProtocolInfo.Version}.",
                new JsonObject { ["supported"] = new JsonArray(ProtocolInfo.Version) });
        }
        return new HelloResult {
            Protocol = ProtocolInfo.Version,
            Bridge = new BridgeInfo {
                Version = BridgeHost.BridgeVersion,
                BuiltForCore = CoreIdentity.Sha256[..12],
                BuiltForCoreSha256 = CoreIdentity.Sha256,
                StartedBy = BridgeHost.StartedBy,
            },
            OpenUtau = new OpenUtauInfo {
                Version = BridgeHost.OpenUtauVersion,
                CoreSha256 = BridgeHost.RunningCoreSha256,
                Match = BridgeHost.CoreMatches,
            },
            Capabilities = capabilities,
            Limits = new BridgeLimits(),
            State = new BridgeState {
                Ready = BridgeHost.IsReady,
                Epoch = BridgeHost.Ids.Epoch,
                StructureRev = BridgeHost.Revisions.StructureRev,
            },
        };
    }

    static async Task<PingResult> PingAsync(CancellationToken ct) {
        if (!BridgeHost.IsReady) {
            return new PingResult { Ready = false, Epoch = BridgeHost.Ids.Epoch };
        }
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        await UiDispatcher.RunAsync(_ => 0, ct).ConfigureAwait(false);
        return new PingResult {
            Ready = true,
            Epoch = BridgeHost.Ids.Epoch,
            UiLatencyMs = Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds, 1),
        };
    }

    static T Read<T>(JsonElement? @params, JsonTypeInfo<T> type) =>
        ReadOptional(@params, type) ?? throw BridgeException.InvalidParams("Missing params.");

    static T? ReadOptional<T>(JsonElement? @params, JsonTypeInfo<T> type) {
        if (@params is not { ValueKind: JsonValueKind.Object } element) {
            return default;
        }
        try {
            return element.Deserialize(type);
        } catch (JsonException e) {
            throw BridgeException.InvalidParams($"Invalid params: {e.Message}", e.Path);
        }
    }

    static JsonNode? Serialize<T>(T value, JsonTypeInfo<T> type) => JsonSerializer.SerializeToNode(value, type);
}
