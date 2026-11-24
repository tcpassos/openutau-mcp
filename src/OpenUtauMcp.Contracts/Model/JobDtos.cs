namespace OpenUtauMcp.Contracts.Model;

public static class JobKinds {
    /// <summary>One stereo 16-bit WAV of the whole project (fader and Mix FX applied).</summary>
    public const string ExportMixdown = "export.mixdown";
    /// <summary>One mono 16-bit WAV per unmuted voice track (no fader, no Mix FX).</summary>
    public const string ExportStems = "export.stems";
    /// <summary>OpenUtau's "Load rendered pitch" batch edit: the renderer's pitch written to pitd (separate undo step).</summary>
    public const string BatchLoadRenderedPitch = "batch.loadRenderedPitch";
    /// <summary>OpenUtau's "Refresh real curves" batch edit (separate undo step).</summary>
    public const string BatchRefreshRealCurves = "batch.refreshRealCurves";
    /// <summary>The bridge's own exporter: tick range, chosen tracks, wav16/wav24/wav32f, mono or stereo, faders optional, cancellable.</summary>
    public const string ExportCustom = "export.custom";
}

public static class JobStates {
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed record JobStartParams {
    public required string Kind { get; init; }
    /// <summary>Export target: the WAV file (mixdown) or the file name prefix (stems).</summary>
    public string? Path { get; init; }
    /// <summary>Batch jobs: the voice part and optionally its notes (none = whole part).</summary>
    public string? PartId { get; init; }
    public IReadOnlyList<string>? NoteIds { get; init; }
    /// <summary>export.custom: absolute tick range [FromTick, ToTick); null = whole project.</summary>
    public int? FromTick { get; init; }
    public int? ToTick { get; init; }
    /// <summary>export.custom: tracks to render (default: every unmuted track).</summary>
    public IReadOnlyList<string>? TrackIds { get; init; }
    /// <summary>export.custom: wav16 | wav24 | wav32f (default wav24).</summary>
    public string? Format { get; init; }
    /// <summary>export.custom: one file per track (Path is the name prefix) instead of one mix.</summary>
    public bool PerTrack { get; init; }
    /// <summary>export.custom: apply track volume, pan and Mix FX (default true).</summary>
    public bool? ApplyFaders { get; init; }
    /// <summary>export.custom: 1 or 2 (default 2).</summary>
    public int? Channels { get; init; }
}

public sealed record JobIdParams(string JobId);

public sealed record JobWaitParams {
    public required string JobId { get; init; }
    public int TimeoutMs { get; init; } = 30000;
}

public sealed record JobDto {
    public required string JobId { get; init; }
    public required string Kind { get; init; }
    public required string State { get; init; }
    public JobProgress? Progress { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public JobResult? Result { get; init; }
    public JobError? Error { get; init; }
}

public sealed record JobProgress(double? Pct, string? Info);

public sealed record JobResult {
    public IReadOnlyList<ExportedFile> Files { get; init; } = [];
    /// <summary>Batch jobs: whether the part changed (false = the batch had nothing to write).</summary>
    public bool? Changed { get; init; }
    public string? PartId { get; init; }
    public int? PartRev { get; init; }
}

public sealed record ExportedFile {
    public required string Path { get; init; }
    public required long Bytes { get; init; }
    public double Seconds { get; init; }
    public int Channels { get; init; }
    public int SampleRate { get; init; }
    /// <summary>Track name for stems.</summary>
    public string? Track { get; init; }
}

public sealed record JobError(string Code, string Message);

/// <summary>batch.run: one of OpenUtau's synchronous batch edits on a voice part.</summary>
public sealed record BatchRunParams {
    /// <summary>MCP name, e.g. "quantize", "fix_overlap" (see BatchNames).</summary>
    public required string Name { get; init; }
    public required string PartId { get; init; }
    /// <summary>Notes to edit; null or empty = the whole part (as OpenUtau does with no selection).</summary>
    public IReadOnlyList<string>? NoteIds { get; init; }
    /// <summary>quantize: grid in ticks.</summary>
    public int? Grid { get; init; }
    /// <summary>transpose: semitones.</summary>
    public int? Semitones { get; init; }
    /// <summary>add_breath, add_tail, remove_tail: the lyric of the added/removed notes.</summary>
    public string? Lyric { get; init; }
}

public sealed record BatchRunResult {
    public required int Epoch { get; init; }
    public required string PartId { get; init; }
    public required int PartRev { get; init; }
    public required bool Changed { get; init; }
    /// <summary>Note ids that exist after the edit but not before (batch edits may recreate notes).</summary>
    public required IReadOnlyList<string> Created { get; init; }
    /// <summary>Note ids that no longer exist; they now answer NOT_FOUND.</summary>
    public required IReadOnlyList<string> Removed { get; init; }
    public IReadOnlyList<Errors.Warning>? Warnings { get; init; }
}

public static class BatchNames {
    public const string LoadRenderedPitch = "load_rendered_pitch";
    public const string RefreshRealCurves = "refresh_real_curves";

    public static readonly string[] Sync = [
        "quantize", "auto_legato", "fix_overlap", "transpose", "add_breath", "add_tail", "remove_tail",
        "add_phonetic_hints", "remove_phonetic_hints", "bake_pitch", "reset_pitch", "reset_expressions",
        "reset_vibratos", "clear_vibratos", "clear_timings", "reset_aliases", "reset_all",
    ];
    public static readonly string[] Async = [LoadRenderedPitch, RefreshRealCurves];
}
