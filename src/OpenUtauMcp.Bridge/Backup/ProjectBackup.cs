using System.Text;
using System.Text.RegularExpressions;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using OpenUtauMcp.Contracts.Errors;
using Serilog;

namespace OpenUtauMcp.Bridge.Backup;

/// <summary>
/// Side-effect-free project backups.
/// Ustx.Save and Ustx.AutoSave must never be used for this: they repoint Project.FilePath/Saved and write
/// the user's Preferences, so the user's next Ctrl+S would go to the backup folder. Instead the project
/// is serialized exactly like Ustx.Save does (BeforeSave, Yaml serialize, AfterSave) on the UI thread,
/// and the text is written to %LOCALAPPDATA%\OpenUtauMcp\backups off the UI thread.
/// </summary>
public static partial class ProjectBackup {
    const int KeepPerProject = 50;
    static readonly TimeSpan KeepAtLeast = TimeSpan.FromDays(14);

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenUtauMcp", "backups");

    public sealed record Snapshot(string Yaml, string FileName, string Stem);

    [GeneratedRegex(@"[^A-Za-z0-9_.-]+")]
    private static partial Regex UnsafeChars();

    /// <summary>UI thread only. Throws INTERNAL if serializing changed any observable save state.</summary>
    public static Snapshot Serialize(UProject project, string reason) {
        var doc = DocManager.Inst;
        var before = (project.FilePath, project.Saved, doc.ChangesSaved, doc.Recovered, Preferences.Default.RecoveryPath);
        string yaml;
        project.BeforeSave();
        try {
            yaml = Yaml.DefaultSerializer.Serialize(project);
        } finally {
            project.AfterSave();
        }
        var after = (project.FilePath, project.Saved, doc.ChangesSaved, doc.Recovered, Preferences.Default.RecoveryPath);
        if (before != after) {
            Log.Error($"{BridgeHost.LogPrefix} backup serialization changed save state: {before} -> {after}");
            throw new BridgeException(ErrorCodes.Internal, "Backup serialization changed the project's save state.");
        }
        string source = !string.IsNullOrEmpty(project.FilePath) ? Path.GetFileNameWithoutExtension(project.FilePath) : project.name;
        string stem = UnsafeChars().Replace(string.IsNullOrWhiteSpace(source) ? "project" : source, "_");
        string safeReason = UnsafeChars().Replace(reason, "_");
        return new Snapshot(yaml, $"{stem}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{safeReason}.ustx", stem);
    }

    /// <summary>Writes the snapshot (temp file + move) and prunes old backups of the same project.</summary>
    public static async Task<string> WriteAsync(Snapshot snapshot) {
        System.IO.Directory.CreateDirectory(Directory);
        string final = Path.Combine(Directory, snapshot.FileName);
        string temp = final + ".tmp";
        try {
            await File.WriteAllTextAsync(temp, snapshot.Yaml, new UTF8Encoding(false)).ConfigureAwait(false);
            File.Move(temp, final, overwrite: true);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            throw new BridgeException(ErrorCodes.IoError, $"Could not write the backup: {e.Message}",
                new() { ["path"] = final });
        }
        Prune(snapshot.Stem);
        Log.Information($"{BridgeHost.LogPrefix} backup written: {final}");
        return final;
    }

    static void Prune(string stem) {
        try {
            var files = new DirectoryInfo(Directory).GetFiles(stem + "-*.ustx")
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var old in files.Skip(KeepPerProject).Where(f => DateTime.UtcNow - f.LastWriteTimeUtc > KeepAtLeast)) {
                old.Delete();
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Log.Debug(e, $"{BridgeHost.LogPrefix} backup pruning failed");
        }
    }
}
