using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OpenUtauMcp.Server.Tools;

/// <summary>
/// Confirmation of destructive actions: an explicit confirm parameter, or, when the
/// client supports elicitation, a yes/no question shown to the user. Without either the tool answers
/// CONFIRMATION_REQUIRED.
/// </summary>
public static class Confirmation {
    /// <summary>Asks the user through elicitation. False when the client cannot ask, declines, cancels or fails.</summary>
    public static async Task<bool> AskAsync(McpServer? server, string message, CancellationToken ct) {
        if (server?.ClientCapabilities?.Elicitation is null) {
            return false;
        }
        try {
            var result = await server.ElicitAsync(new ElicitRequestParams {
                Message = message,
                RequestedSchema = new ElicitRequestParams.RequestSchema {
                    Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition> {
                        ["confirm"] = new ElicitRequestParams.BooleanSchema { Title = "Confirm", Description = "Proceed with this change." },
                    },
                    Required = ["confirm"],
                },
            }, ct).ConfigureAwait(false);
            return result.IsAccepted && result.Content is { } content
                && content.TryGetValue("confirm", out var value) && value.ValueKind == JsonValueKind.True;
        } catch (Exception e) when (e is not OperationCanceledException) {
            return false;
        }
    }

    /// <summary>Throws CONFIRMATION_REQUIRED unless already confirmed or the user accepts through elicitation.</summary>
    public static async Task RequireAsync(McpServer? server, bool confirmed, string message, string hint, CancellationToken ct,
                                          System.Text.Json.Nodes.JsonObject? details = null) {
        if (confirmed || await AskAsync(server, message, ct).ConfigureAwait(false)) {
            return;
        }
        throw new ToolInputException(ToolInputException.ConfirmationRequired, message, hint, details);
    }
}
