using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Server.Bridge;
using OpenUtauMcp.Server.Units;

namespace OpenUtauMcp.Server.Tools;

/// <summary>
/// Text-first tool responses: one summary sentence, then minified JSON.
/// Errors: "CODE: what happened. What to do next." plus {ok:false,error:{code,message,hint,retryable,details}}.
/// </summary>
public static class ToolResult {
    static readonly JsonSerializerOptions compact = new() {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static CallToolResult Ok(string summary, JsonObject data, IEnumerable<Warning>? warnings = null, JsonObject? extra = null) {
        var body = new JsonObject { ["ok"] = true };
        if (extra is not null) {
            foreach (var (key, value) in extra) {
                body[key] = value?.DeepClone();
            }
        }
        body["data"] = data;
        var list = warnings?.ToList();
        if (list is { Count: > 0 }) {
            body["warnings"] = new JsonArray(list.Select(w => (JsonNode?)new JsonObject {
                ["code"] = w.Code, ["message"] = w.Message, ["ref"] = w.Ref,
            }).ToArray());
        }
        return Text($"{summary}\n{body.ToJsonString(compact)}", isError: false);
    }

    public static CallToolResult Error(string code, string message, string? hint = null, JsonObject? details = null) {
        hint ??= DefaultHint(code);
        var error = new JsonObject {
            ["code"] = code,
            ["message"] = message,
            ["hint"] = hint,
            ["retryable"] = ErrorCodes.IsRetryable(code),
        };
        if (details is { Count: > 0 }) {
            var clean = details.DeepClone().AsObject();
            clean.Remove("code");
            clean.Remove("retryable");
            if (clean.Count > 0) {
                error["details"] = clean;
            }
        }
        string text = hint is null ? $"{code}: {message}" : $"{code}: {message} {hint}";
        return Text($"{text}\n{new JsonObject { ["ok"] = false, ["error"] = error }.ToJsonString(compact)}", isError: true);
    }

    /// <summary>Runs a tool body and converts expected failures into error results.</summary>
    public static async Task<CallToolResult> Guard(Func<Task<CallToolResult>> body) {
        try {
            return await body().ConfigureAwait(false);
        } catch (BridgeCallException e) {
            return Error(e.Code, e.Message, details: e.Details);
        } catch (UnitException e) {
            return Error(e.Code, e.Message);
        } catch (ToolInputException e) {
            return Error(e.Code, e.Message, e.Hint, e.Details);
        }
    }

    static string? DefaultHint(string code) => code switch {
        BridgeCallException.Unavailable =>
            "Start OpenUtau. If it is running, check that the McpBridge plugin is installed in Documents\\OpenUtau\\Plugins\\McpBridge.",
        ErrorCodes.VersionMismatch => "Rebuild the bridge for this OpenUtau: dotnet build src/OpenUtauMcp.Bridge -c Release -t:Install, then restart OpenUtau.",
        ErrorCodes.NotReady => "OpenUtau is still starting; retry in a few seconds.",
        ErrorCodes.NotFound => "Re-read the notes with ou_notes_get to get current ids.",
        ErrorCodes.StaleEpoch => "The project was reopened; re-read it with ou_project_get and ou_notes_get.",
        ErrorCodes.Conflict => "The part changed since you read it; re-read it with ou_notes_get and retry with the new partRev.",
        ErrorCodes.BusyUserEditing => "The user is editing in OpenUtau; retry in a moment.",
        ErrorCodes.UiUnresponsive => "OpenUtau's UI is busy; retry in a moment.",
        ErrorCodes.UnknownExpression =>
            "If ou_catalog_get kind=expressions lists it as suggested, pass ensure:true to ou_curves_edit to register it; otherwise use a registered one.",
        ErrorCodes.OpFailed => "Nothing was changed. Check the failing op and retry.",
        UnitException.BadPosition or UnitException.BadDuration or UnitException.BadPitch => null,
        _ => null,
    };

    static CallToolResult Text(string text, bool isError) => new() {
        Content = [new TextContentBlock { Text = text }],
        IsError = isError,
    };
}

/// <summary>Invalid tool input detected by the server (before calling the bridge).</summary>
public sealed class ToolInputException(string code, string message, string? hint = null, JsonObject? details = null) : Exception(message) {
    public const string AmbiguousRef = "AMBIGUOUS_REF";
    public const string OutsidePart = "OUTSIDE_PART";
    public const string InvalidParams = "INVALID_PARAMS";
    public const string ConfirmationRequired = "CONFIRMATION_REQUIRED";
    public const string LyricsCountMismatch = "LYRICS_COUNT_MISMATCH";

    public string Code { get; } = code;
    public string? Hint { get; } = hint;
    public JsonObject? Details { get; } = details;
}
