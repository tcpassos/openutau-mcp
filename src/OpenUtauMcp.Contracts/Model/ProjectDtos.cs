using OpenUtauMcp.Contracts.Errors;

namespace OpenUtauMcp.Contracts.Model;

/// <summary>Raw OpenUtau units: ticks (480 per quarter), MIDI tones, cents.</summary>
public sealed record ProjectGetParams {
    /// <summary>Any of "tempo", "tracks", "parts", "expressions", "comment". Null = tempo+tracks+parts.</summary>
    public IReadOnlyList<string>? Include { get; init; }
}

public sealed record ProjectDto {
    public required int Epoch { get; init; }
    public required int StructureRev { get; init; }
    public required string Name { get; init; }
    public string? FilePath { get; init; }
    public required int Resolution { get; init; }
    public int Key { get; init; }
    public IReadOnlyList<TempoDto>? Tempos { get; init; }
    public IReadOnlyList<TimeSignatureDto>? TimeSignatures { get; init; }
    public IReadOnlyList<TrackDto>? Tracks { get; init; }
    public IReadOnlyList<PartDto>? Parts { get; init; }
    public IReadOnlyList<ExpressionDescriptorDto>? Expressions { get; init; }
    public string? Comment { get; init; }
}

public sealed record TempoDto(int Tick, double Bpm);

/// <summary>Time signature starting at bar <see cref="Bar"/> (0-based, as in OpenUtau).</summary>
public sealed record TimeSignatureDto(int Bar, int BeatPerBar, int BeatUnit);

public sealed record TrackDto {
    public required string Id { get; init; }
    public required int Index { get; init; }
    public required string Name { get; init; }
    public string? Singer { get; init; }
    public bool SingerFound { get; init; }
    public string? Phonemizer { get; init; }
    public string? Renderer { get; init; }
    public double VolumeDb { get; init; }
    public double Pan { get; init; }
    public bool Mute { get; init; }
    public bool Solo { get; init; }
    public IReadOnlyList<string>? VoiceColors { get; init; }
}

public sealed record PartDto {
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string TrackId { get; init; }
    /// <summary>"voice" or "wave".</summary>
    public required string Type { get; init; }
    /// <summary>Absolute start tick.</summary>
    public required int Pos { get; init; }
    public required int Dur { get; init; }
    public int NoteCount { get; init; }
    public int PartRev { get; init; }
    public string? Comment { get; init; }
}

public sealed record ExpressionDescriptorDto {
    public required string Abbr { get; init; }
    public required string Name { get; init; }
    /// <summary>"numerical", "options", "curve" or "maskedCurve".</summary>
    public required string Type { get; init; }
    public float Min { get; init; }
    public float Max { get; init; }
    public float DefaultValue { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
}

public sealed record StatusResult {
    public required StatusProject Project { get; init; }
    public required StatusPlayback Playback { get; init; }
    public required HistoryState History { get; init; }
    public IReadOnlyList<Warning>? Warnings { get; init; }
    /// <summary>Last OpenUtau error messages (most recent last).</summary>
    public IReadOnlyList<string>? RecentErrors { get; init; }
    /// <summary>Running or recently finished jobs.</summary>
    public IReadOnlyList<JobDto>? Jobs { get; init; }
}

public sealed record StatusProject {
    public required string Name { get; init; }
    public string? FilePath { get; init; }
    public required bool ChangesSaved { get; init; }
    public required int Epoch { get; init; }
    public required int StructureRev { get; init; }
    public required int Tracks { get; init; }
    public required int Parts { get; init; }
    public double Bpm { get; init; }
}

public sealed record StatusPlayback(string State, int Tick);

public sealed record HistoryState {
    public required bool CanUndo { get; init; }
    public string? UndoName { get; init; }
    public required bool CanRedo { get; init; }
    public string? RedoName { get; init; }
    public required bool ChangesSaved { get; init; }
}

public sealed record HistoryStepParams {
    public int Count { get; init; } = 1;
}

public sealed record HistoryStepResult {
    public required IReadOnlyList<string> Steps { get; init; }
    public required HistoryState History { get; init; }
}
