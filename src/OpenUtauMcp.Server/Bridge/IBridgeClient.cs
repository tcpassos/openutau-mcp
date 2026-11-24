using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Contracts.Protocol;

namespace OpenUtauMcp.Server.Bridge;

/// <summary>Raw access to the bridge protocol. Implemented by the named-pipe client and by test fakes.</summary>
public interface IBridgeClient {
    /// <summary>Handshake result of the current connection (connects if needed).</summary>
    Task<HelloResult> ConnectAsync(CancellationToken ct);

    /// <summary>Sends one request and returns its result. Throws <see cref="BridgeCallException"/> on errors.</summary>
    Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct, TimeSpan? timeout = null);
}

/// <summary>Typed helpers over <see cref="IBridgeClient.CallAsync"/>.</summary>
public static class BridgeApi {
    static ContractsJsonContext Json => ContractsJson.Context;

    public static Task<StatusResult> StatusAsync(this IBridgeClient b, CancellationToken ct) =>
        b.CallTypedAsync(Methods.Status, null, Json.StatusResult, ct);

    public static Task<ProjectDto> ProjectGetAsync(this IBridgeClient b, ProjectGetParams p, CancellationToken ct) =>
        b.CallTypedAsync(Methods.ProjectGet, JsonSerializer.SerializeToNode(p, Json.ProjectGetParams), Json.ProjectDto, ct);

    public static Task<NotesQueryResult> NotesQueryAsync(this IBridgeClient b, NotesQueryParams p, CancellationToken ct) =>
        b.CallTypedAsync(Methods.NotesQuery, JsonSerializer.SerializeToNode(p, Json.NotesQueryParams), Json.NotesQueryResult, ct);

    public static Task<EditApplyResult> EditApplyAsync(this IBridgeClient b, EditApplyParams p, CancellationToken ct) =>
        b.CallTypedAsync(Methods.EditApply, JsonSerializer.SerializeToNode(p, Json.EditApplyParams), Json.EditApplyResult, ct);

    public static Task<HistoryState> HistoryGetAsync(this IBridgeClient b, CancellationToken ct) =>
        b.CallTypedAsync(Methods.HistoryGet, null, Json.HistoryState, ct);

    public static Task<HistoryStepResult> HistoryStepAsync(this IBridgeClient b, bool undo, int count, CancellationToken ct) =>
        b.CallTypedAsync(undo ? Methods.HistoryUndo : Methods.HistoryRedo,
            JsonSerializer.SerializeToNode(new HistoryStepParams { Count = count }, Json.HistoryStepParams), Json.HistoryStepResult, ct);

    public static Task<ProjectOpenResult> ProjectOpenAsync(this IBridgeClient b, ProjectOpenParams p, CancellationToken ct) =>
        b.CallTypedAsync(Methods.ProjectOpen, JsonSerializer.SerializeToNode(p, Json.ProjectOpenParams), Json.ProjectOpenResult, ct,
            TimeSpan.FromSeconds(90));

    public static Task<ProjectSaveResult> ProjectSaveAsync(this IBridgeClient b, ProjectSaveParams p, CancellationToken ct) =>
        b.CallTypedAsync(Methods.ProjectSave, JsonSerializer.SerializeToNode(p, Json.ProjectSaveParams), Json.ProjectSaveResult, ct);

    public static Task<BackupResult> ProjectBackupAsync(this IBridgeClient b, string reason, CancellationToken ct) =>
        b.CallTypedAsync(Methods.ProjectBackup, JsonSerializer.SerializeToNode(new ProjectBackupParams { Reason = reason }, Json.ProjectBackupParams),
            Json.BackupResult, ct);

    public static Task<PlaybackStateDto> PlaybackGetAsync(this IBridgeClient b, CancellationToken ct) =>
        b.CallTypedAsync(Methods.PlaybackGet, null, Json.PlaybackStateDto, ct);

