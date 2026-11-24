using OpenUtau.Core;
using OpenUtau.Core.Format;
using OpenUtau.Core.Ustx;
using OpenUtauMcp.Bridge.Backup;
using OpenUtauMcp.Bridge.Dispatch;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>
/// project.open / project.save / project.backup.
/// Opening clears OpenUtau's undo history, so a backup is always written first; unsaved changes are
/// refused unless the caller chose to save or back them up. Never discards work without a copy.
/// </summary>
public static class FileAdapter {
    static readonly HashSet<string> openable = new(StringComparer.OrdinalIgnoreCase) {
        ".ustx", ".ust", ".vsqx", ".vsq", ".mid", ".midi", ".musicxml", ".xml", ".svp", ".ufdata",
    };

    public static async Task<ProjectOpenResult> OpenAsync(ProjectOpenParams p, CancellationToken ct) {
        BridgeHost.Jobs.EnsureIdle();
        var prep = await UiDispatcher.RunAsync(project => {
            UndoScope.EnsureNoUserEdit();
            string? requested = p.Path ?? (string.IsNullOrEmpty(project.FilePath) ? null : project.FilePath);
            if (requested is null) {
                throw BridgeException.InvalidParams("The project has no file to reload; pass 'path'.", "path");
            }
            string path = Path.GetFullPath(requested);
            if (!openable.Contains(Path.GetExtension(path))) {
                throw new BridgeException(ErrorCodes.UnsupportedFormat, $"OpenUtau cannot open '{Path.GetExtension(path)}' files.",
                    new() { ["path"] = path });
            }
            if (!File.Exists(path)) {
                throw new BridgeException(ErrorCodes.IoError, $"File not found: {path}", new() { ["path"] = path });
            }
            HandleUnsaved(project, p.OnUnsaved);
            return (path, snapshot: ProjectBackup.Serialize(project, "before-open"));
        }, ct).ConfigureAwait(false);

        string backupPath = await ProjectBackup.WriteAsync(prep.snapshot).ConfigureAwait(false);

        var opened = await UiDispatcher.RunAsync(project => {
            UndoScope.EnsureNoUserEdit();
            long errorSeq = BridgeHost.Events.ErrorSeq;
            try {
                // Same as the UI's open, minus VoiceColorRemappingNotification (it may open a dialog).
                Formats.LoadProject([prep.path]);
            } catch (Exception e) when (e.GetType().Name == "FileFormatException") {
                // System.IO.FileFormatException lives in System.IO.Packaging; matched by name to keep one DLL.
                throw new BridgeException(ErrorCodes.UnsupportedFormat, $"OpenUtau could not read the file: {e.Message}",
                    new() { ["path"] = prep.path });
            } catch (Exception e) when (e is not BridgeException) {
                throw new BridgeException(ErrorCodes.IoError, $"OpenUtau could not open the file: {e.Message}",
                    new() { ["path"] = prep.path });
            }
            var doc = DocManager.Inst;
            if (ReferenceEquals(doc.Project, project)) {
                var errors = BridgeHost.Events.ErrorsSince(errorSeq);
                throw new BridgeException(ErrorCodes.IoError,
                    $"OpenUtau did not open the file{(errors.Count > 0 ? ": " + string.Join(" | ", errors) : ".")}",
                    new() { ["path"] = prep.path });
            }
            doc.Recovered = false;
            UiPages.ShowProject();
            return ProjectAdapter.Status(BridgeHost.Ids.Sync(doc.Project)).Project;
        }, ct).ConfigureAwait(false);

        return new ProjectOpenResult { Project = opened, BackupPath = backupPath };
    }

    /// <summary>New project, like File > New: the user's default template if it exists, else an empty project.</summary>
    public static async Task<ProjectOpenResult> NewAsync(ProjectNewParams p, CancellationToken ct) {
        BridgeHost.Jobs.EnsureIdle();
        var snapshot = await UiDispatcher.RunAsync(project => {
            UndoScope.EnsureNoUserEdit();
            HandleUnsaved(project, p.OnUnsaved);
            return ProjectBackup.Serialize(project, "before-new");
        }, ct).ConfigureAwait(false);
        string backupPath = await ProjectBackup.WriteAsync(snapshot).ConfigureAwait(false);

        var created = await UiDispatcher.RunAsync(_ => {
            UndoScope.EnsureNoUserEdit();
            var doc = DocManager.Inst;
            string template = Path.Combine(PathManager.Inst.TemplatesPath, "default.ustx");
            bool fromTemplate = false;
            if (File.Exists(template)) {
                try {
                    Formats.LoadProject([template]);
                    doc.Project.Saved = false;
                    doc.Project.FilePath = string.Empty;
                    fromTemplate = true;
                } catch (Exception e) {
                    Serilog.Log.Warning(e, $"{BridgeHost.LogPrefix} default template failed; creating an empty project");
                }
            }
            if (!fromTemplate) {
                doc.ExecuteCmd(new LoadProjectNotification(Ustx.Create()));
            }
            doc.Recovered = false;
            UiPages.ShowProject();
            return ProjectAdapter.Status(BridgeHost.Ids.Sync(doc.Project)).Project;
        }, ct).ConfigureAwait(false);

        return new ProjectOpenResult { Project = created, BackupPath = backupPath };
    }

