using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Server.Bridge;
using OpenUtauMcp.Server.Safety;

namespace OpenUtauMcp.Server.Tools.Core;

[McpServerToolType]
public static class ProjectSaveTool {
    static readonly string[] ustx = [".ustx"];

    [McpServerTool(Name = "ou_project_save", Title = "Save, back up or export the project", ReadOnly = false, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("save: writes the open project to its file (like Ctrl+S). save_as: writes to a new .ustx and the project then points to it. " +
                 "backup: writes a copy to the backups folder without changing which file the project is saved to. " +
                 "export: writes MIDI of the whole project (format midi), or a UST or DiffSinger script of one part (format ust / ds, with part). " +
                 "Example: {\"action\":\"save_as\",\"path\":\"C:\\\\Songs\\\\song-v2.ustx\"}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge, PathPolicy paths,
        [Description("save (default) | save_as | backup | export")] string action = "save",
        [Description("Absolute path: .ustx for save_as (or save when the project was never saved); .mid, .ust or .ds for export.")] string? path = null,
        [Description("export: midi | ust | ds.")] string? format = null,
        [Description("export ust/ds: part id or exact name.")] string? part = null,
        [Description("Allow replacing an existing file with save_as or export.")] bool overwrite = false,
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            if (action == "backup") {
                var backup = await bridge.ProjectBackupAsync("manual", ct).ConfigureAwait(false);
                return ToolResult.Ok($"Backup written to {backup.Path}. The project still saves to its own file.",
                    new JsonObject { ["file"] = backup.Path });
            }
            if (action == "export") {
                string ext = format switch {
                    ExportFormats.Midi => ".mid",
                    ExportFormats.Ust => ".ust",
                    ExportFormats.Ds => ".ds",
                    _ => throw new ToolInputException(ToolInputException.InvalidParams, "export needs format: midi, ust or ds."),
                };
                if (path is null) {
                    throw new ToolInputException(ToolInputException.InvalidParams, "'path' is required for export.");
                }
                if (format != ExportFormats.Midi && part is null) {
                    throw new ToolInputException(ToolInputException.InvalidParams, $"A {format} export is per part: pass 'part'.");
                }
                var ctx = await ProjectContext.LoadAsync(bridge, ct).ConfigureAwait(false);
                string? partId = part is null ? null : ctx.ResolvePart(part).Id;
                string file = paths.Check(path, ctx.Project.FilePath, [ext], write: true, overwrite);
                var exported = await bridge.ProjectExportAsync(new ProjectExportParams { Format = format!, Path = file, PartId = partId }, ct)
                    .ConfigureAwait(false);
                return ToolResult.Ok($"Exported {format} to {exported.File}.", new JsonObject { ["file"] = exported.File, ["bytes"] = exported.Bytes });
            }
            if (action is not ("save" or "save_as")) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"Unknown action '{action}'. Use save, save_as, backup or export.");
            }
            var status = await bridge.StatusAsync(ct).ConfigureAwait(false);
            string? target = null;
            if (action == "save_as" || status.Project.FilePath is null) {
                if (path is null) {
                    throw new ToolInputException(ToolInputException.InvalidParams,
                        action == "save_as" ? "'path' is required for save_as." : "The project was never saved; pass 'path'.");
                }
                target = paths.Check(path, status.Project.FilePath, ustx, write: true, overwrite);
            }
            var result = await bridge.ProjectSaveAsync(new ProjectSaveParams { Path = target }, ct).ConfigureAwait(false);
            return ToolResult.Ok($"Saved to {result.File}.", new JsonObject { ["file"] = result.File, ["changesSaved"] = result.ChangesSaved });
        });
}