    public static Task<PlaybackStateDto> PlaybackControlAsync(this IBridgeClient b, PlaybackControlParams p, CancellationToken ct) =>
        b.CallTypedAsync(Methods.PlaybackControl, JsonSerializer.SerializeToNode(p, Json.PlaybackControlParams), Json.PlaybackStateDto, ct);

    public static Task<JobDto> JobStartAsync(this IBridgeClient b, JobStartParams p, CancellationToken ct) =>
        b.CallTypedAsync(Methods.JobsStart, JsonSerializer.SerializeToNode(p, Json.JobStartParams), Json.JobDto, ct);

    public static Task<JobDto> JobGetAsync(this IBridgeClient b, string jobId, CancellationToken ct) =>
        b.CallTypedAsync(Methods.JobsGet, JsonSerializer.SerializeToNode(new JobIdParams(jobId), Json.JobIdParams), Json.JobDto, ct);

    public static Task<JobDto> JobWaitAsync(this IBridgeClient b, string jobId, TimeSpan wait, CancellationToken ct) =>
        b.CallTypedAsync(Methods.JobsWait,
            JsonSerializer.SerializeToNode(new JobWaitParams { JobId = jobId, TimeoutMs = (int)wait.TotalMilliseconds }, Json.JobWaitParams),
            Json.JobDto, ct, wait + TimeSpan.FromSeconds(10));

    public static Task<JobDto> JobCancelAsync(this IBridgeClient b, string jobId, CancellationToken ct) =>
        b.CallTypedAsync(Methods.JobsCancel, JsonSerializer.SerializeToNode(new JobIdParams(jobId), Json.JobIdParams), Json.JobDto, ct);

    public static Task<ProjectOpenResult> ProjectNewAsync(this IBridgeClient b, string onUnsaved, CancellationToken ct) =>
        b.CallTypedAsync(Methods.ProjectNew, JsonSerializer.SerializeToNode(new ProjectNewParams { OnUnsaved = onUnsaved }, Json.ProjectNewParams),
            Json.ProjectOpenResult, ct, TimeSpan.FromSeconds(90));

    public static Task<CurvesQueryResult> CurvesQueryAsync(this IBridgeClient b, CurvesQueryParams p, CancellationToken ct) =>
        b.CallTypedAsync(Methods.CurvesQuery, JsonSerializer.SerializeToNode(p, Json.CurvesQueryParams), Json.CurvesQueryResult, ct);

    public static Task<PhrasesQueryResult> PhrasesQueryAsync(this IBridgeClient b, PhrasesQueryParams p, CancellationToken ct) =>
        b.CallTypedAsync(Methods.PhrasesQuery, JsonSerializer.SerializeToNode(p, Json.PhrasesQueryParams), Json.PhrasesQueryResult, ct,
            TimeSpan.FromMilliseconds((p.AwaitMs ?? 0) + 30000));

    public static Task<BatchRunResult> BatchRunAsync(this IBridgeClient b, BatchRunParams p, CancellationToken ct) =>
        b.CallTypedAsync(Methods.BatchRun, JsonSerializer.SerializeToNode(p, Json.BatchRunParams), Json.BatchRunResult, ct,
            TimeSpan.FromSeconds(60));

    public static Task<ProjectImportResult> ProjectImportAsync(this IBridgeClient b, ProjectImportParams p, CancellationToken ct) =>
        b.CallTypedAsync(Methods.ProjectImportTracks, JsonSerializer.SerializeToNode(p, Json.ProjectImportParams), Json.ProjectImportResult, ct,
            TimeSpan.FromSeconds(90));

    public static Task<ProjectExportResult> ProjectExportAsync(this IBridgeClient b, ProjectExportParams p, CancellationToken ct) =>
        b.CallTypedAsync(Methods.ProjectExport, JsonSerializer.SerializeToNode(p, Json.ProjectExportParams), Json.ProjectExportResult, ct,
            TimeSpan.FromSeconds(60));