    /// <summary>
    /// Adds the tracks and parts of other files, like File > Import tracks. OpenUtau clears the undo history (it
    /// reloads the same project object), so a backup is written first; existing ids stay valid.
    /// </summary>
    public static async Task<ProjectImportResult> ImportAsync(ProjectImportParams p, CancellationToken ct) {
        BridgeHost.Jobs.EnsureIdle();
        if (p.Paths.Count == 0) {
            throw BridgeException.InvalidParams("'paths' is empty.", "paths");
        }
        var prep = await UiDispatcher.RunAsync(project => {
            UndoScope.EnsureNoUserEdit();
            var paths = p.Paths.Select(Path.GetFullPath).ToArray();
            foreach (var path in paths) {
                if (!openable.Contains(Path.GetExtension(path))) {
                    throw new BridgeException(ErrorCodes.UnsupportedFormat, $"OpenUtau cannot import '{Path.GetExtension(path)}' files.",
                        new() { ["path"] = path });
                }
                if (!File.Exists(path)) {
                    throw new BridgeException(ErrorCodes.IoError, $"File not found: {path}", new() { ["path"] = path });
                }
            }
            HandleUnsaved(project, p.OnUnsaved);
            return (paths, snapshot: ProjectBackup.Serialize(project, "before-import"));
        }, ct).ConfigureAwait(false);

        string backupPath = await ProjectBackup.WriteAsync(prep.snapshot).ConfigureAwait(false);

        return await UiDispatcher.RunAsync(project => {
            UndoScope.EnsureNoUserEdit();
            var tracksBefore = project.tracks.ToHashSet();
            var partsBefore = project.parts.ToHashSet();
            long errorSeq = BridgeHost.Events.ErrorSeq;
            try {
                UndoScope.RunOwnGroup(() => Formats.ImportTracks(project, prep.paths, p.ImportTempo));
            } catch (Exception e) when (e is not BridgeException) {
                throw new BridgeException(ErrorCodes.IoError, $"OpenUtau could not import the files: {e.Message}",
                    new() { ["backup"] = backupPath });
            }
            // OpenUtau clears the undo history but keeps Project.Saved, so the imported tracks would look saved
            // and closing OpenUtau would not warn. Mark the project as changed.
            project.Saved = false;
            var errors = BridgeHost.Events.ErrorsSince(errorSeq);
            var ids = BridgeHost.Ids;
            var newTracks = project.tracks.Where(t => !tracksBefore.Contains(t)).Select(ids.IdOf).ToList();
            var newParts = project.parts.Where(x => !partsBefore.Contains(x)).Select(ids.IdOf).ToList();
            if (newTracks.Count == 0 && errors.Count > 0) {
                throw new BridgeException(ErrorCodes.IoError, $"OpenUtau did not import the files: {string.Join(" | ", errors)}",
                    new() { ["backup"] = backupPath });
            }
            return new ProjectImportResult {
                Project = ProjectAdapter.Status(ids.Sync(DocManager.Inst.Project)).Project,
                BackupPath = backupPath,
                CreatedTracks = newTracks,
                CreatedParts = newParts,
            };
        }, ct).ConfigureAwait(false);
    }

