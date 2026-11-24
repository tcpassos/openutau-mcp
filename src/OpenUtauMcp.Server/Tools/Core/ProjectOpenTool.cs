using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Server.Bridge;
using OpenUtauMcp.Server.Safety;

namespace OpenUtauMcp.Server.Tools.Core;

[McpServerToolType]
public static class ProjectOpenTool {
    static readonly string[] openable = [".ustx", ".ust", ".vsqx", ".vsq", ".mid", ".midi", ".musicxml", ".xml", ".svp", ".ufdata"];

    [McpServerTool(Name = "ou_project_open", Title = "Open, create, reload a project or import tracks", ReadOnly = false, Idempotent = false, OpenWorld = false, Destructive = true)]
    [Description("Opens a project (replacing the open one; all ids become invalid), creates a new one, reloads it from disk, or adds the tracks of other files (import_tracks; ids stay valid). OpenUtau clears its undo history, so a backup is written first. With unsaved changes it refuses unless onUnsaved is save or backup. Example: {\"action\":\"open\",\"path\":\"C:/Songs/song.ustx\",\"onUnsaved\":\"backup\"}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge, PathPolicy policy,
        [Description("open | new | reload | import_tracks")] string action,
        [Description("Absolute path of the file to open (open only): .ustx, .ust, .vsqx, .mid, .musicxml, .svp, .ufdata.")] string? path = null,
        [Description("import_tracks: absolute paths of the files whose tracks are added.")] string[]? paths = null,
        [Description("import_tracks: also take the tempo and time signatures of the first file.")] bool importTempo = false,
        [Description("With unsaved changes: fail (default), save (save first), backup (keep a backup copy and continue).")] string onUnsaved = "fail",
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            if (onUnsaved is not (OnUnsaved.Fail or OnUnsaved.Save or OnUnsaved.Backup)) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"Unknown onUnsaved '{onUnsaved}'. Use fail, save or backup.");
            }
            if (action == "import_tracks") {
                if (paths is not { Length: > 0 }) {
                    throw new ToolInputException(ToolInputException.InvalidParams, "'paths' is required for import_tracks.");
                }
                var current = await bridge.StatusAsync(ct).ConfigureAwait(false);
                var files = paths.Select(x => policy.Check(x, current.Project.FilePath, openable, write: false)).ToList();
                var imported = await bridge.ProjectImportAsync(new ProjectImportParams { Paths = files, ImportTempo = importTempo, OnUnsaved = onUnsaved }, ct)
                    .ConfigureAwait(false);
                var ip = imported.Project;
                return ToolResult.Ok($"Imported {imported.CreatedTracks.Count} tracks and {imported.CreatedParts.Count} parts into '{ip.Name}'. " +
                                     $"Existing ids stay valid; OpenUtau cleared its undo history (backup: {imported.BackupPath}).",
                    new JsonObject {
                        ["createdTracks"] = new JsonArray(imported.CreatedTracks.Select(t => (JsonNode?)t).ToArray()),
                        ["createdParts"] = new JsonArray(imported.CreatedParts.Select(t => (JsonNode?)t).ToArray()),
                        ["backup"] = imported.BackupPath,
                    }, null, new JsonObject { ["epoch"] = ip.Epoch });
            }
            string? target = null;
            if (action == "open") {
                if (path is null) {
                    throw new ToolInputException(ToolInputException.InvalidParams, "'path' is required for open.");
                }
                var status = await bridge.StatusAsync(ct).ConfigureAwait(false);
                target = policy.Check(path, status.Project.FilePath, openable, write: false);
            } else if (action is not ("reload" or "new")) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"Unknown action '{action}'. Use open, new, reload or import_tracks.");
            }
            var result = action == "new"
                ? await bridge.ProjectNewAsync(onUnsaved, ct).ConfigureAwait(false)
                : await bridge.ProjectOpenAsync(new ProjectOpenParams { Path = target, OnUnsaved = onUnsaved }, ct).ConfigureAwait(false);
            var p = result.Project;
            return ToolResult.Ok($"Opened '{p.Name}' ({p.Tracks} tracks, {p.Parts} parts). Previous ids are no longer valid; backup: {result.BackupPath}",
                new JsonObject {
                    ["project"] = new JsonObject {
                        ["name"] = p.Name, ["filePath"] = p.FilePath, ["tracks"] = p.Tracks, ["parts"] = p.Parts, ["bpm"] = p.Bpm,
                    },
                    ["backup"] = result.BackupPath,
                }, result.Warnings, new JsonObject { ["epoch"] = p.Epoch });
        });
}
