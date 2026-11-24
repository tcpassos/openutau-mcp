using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Server.Bridge;

namespace OpenUtauMcp.Server.Tools.Core;

[McpServerToolType]
public static class HistoryTool {
    [McpServerTool(Name = "ou_history", Title = "Undo or redo", ReadOnly = false, Idempotent = false, OpenWorld = false, Destructive = true)]
    [Description("Undo or redo steps in OpenUtau's history (the same as Ctrl+Z / Ctrl+Y, including the user's own edits), " +
                 "or just read the names of the next undo/redo steps. Example: {\"action\":\"undo\",\"count\":1}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge,
        [Description("undo | redo | get")] string action,
        [Description("Steps to undo/redo (1-20).")] int count = 1,
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            if (action == "get") {
                var state = await bridge.HistoryGetAsync(ct).ConfigureAwait(false);
                return ToolResult.Ok($"Next undo: {state.UndoName ?? "(none)"}; next redo: {state.RedoName ?? "(none)"}.",
                    new JsonObject { ["undo"] = state.UndoName, ["redo"] = state.RedoName, ["changesSaved"] = state.ChangesSaved });
            }
            if (action is not ("undo" or "redo")) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"Unknown action '{action}'. Use undo, redo or get.");
            }
            var result = await bridge.HistoryStepAsync(action == "undo", count, ct).ConfigureAwait(false);
            string done = result.Steps.Count == 0 ? $"Nothing to {action}." : $"{(action == "undo" ? "Undid" : "Redid")}: {string.Join("; ", result.Steps)}.";
            return ToolResult.Ok(done, new JsonObject {
                ["steps"] = new JsonArray(result.Steps.Select(s => (JsonNode?)s).ToArray()),
                ["undo"] = result.History.UndoName,
                ["redo"] = result.History.RedoName,
            });
        });
}