    /// <summary>MIDI of the project, or UST / DiffSinger script of one voice part (OpenUtau's own writers).</summary>
    public static Task<ProjectExportResult> ExportAsync(ProjectExportParams p, CancellationToken ct) =>
        UiDispatcher.RunAsync(project => {
            string path = Path.GetFullPath(p.Path);
            string expected = p.Format switch {
                ExportFormats.Midi => ".mid",
                ExportFormats.Ust => ".ust",
                ExportFormats.Ds => ".ds",
                _ => throw BridgeException.InvalidParams($"Unknown format '{p.Format}'. Use midi, ust or ds.", "format"),
            };
            if (!string.Equals(Path.GetExtension(path), expected, StringComparison.OrdinalIgnoreCase)
                && !(p.Format == ExportFormats.Midi && string.Equals(Path.GetExtension(path), ".midi", StringComparison.OrdinalIgnoreCase))) {
                throw new BridgeException(ErrorCodes.UnsupportedFormat, $"A {p.Format} export must end in {expected}.", new() { ["path"] = path });
            }
            UVoicePart? part = null;
            if (p.Format != ExportFormats.Midi) {
                part = BridgeHost.Ids.ResolveVoicePart(project,
                    p.PartId ?? throw BridgeException.InvalidParams($"A {p.Format} export needs 'partId'.", "partId"));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try {
                switch (p.Format) {
                    case ExportFormats.Midi: OpenUtau.Core.Format.MidiWriter.Save(path, project); break;
                    case ExportFormats.Ust: OpenUtau.Classic.Ust.SavePart(project, part!, path); break;
                    default:
                        OpenUtau.Core.DiffSinger.DiffSingerScript.SavePart(project, part!, path, new OpenUtau.Core.DiffSinger.DsScriptExportOptions());
                        break;
                }
            } catch (Exception e) {
                throw new BridgeException(ErrorCodes.IoError, $"The {p.Format} export failed: {e.Message}", new() { ["path"] = path });
            }
            var info = new FileInfo(path);
            if (!info.Exists) {
                throw new BridgeException(ErrorCodes.IoError, $"OpenUtau did not write {path}.", new() { ["path"] = path });
            }
            return new ProjectExportResult(info.FullName, info.Length);
        }, ct);

    static void HandleUnsaved(UProject project, string onUnsaved) {
        if (DocManager.Inst.ChangesSaved) {
            return;
        }
        switch (onUnsaved) {
            case OnUnsaved.Save:
                SaveOnUi(project, null);
                break;
            case OnUnsaved.Backup:
                break;  // the backup taken before loading keeps the unsaved work
            default:
                throw new BridgeException(ErrorCodes.UnsavedChanges,
                    "The open project has unsaved changes; this would discard them.",
                    new() { ["onUnsaved"] = onUnsaved });
        }
    }

    public static Task<ProjectSaveResult> SaveAsync(ProjectSaveParams p, CancellationToken ct) {
        BridgeHost.Jobs.EnsureIdle();
        return UiDispatcher.RunAsync(project => {
            UndoScope.EnsureNoUserEdit();
            string file = SaveOnUi(project, p.Path is null ? null : Path.GetFullPath(p.Path));
            return new ProjectSaveResult { File = file, ChangesSaved = DocManager.Inst.ChangesSaved };
        }, ct);
    }

    public static async Task<BackupResult> BackupAsync(ProjectBackupParams p, CancellationToken ct) {
        var snapshot = await UiDispatcher.RunAsync(project => ProjectBackup.Serialize(project, p.Reason), ct).ConfigureAwait(false);
        return new BackupResult(await ProjectBackup.WriteAsync(snapshot).ConfigureAwait(false));
    }

    /// <summary>
    /// Saves like Ctrl+S / Save As. Success is checked independently: OpenUtau marks the project saved
    /// before writing and swallows write errors (it only posts an ErrorMessageNotification).
    /// </summary>
    static string SaveOnUi(UProject project, string? saveAs) {
        string? target = saveAs ?? (string.IsNullOrEmpty(project.FilePath) ? null : project.FilePath);
        if (target is null) {
            throw BridgeException.InvalidParams("The project has never been saved; pass 'path'.", "path");
        }
        if (!string.Equals(Path.GetExtension(target), ".ustx", StringComparison.OrdinalIgnoreCase)) {
            throw new BridgeException(ErrorCodes.UnsupportedFormat, "Projects are saved as .ustx.", new() { ["path"] = target });
        }
        var started = DateTime.UtcNow.AddSeconds(-2);
        long errorSeq = BridgeHost.Events.ErrorSeq;
        string previousPath = project.FilePath;
        bool previousSaved = project.Saved;
        DocManager.Inst.ExecuteCmd(new SaveProjectNotification(saveAs ?? ""));
        var errors = BridgeHost.Events.ErrorsSince(errorSeq);
        var info = new FileInfo(target);
        if (errors.Count > 0 || !info.Exists || info.LastWriteTimeUtc < started) {
            // Ustx.Save repoints FilePath before writing and skips AfterSave when the write fails:
            // undo that, so a failed "save as" does not leave the project pointing at an unwritten file.
            project.FilePath = previousPath;
            project.Saved = previousSaved;
            project.AfterSave();
            throw new BridgeException(ErrorCodes.IoError,
                $"The project was not written to {target}{(errors.Count > 0 ? ": " + string.Join(" | ", errors) : ".")} " +
                "OpenUtau may now show the project as saved; it is not.",
                new() { ["path"] = target });
        }
        return info.FullName;
    }
}
