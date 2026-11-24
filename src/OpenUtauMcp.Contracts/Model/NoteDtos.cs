using OpenUtauMcp.Contracts.Errors;

namespace OpenUtauMcp.Contracts.Model;

public sealed record NotesQueryParams {
    public required string PartId { get; init; }
    /// <summary>Absolute tick range [FromTick, ToTick) - notes starting inside it.</summary>
    public TickRange? Range { get; init; }
    public IReadOnlyList<string>? Ids { get; init; }
    /// <summary>Optional extra fields: "vibrato", "pitch", "exprs", "phonemes", "errors", "ms".</summary>
    public IReadOnlyList<string>? Fields { get; init; }
    /// <summary>With "phonemes": wait up to this long for OpenUtau to phonemize the part (else DERIVED_STALE).</summary>
    public int? AwaitMs { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; } = 500;
}

public sealed record TickRange(int FromTick, int ToTick);

public sealed record NotesQueryResult {
    public required string PartId { get; init; }
    /// <summary>Absolute start tick of the part (note positions are relative to it).</summary>
    public required int PartPos { get; init; }
    public required int PartRev { get; init; }
    public required int Epoch { get; init; }
    public required int Total { get; init; }
    public required IReadOnlyList<NoteDto> Notes { get; init; }
    public int? NextOffset { get; init; }
    public IReadOnlyList<Warning>? Warnings { get; init; }
}

/// <summary>A note in raw units. Position is relative to its part.</summary>
public sealed record NoteDto {
    public required string Id { get; init; }
    public required int Pos { get; init; }
    public required int Dur { get; init; }
    public required int Tone { get; init; }
    public required string Lyric { get; init; }
    public int Tuning { get; init; }
    public VibratoDto? Vibrato { get; init; }
    public PitchDto? Pitch { get; init; }
    public IReadOnlyList<NoteExpressionDto>? Exprs { get; init; }
    public bool? Error { get; init; }
    public bool? OverlapError { get; init; }
    public double? PosMs { get; init; }
    public double? EndMs { get; init; }
    public IReadOnlyList<PhonemeDto>? Phonemes { get; init; }
    /// <summary>Per-note phonemizer override (factory display name), with "phonemes".</summary>
    public string? Phonemizer { get; init; }
}

public sealed record VibratoDto {
    public float Length { get; init; }
    public float Period { get; init; }
    public float Depth { get; init; }
    public float In { get; init; }
    public float Out { get; init; }
    public float Shift { get; init; }
    public float Drift { get; init; }
    public float VolLink { get; init; }
}

/// <summary>Pitch points: x in ms relative to note start, y in tenths of a semitone (OpenUtau units).</summary>
public sealed record PitchDto {
    public bool SnapFirst { get; init; } = true;
    public required IReadOnlyList<PitchPointDto> Points { get; init; }
}

public sealed record PitchPointDto(float X, float Y, string Shape);

public sealed record NoteExpressionDto(string Abbr, int? Index, float Value);

/// <summary>
/// A phoneme as OpenUtau resolved it. Pos/Dur in ticks relative to the note start (Pos may be negative: consonants
/// start before the note). Overrides show what the user or MCP changed.
/// </summary>
public sealed record PhonemeDto {
    public required int Index { get; init; }
    public required string Phoneme { get; init; }
    public required int Pos { get; init; }
    public required int Dur { get; init; }
    public double PosMs { get; init; }
    public double DurMs { get; init; }
    public string? Alias { get; init; }
    public int? Offset { get; init; }
    public float? PreutterDeltaMs { get; init; }
    public float? OverlapDeltaMs { get; init; }
    public bool Error { get; init; }
}

/// <summary>phrases.query: render phrases of a part with sampled series.</summary>
public sealed record PhrasesQueryParams {
    public required string PartId { get; init; }
    /// <summary>Absolute tick range [FromTick, ToTick): phrases overlapping it.</summary>
    public TickRange? Range { get; init; }
    /// <summary>"pitch" (final, cents), "pitchBase" (before pitd), "dynamics", "tension", "breathiness", "voicing", "gender".</summary>
    public IReadOnlyList<string>? Series { get; init; }
    /// <summary>Sampling step in ticks, a multiple of 5 (default 5).</summary>
    public int StepTicks { get; init; } = 5;
    public int? AwaitMs { get; init; }
}

public sealed record PhrasesQueryResult {
    public required string PartId { get; init; }
    public required int PartRev { get; init; }
    public required int Epoch { get; init; }
    public required int StepTicks { get; init; }
    public required IReadOnlyList<PhraseDto> Phrases { get; init; }
    public IReadOnlyList<Warning>? Warnings { get; init; }
}

/// <summary>Series[i] is the value at absolute tick SeriesStartTick + i * StepTicks.</summary>
public sealed record PhraseDto {
    public required int StartTick { get; init; }
    public required int EndTick { get; init; }
    public required int SeriesStartTick { get; init; }
    public required IReadOnlyList<string> NoteIds { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<float>> Series { get; init; }
}
