using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Server.Bridge;

namespace OpenUtauMcp.Server.Tools.Core;

[McpServerToolType]
public static class StatusTool {
    [McpServerTool(Name = "ou_status", Title = "Get OpenUtau status", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Call first in a session. Reports whether the OpenUtau bridge is connected, the open project " +
                 "(name, file, unsaved changes, BPM, track/part counts), playback state and undo/redo names. " +
                 "Never fails: if OpenUtau is not reachable it says so and how to fix it.")]
    public static async Task<CallToolResult> Run(IBridgeClient bridge, CancellationToken ct) {
        Contracts.Protocol.HelloResult hello;
        try {
            hello = await bridge.ConnectAsync(ct).ConfigureAwait(false);
        } catch (BridgeCallException e) {
            return ToolResult.Ok($"OpenUtau is not reachable: {e.Message} Start OpenUtau with the McpBridge plugin installed.",
                new JsonObject { ["bridge"] = new JsonObject { ["connected"] = false, ["error"] = e.Code } });
        }
        var bridgeInfo = new JsonObject {
            ["connected"] = true,
            ["version"] = hello.Bridge.Version,
            ["openUtauVersion"] = hello.OpenUtau.Version,
            ["match"] = hello.OpenUtau.Match,
            ["ready"] = hello.State.Ready,
            ["startedBy"] = hello.Bridge.StartedBy,
        };
        if (!hello.OpenUtau.Match) {
            return ToolResult.Error("VERSION_MISMATCH",
                $"The bridge was built for a different OpenUtau.Core than the running OpenUtau {hello.OpenUtau.Version}.",
                details: new JsonObject { ["bridge"] = bridgeInfo });
        }
        if (!hello.State.Ready) {
            return ToolResult.Ok("Connected, but OpenUtau is still starting up. Retry in a few seconds.",
                new JsonObject { ["bridge"] = bridgeInfo });
        }

        return await ToolResult.Guard(async () => {
            var status = await bridge.StatusAsync(ct).ConfigureAwait(false);
            var p = status.Project;
            var data = new JsonObject {
                ["bridge"] = bridgeInfo,
                ["project"] = new JsonObject {
                    ["name"] = p.Name,
                    ["filePath"] = p.FilePath,
                    ["changesSaved"] = p.ChangesSaved,
                    ["bpm"] = p.Bpm,
                    ["tracks"] = p.Tracks,
                    ["parts"] = p.Parts,
                    ["epoch"] = p.Epoch,
                },
                ["playback"] = new JsonObject { ["state"] = status.Playback.State },
                ["history"] = new JsonObject {
                    ["undo"] = status.History.UndoName,
                    ["redo"] = status.History.RedoName,
                },
            };
            string saved = p.ChangesSaved ? "saved" : "has unsaved changes";
            return ToolResult.Ok($"Connected to OpenUtau {hello.OpenUtau.Version}. Project '{p.Name}' ({p.Tracks} tracks, {p.Parts} parts, {p.Bpm:0.##} BPM) {saved}.", data);
        }).ConfigureAwait(false);
    }
}
