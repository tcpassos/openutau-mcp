using System.Text.Json.Nodes;
using OpenUtauMcp.Contracts.Errors;

namespace OpenUtauMcp.Bridge;

/// <summary>An expected failure with a symbolic error code; becomes a JSON-RPC application error.</summary>
public sealed class BridgeException : Exception {
    public string Code { get; }
    public JsonObject Details { get; }

    public BridgeException(string code, string message, JsonObject? details = null) : base(message) {
        Code = code;
        Details = details ?? new JsonObject();
    }

    public static BridgeException InvalidParams(string message, string? path = null) =>
        new(ErrorCodes.InvalidParams, message, path is null ? null : new JsonObject { ["path"] = path });

    public static BridgeException OutOfRange(string path, double value, double min, double max) =>
        new(ErrorCodes.OutOfRange, $"{path} = {value} is outside [{min}, {max}].",
            new JsonObject { ["path"] = path, ["value"] = value, ["min"] = min, ["max"] = max });

    public static BridgeException NotFound(string kind, string reference, string reason) =>
        new(ErrorCodes.NotFound,
            reason == "removed" ? $"The {kind} {reference} no longer exists (it was removed)." : $"Unknown {kind} id '{reference}'.",
            new JsonObject { ["kind"] = kind, ["ref"] = reference, ["reason"] = reason });
}
