using System.Text.Json;
using System.Text.Json.Serialization;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Contracts.Protocol;

namespace OpenUtauMcp.Contracts;

/// <summary>
/// Source-generated JSON metadata for every protocol type: camelCase, enums as strings,
/// nulls omitted, unknown fields ignored, "op" discriminator accepted in any position.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    AllowOutOfOrderMetadataProperties = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RpcRequest))]
[JsonSerializable(typeof(RpcResponse))]
[JsonSerializable(typeof(RpcNotification))]
[JsonSerializable(typeof(HelloParams))]
[JsonSerializable(typeof(HelloResult))]
[JsonSerializable(typeof(PingResult))]
[JsonSerializable(typeof(StatusResult))]
[JsonSerializable(typeof(ProjectGetParams))]
[JsonSerializable(typeof(ProjectDto))]
[JsonSerializable(typeof(NotesQueryParams))]
[JsonSerializable(typeof(NotesQueryResult))]
[JsonSerializable(typeof(EditApplyParams))]
[JsonSerializable(typeof(EditApplyResult))]
[JsonSerializable(typeof(HistoryState))]
[JsonSerializable(typeof(HistoryStepParams))]
[JsonSerializable(typeof(HistoryStepResult))]
[JsonSerializable(typeof(CancelParams))]
[JsonSerializable(typeof(ProjectOpenParams))]
[JsonSerializable(typeof(ProjectOpenResult))]
[JsonSerializable(typeof(ProjectSaveParams))]
[JsonSerializable(typeof(ProjectSaveResult))]
[JsonSerializable(typeof(ProjectBackupParams))]
[JsonSerializable(typeof(BackupResult))]
[JsonSerializable(typeof(PlaybackControlParams))]
[JsonSerializable(typeof(PlaybackStateDto))]
[JsonSerializable(typeof(JobStartParams))]
[JsonSerializable(typeof(JobIdParams))]
[JsonSerializable(typeof(JobWaitParams))]
[JsonSerializable(typeof(JobDto))]
[JsonSerializable(typeof(CurvesQueryParams))]
[JsonSerializable(typeof(CurvesQueryResult))]
[JsonSerializable(typeof(PhrasesQueryParams))]
[JsonSerializable(typeof(PhrasesQueryResult))]
[JsonSerializable(typeof(BatchRunParams))]
[JsonSerializable(typeof(BatchRunResult))]
[JsonSerializable(typeof(ProjectImportParams))]
[JsonSerializable(typeof(ProjectImportResult))]
[JsonSerializable(typeof(ProjectExportParams))]
[JsonSerializable(typeof(ProjectExportResult))]
[JsonSerializable(typeof(CatalogListParams))]
[JsonSerializable(typeof(SingersCatalog))]
[JsonSerializable(typeof(PhonemizersCatalog))]
[JsonSerializable(typeof(RenderersCatalog))]
[JsonSerializable(typeof(G2pQueryParams))]
[JsonSerializable(typeof(G2pQueryResult))]
[JsonSerializable(typeof(DawState))]
[JsonSerializable(typeof(DawTargetParams))]
[JsonSerializable(typeof(CatalogSingerParams))]
[JsonSerializable(typeof(SingerInfo))]
[JsonSerializable(typeof(CatalogExpressionsParams))]
[JsonSerializable(typeof(ExpressionsCatalog))]
[JsonSerializable(typeof(ProjectNewParams))]
public sealed partial class ContractsJsonContext : JsonSerializerContext;

public sealed record CancelParams(long Id);

public static class ContractsJson {
    public static ContractsJsonContext Context => ContractsJsonContext.Default;
    public static JsonSerializerOptions Options => ContractsJsonContext.Default.Options;
}
