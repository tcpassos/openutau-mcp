using OpenUtauMcp.Contracts.Errors;

namespace OpenUtauMcp.Contracts.Model;

public static class OnUnsaved {
    /// <summary>Refuse with UNSAVED_CHANGES (default).</summary>
    public const string Fail = "fail";
    /// <summary>Save the current project to its file first.</summary>
    public const string Save = "save";
    /// <summary>Write a side-effect-free backup copy and continue.</summary>
    public const string Backup = "backup";
}

public sealed record ProjectOpenParams {
    /// <summary>File to open. Null = reload the current project file.</summary>
    public string? Path { get; init; }
    public string OnUnsaved { get; init; } = Model.OnUnsaved.Fail;
}

public sealed record ProjectOpenResult {
    public required StatusProject Project { get; init; }
    /// <summary>Backup written before the undo history was cleared.</summary>
    public string? BackupPath { get; init; }
    public IReadOnlyList<Warning>? Warnings { get; init; }
}

public sealed record ProjectSaveParams {
    /// <summary>Null = save to the current file; otherwise "save as" (the project then points to this file).</summary>
    public string? Path { get; init; }
}

public sealed record ProjectSaveResult {
    public required string File { get; init; }
    public required bool ChangesSaved { get; init; }
}

public sealed record ProjectBackupParams {
    public string Reason { get; init; } = "manual";
}

public sealed record BackupResult(string Path);

public static class PlaybackActions {
    public const string Play = "play";
    public const string Pause = "pause";
    public const string Stop = "stop";
    public const string Seek = "seek";
}

public sealed record PlaybackControlParams {
    public required string Action { get; init; }
    /// <summary>Absolute start tick (play/seek). Null = current cursor.</summary>
    public int? Tick { get; init; }
    /// <summary>Absolute end tick (play). Null = to the end.</summary>
    public int? EndTick { get; init; }
    /// <summary>Play only this track.</summary>
    public string? TrackId { get; init; }
    public bool? Loop { get; init; }
}

public sealed record PlaybackStateDto {
    /// <summary>"stopped", "starting" or "playing".</summary>
    public required string State { get; init; }
    public required int Tick { get; init; }
    public bool Loop { get; init; }
}

/// <summary>project.importTracks: adds the tracks of other files to the open project. Existing ids stay valid.</summary>
public sealed record ProjectImportParams {
    public required IReadOnlyList<string> Paths { get; init; }
    /// <summary>Replace the project's tempo and time signatures with the first file's.</summary>
    public bool ImportTempo { get; init; }
    public string OnUnsaved { get; init; } = Model.OnUnsaved.Fail;
}

public sealed record ProjectImportResult {
    public required StatusProject Project { get; init; }
    public required string BackupPath { get; init; }
    public required IReadOnlyList<string> CreatedTracks { get; init; }
    public required IReadOnlyList<string> CreatedParts { get; init; }
}

public static class ExportFormats {
    public const string Midi = "midi";
    public const string Ust = "ust";
    public const string Ds = "ds";
}

/// <summary>project.export: MIDI of the whole project, or UST / DiffSinger script (.ds) of one voice part.</summary>
public sealed record ProjectExportParams {
    public required string Format { get; init; }
    public required string Path { get; init; }
    public string? PartId { get; init; }
}

public sealed record ProjectExportResult(string File, long Bytes);
