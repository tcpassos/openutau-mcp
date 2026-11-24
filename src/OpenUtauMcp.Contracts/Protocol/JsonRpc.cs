using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenUtauMcp.Contracts.Protocol;

/// <summary>JSON-RPC 2.0 request (one NDJSON line).</summary>
public sealed record RpcRequest {
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; init; } = "2.0";
    public long? Id { get; init; }
    public required string Method { get; init; }
    public JsonElement? Params { get; init; }
    public RpcMeta? Meta { get; init; }
}

public sealed record RpcMeta {
    public string? Trace { get; init; }
    public int? TimeoutMs { get; init; }
}

/// <summary>JSON-RPC 2.0 response. Exactly one of <see cref="Result"/> or <see cref="Error"/> is set.</summary>
public sealed record RpcResponse {
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; init; } = "2.0";
    public long? Id { get; init; }
    public JsonNode? Result { get; init; }
    public RpcError? Error { get; init; }
}

public sealed record RpcError {
    public required int Code { get; init; }
    public required string Message { get; init; }
    /// <summary>Always contains "code" (symbolic, see <see cref="Errors.ErrorCodes"/>) and "retryable".</summary>
    public JsonObject? Data { get; init; }
}

/// <summary>Bridge -> client push message (no id).</summary>
public sealed record RpcNotification {
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; init; } = "2.0";
    public string Method { get; init; } = "event";
    public required EventParams Params { get; init; }
}

public sealed record EventParams {
    public required string Topic { get; init; }
    public JsonNode? Data { get; init; }
}

/// <summary>Standard JSON-RPC error numbers plus the single application error number.</summary>
public static class RpcErrorNumbers {
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    /// <summary>All application errors; the symbolic code is in data.code.</summary>
    public const int Application = -32010;
}