    public static Task<SingersCatalog> CatalogSingersAsync(this IBridgeClient b, CatalogListParams p, CancellationToken ct) =>
        b.CallTypedAsync(Methods.CatalogSingers, JsonSerializer.SerializeToNode(p, Json.CatalogListParams), Json.SingersCatalog, ct);

    public static Task<PhonemizersCatalog> CatalogPhonemizersAsync(this IBridgeClient b, CatalogListParams p, CancellationToken ct) =>
        b.CallTypedAsync(Methods.CatalogPhonemizers, JsonSerializer.SerializeToNode(p, Json.CatalogListParams), Json.PhonemizersCatalog, ct);

    public static Task<RenderersCatalog> CatalogRenderersAsync(this IBridgeClient b, CancellationToken ct) =>
        b.CallTypedAsync(Methods.CatalogRenderers, null, Json.RenderersCatalog, ct);

    public static Task<G2pQueryResult> G2pQueryAsync(this IBridgeClient b, G2pQueryParams p, CancellationToken ct) =>
        b.CallTypedAsync(Methods.G2pQuery, JsonSerializer.SerializeToNode(p, Json.G2pQueryParams), Json.G2pQueryResult, ct);

    public static Task<DawState> DawAsync(this IBridgeClient b, string method, string? server, CancellationToken ct, string? trackId = null) =>
        b.CallTypedAsync(method, JsonSerializer.SerializeToNode(new DawTargetParams { Server = server, TrackId = trackId }, Json.DawTargetParams),
            Json.DawState, ct, TimeSpan.FromSeconds(40));

    public static Task<SingerInfo> CatalogSingerAsync(this IBridgeClient b, CatalogSingerParams p, CancellationToken ct) =>
        b.CallTypedAsync(Methods.CatalogSinger, JsonSerializer.SerializeToNode(p, Json.CatalogSingerParams), Json.SingerInfo, ct);

    public static Task<ExpressionsCatalog> CatalogExpressionsAsync(this IBridgeClient b, string trackId, CancellationToken ct) =>
        b.CallTypedAsync(Methods.CatalogExpressions, JsonSerializer.SerializeToNode(new CatalogExpressionsParams(trackId), Json.CatalogExpressionsParams),
            Json.ExpressionsCatalog, ct);

    /// <summary>Reads every note of a voice part, following pagination.</summary>
    public static async Task<NotesQueryResult> AllNotesAsync(this IBridgeClient b, string partId, IReadOnlyList<string>? fields, CancellationToken ct,
                                                             int? awaitMs = null) {
        var notes = new List<NoteDto>();
        var warnings = new List<Warning>();
        NotesQueryResult page;
        int offset = 0;
        do {
            page = await b.NotesQueryAsync(new NotesQueryParams {
                PartId = partId, Fields = fields, Offset = offset, Limit = ProtocolInfo.MaxNotesPerQuery,
                AwaitMs = offset == 0 ? awaitMs : null,  // only the first page waits for derived data
            }, ct).ConfigureAwait(false);
            notes.AddRange(page.Notes);
            warnings.AddRange(page.Warnings ?? []);
            offset = page.NextOffset ?? -1;
        } while (offset >= 0);
        return page with { Notes = notes, NextOffset = null, Warnings = warnings.Count > 0 ? warnings : null };
    }

    static async Task<T> CallTypedAsync<T>(this IBridgeClient b, string method, JsonNode? p, JsonTypeInfo<T> type, CancellationToken ct,
                                           TimeSpan? timeout = null) {
        var node = await b.CallAsync(method, p, ct, timeout).ConfigureAwait(false);
        return node.Deserialize(type) ?? throw new BridgeCallException("INTERNAL", $"Empty result from {method}.", null);
    }
}

/// <summary>Error returned by the bridge (or raised locally, e.g. BRIDGE_UNAVAILABLE).</summary>
public sealed class BridgeCallException(string code, string message, JsonObject? details) : Exception(message) {
    public const string Unavailable = "BRIDGE_UNAVAILABLE";

    public string Code { get; } = code;
    public JsonObject? Details { get; } = details;
}
