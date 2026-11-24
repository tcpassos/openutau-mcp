using System.Text.Json.Serialization;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;

namespace OpenUtauMcp.Contracts.Ops;

/// <summary>
/// One operation of an <c>edit.apply</c> plan, discriminated by "op".
/// All values are absolute and in raw OpenUtau units (ticks relative to the part, MIDI tones, pitch point
/// x in ms and y in tenths of a semitone).
/// References: an id ("n12", "p3", "t2") or a symbolic ref ("@a") created by an earlier op of the same plan.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(NoteUpdateOp), OpNames.NoteUpdate)]
[JsonDerivedType(typeof(CurveReplaceOp), OpNames.CurveReplace)]
[JsonDerivedType(typeof(NoteAddOp), OpNames.NoteAdd)]
[JsonDerivedType(typeof(NoteRemoveOp), OpNames.NoteRemove)]
[JsonDerivedType(typeof(NoteVibratoOp), OpNames.NoteVibrato)]
[JsonDerivedType(typeof(NotePitchOp), OpNames.NotePitch)]
[JsonDerivedType(typeof(NoteExprOp), OpNames.NoteExpr)]
[JsonDerivedType(typeof(CurveSetRangeOp), OpNames.CurveSetRange)]
[JsonDerivedType(typeof(CurveClearOp), OpNames.CurveClear)]
[JsonDerivedType(typeof(ProjectExpressionsOp), OpNames.ProjectExpressions)]
[JsonDerivedType(typeof(TrackAddOp), OpNames.TrackAdd)]
[JsonDerivedType(typeof(TrackMixOp), OpNames.TrackMix)]
[JsonDerivedType(typeof(PartAddOp), OpNames.PartAdd)]
[JsonDerivedType(typeof(PartDuplicateOp), OpNames.PartDuplicate)]
[JsonDerivedType(typeof(PartCommentOp), OpNames.PartComment)]
[JsonDerivedType(typeof(TrackUpdateOp), OpNames.TrackUpdate)]
[JsonDerivedType(typeof(TrackRemoveOp), OpNames.TrackRemove)]
[JsonDerivedType(typeof(PartUpdateOp), OpNames.PartUpdate)]
[JsonDerivedType(typeof(PartRemoveOp), OpNames.PartRemove)]
[JsonDerivedType(typeof(ProjectUpdateOp), OpNames.ProjectUpdate)]
[JsonDerivedType(typeof(NoteResetExprsOp), OpNames.NoteResetExprs)]
[JsonDerivedType(typeof(NotePhonemizerOp), OpNames.NotePhonemizer)]
[JsonDerivedType(typeof(NotePhonemesOp), OpNames.NotePhonemes)]
[JsonDerivedType(typeof(PartAddWaveOp), OpNames.PartAddWave)]
public abstract record EditOp;

public static class OpNames {
    public const string NoteUpdate = "note.update";
    public const string CurveReplace = "curve.replace";
    public const string NoteAdd = "note.add";
    public const string NoteRemove = "note.remove";
    public const string NoteVibrato = "note.vibrato";
    public const string NotePitch = "note.pitch";
    public const string NoteExpr = "note.expr";
    public const string CurveSetRange = "curve.setRange";
    public const string CurveClear = "curve.clear";
    public const string ProjectExpressions = "project.expressions";
    public const string TrackAdd = "track.add";
    public const string TrackMix = "track.mix";
    public const string PartAdd = "part.add";
    public const string PartDuplicate = "part.duplicate";
    public const string PartComment = "part.comment";
    public const string TrackUpdate = "track.update";
    public const string TrackRemove = "track.remove";
    public const string PartUpdate = "part.update";
    public const string PartRemove = "part.remove";
    public const string ProjectUpdate = "project.update";
    public const string NoteResetExprs = "note.resetExprs";
    public const string NotePhonemizer = "note.phonemizer";
    public const string NotePhonemes = "note.phonemes";
    public const string PartAddWave = "part.addWave";
}

/// <summary>
/// Changes a track. SingerId applies the singer like OpenUtau's track header (singer, then its default
/// phonemizer and renderer); Phonemizer/Renderer given here win over those defaults. Position is 0-based.
/// </summary>
public sealed record TrackUpdateOp : EditOp {
    public required string TrackId { get; init; }
    public string? Name { get; init; }
    public string? Color { get; init; }
    public string? SingerId { get; init; }
    /// <summary>Phonemizer id (type full name, e.g. OpenUtau.Core.DiffSinger.DiffSingerEnglishPhonemizer) or tag.</summary>
    public string? Phonemizer { get; init; }
    /// <summary>Renderer name, e.g. DIFFSINGER, CLASSIC, WORLDLINE-R.</summary>
    public string? Renderer { get; init; }
    public int? Position { get; init; }
}

/// <summary>Removes a track and its parts.</summary>
public sealed record TrackRemoveOp : EditOp {
    public required string TrackId { get; init; }
}

/// <summary>Renames, moves (absolute position, other track) or resizes a part.</summary>
public sealed record PartUpdateOp : EditOp {
    public required string PartId { get; init; }
    public string? Name { get; init; }
    /// <summary>Absolute start tick.</summary>
    public int? Pos { get; init; }
    public string? TrackId { get; init; }
    public int? Dur { get; init; }
    /// <summary>Resize by moving the start instead of the end.</summary>
    public bool ResizeFromStart { get; init; }
}

public sealed record PartRemoveOp : EditOp {
    public required string PartId { get; init; }
}

/// <summary>Project-level changes: tempo map, time signatures, key and comment (comment is not undoable).</summary>
public sealed record ProjectUpdateOp : EditOp {
    /// <summary>Tempo at tick 0.</summary>
    public double? Bpm { get; init; }
    public IReadOnlyList<TempoDto>? TempoAdd { get; init; }
    /// <summary>Ticks of tempo changes to remove (not tick 0).</summary>
    public IReadOnlyList<int>? TempoRemove { get; init; }
    /// <summary>Time signature at bar 0.</summary>
    public TimeSignatureDto? TimeSig { get; init; }
    public IReadOnlyList<TimeSignatureDto>? TimeSigAdd { get; init; }
    /// <summary>0-based bars of time signature changes to remove (not bar 0).</summary>
    public IReadOnlyList<int>? TimeSigRemove { get; init; }
    /// <summary>Tonic pitch class 0..11 (0 = C).</summary>
    public int? Key { get; init; }
    public string? Comment { get; init; }
}

/// <summary>Replaces a part's comment (saved in the .ustx). Not undoable in OpenUtau (warning NOT_UNDOABLE).</summary>
public sealed record PartCommentOp : EditOp {
    public required string PartId { get; init; }
    public required string Comment { get; init; }
}

/// <summary>Changes a note. Position is relative to the note's part. Null fields are left unchanged.</summary>
public sealed record NoteUpdateOp : EditOp {
    public required string NoteId { get; init; }
    public int? Pos { get; init; }
    public int? Dur { get; init; }
    public int? Tone { get; init; }
    public string? Lyric { get; init; }
    /// <summary>Fine tuning in cents.</summary>
    public int? Tuning { get; init; }
}

/// <summary>Replaces a whole expression curve of a part. Xs are part-relative ticks on a 5-tick grid.</summary>
public sealed record CurveReplaceOp : EditOp {
    public required string PartId { get; init; }
    public required string Abbr { get; init; }
    public required IReadOnlyList<int> Xs { get; init; }
    public required IReadOnlyList<int> Ys { get; init; }
}

/// <summary>
/// Creates a note (OpenUtau's CreateNote, so it gets the user's default portamento unless Pitch is given).
/// Per-phoneme expressions are not allowed here: phonemes do not exist until OpenUtau phonemizes the note.
/// </summary>
public sealed record NoteAddOp : EditOp {
    public required string PartId { get; init; }
    public required int Pos { get; init; }
    public required int Dur { get; init; }
    public required int Tone { get; init; }
    public string Lyric { get; init; } = "a";
    public int? Tuning { get; init; }
    /// <summary>Symbolic name ("@a") later ops can use instead of the id; returned in "created".</summary>
    public string? Ref { get; init; }
    public VibratoPatch? Vibrato { get; init; }
    public PitchSpec? Pitch { get; init; }
    /// <summary>Same value for every phoneme of the note, per expression abbreviation.</summary>
    public IReadOnlyDictionary<string, float>? Exprs { get; init; }
    /// <summary>Extend the part when the note ends after it (warning PART_GROWN). Default true.</summary>
    public bool GrowPart { get; init; } = true;
}

public sealed record NoteRemoveOp : EditOp {
    public required IReadOnlyList<string> NoteIds { get; init; }
}

/// <summary>Partial vibrato change; null fields keep their value. Reset = OpenUtau's default vibrato.</summary>
public sealed record NoteVibratoOp : EditOp {
    public required IReadOnlyList<string> NoteIds { get; init; }
    public VibratoPatch? Vibrato { get; init; }
    public bool Reset { get; init; }
}

public sealed record VibratoPatch {
    /// <summary>Percent of the note (0 = off).</summary>
    public float? Length { get; init; }
    /// <summary>Milliseconds per cycle.</summary>
    public float? Period { get; init; }
    /// <summary>Cents.</summary>
    public float? Depth { get; init; }
    /// <summary>Fade-in, percent of the vibrato.</summary>
    public float? In { get; init; }
    /// <summary>Fade-out, percent of the vibrato.</summary>
    public float? Out { get; init; }
    public float? Shift { get; init; }
    public float? Drift { get; init; }
    public float? VolLink { get; init; }
}

/// <summary>Replaces the pitch points of notes. Points null = reset to the default portamento.</summary>
public sealed record NotePitchOp : EditOp {
    public required IReadOnlyList<string> NoteIds { get; init; }
    public PitchSpec? Pitch { get; init; }
}

public sealed record PitchSpec {
    /// <summary>[x ms from note start, y tenths of a semitone, shape io|l|i|o|sp].</summary>
    public required IReadOnlyList<PitchPointDto> Points { get; init; }
    public bool SnapFirst { get; init; } = true;
}

/// <summary>
/// Sets a per-note expression. Value applies to every phoneme; Values gives one value per phoneme
/// (one note only, and not on a note created or re-lyricked in the same plan). Both null = default.
/// </summary>
public sealed record NoteExprOp : EditOp {
    public required IReadOnlyList<string> NoteIds { get; init; }
    public required string Abbr { get; init; }
    public float? Value { get; init; }
    public IReadOnlyList<float?>? Values { get; init; }
}

/// <summary>Replaces the curve between FromTick and ToTick (part-relative); points outside are untouched.</summary>
public sealed record CurveSetRangeOp : EditOp {
    public required string PartId { get; init; }
    public required string Abbr { get; init; }
    public required int FromTick { get; init; }
    public required int ToTick { get; init; }
    public required IReadOnlyList<int> Xs { get; init; }
    public required IReadOnlyList<int> Ys { get; init; }
}

/// <summary>Clears a curve, or only the [FromTick, ToTick] range when both are given.</summary>
public sealed record CurveClearOp : EditOp {
    public required string PartId { get; init; }
    public required string Abbr { get; init; }
    public int? FromTick { get; init; }
    public int? ToTick { get; init; }
}

/// <summary>
/// Registers expression descriptors in the project (never removes any). Each abbreviation must be a
/// built-in OpenUtau expression or one suggested by the renderer of SuggestedFor (or of any track).
/// </summary>
public sealed record ProjectExpressionsOp : EditOp {
    public required IReadOnlyList<string> Ensure { get; init; }
    public string? SuggestedFor { get; init; }
    /// <summary>Take suggestions from this singer (and its default renderer) instead of a track: for tracks created in the same plan.</summary>
    public string? SuggestedForSinger { get; init; }
}

/// <summary>Adds a track at the end. CopyFrom copies singer, phonemizer and renderer of another track.</summary>
public sealed record TrackAddOp : EditOp {
    public string? Name { get; init; }
    public string? SingerId { get; init; }
    public string? CopyFrom { get; init; }
    public string? Color { get; init; }
    public string? Ref { get; init; }
}

/// <summary>Mixer change. Not undoable in OpenUtau (warning NOT_UNDOABLE); the track header may not refresh.</summary>
public sealed record TrackMixOp : EditOp {
    public required string TrackId { get; init; }
    public double? VolumeDb { get; init; }
    public double? Pan { get; init; }
    public bool? Mute { get; init; }
    public bool? Solo { get; init; }
}

/// <summary>Adds an empty voice part. Pos is absolute.</summary>
public sealed record PartAddOp : EditOp {
    public required string TrackId { get; init; }
    public required int Pos { get; init; }
    public required int Dur { get; init; }
    public string? Name { get; init; }
    public string? Ref { get; init; }
}

/// <summary>Copies a part (with notes and curves) to a track. Pos is absolute; default = same position.</summary>
public sealed record PartDuplicateOp : EditOp {
    public required string PartId { get; init; }
    public required string TrackId { get; init; }
    public int? Pos { get; init; }
    public string? Name { get; init; }
    public string? Ref { get; init; }
    /// <summary>
    /// When set, each copied note gets the ref "{NoteRefPrefix}{source note id}" (e.g. "@d0:n12") so later ops of the same
    /// plan can edit the copy. The source must be an existing part (not a ref).
    /// </summary>
    public string? NoteRefPrefix { get; init; }
}

public sealed record EditApplyParams {
    /// <summary>Name of the undo group shown in OpenUtau's history (English).</summary>
    public string? UndoName { get; init; }
    /// <summary>Optimistic concurrency: expected partRev per part id.</summary>
    public IReadOnlyDictionary<string, int>? IfRev { get; init; }
    public bool DryRun { get; init; }
    /// <summary>Allow notes of touched parts to overlap after the plan (warning OVERLAP instead of failing).</summary>
    public bool AllowOverlap { get; init; }
    public required IReadOnlyList<EditOp> Ops { get; init; }
}

public sealed record EditApplyResult {
    public required int Epoch { get; init; }
    public required int Applied { get; init; }
    public required bool DryRun { get; init; }
    public required IReadOnlyDictionary<string, int> PartRevs { get; init; }
    public required int StructureRev { get; init; }
    /// <summary>Created objects: symbolic ref (or "#opIndex" when the op had no ref) to new id.</summary>
    public IReadOnlyDictionary<string, string>? Created { get; init; }
    public IReadOnlyList<string>? Removed { get; init; }
    public IReadOnlyList<Warning>? Warnings { get; init; }
}

/// <summary>Resets every per-note/per-phoneme expression of the notes to the defaults.</summary>
public sealed record NoteResetExprsOp : EditOp {
    public required IReadOnlyList<string> NoteIds { get; init; }
}

/// <summary>Per-note phonemizer override: a phonemizer id, class name or tag; null = use the track's phonemizer.</summary>
public sealed record NotePhonemizerOp : EditOp {
    public required IReadOnlyList<string> NoteIds { get; init; }
    public string? Phonemizer { get; init; }
}

/// <summary>
/// Phoneme overrides of one existing note (not created or re-lyricked in the same plan). Overrides = null clears
/// timing overrides (offset, preutter, overlap) and aliases.
/// </summary>
public sealed record NotePhonemesOp : EditOp {
    public required string NoteId { get; init; }
    public IReadOnlyList<PhonemeOverrideDto>? Overrides { get; init; }
}

/// <summary>Override of phoneme <see cref="Index"/> of a note. Alias "" removes an alias; 0 deltas remove a timing override.</summary>
public sealed record PhonemeOverrideDto {
    public required int Index { get; init; }
    public string? Alias { get; init; }
    /// <summary>Position offset in ticks.</summary>
    public int? Offset { get; init; }
    public float? PreutterDeltaMs { get; init; }
    public float? OverlapDeltaMs { get; init; }
}

/// <summary>Audio part from a WAV/FLAC/MP3/OGG file at absolute tick Pos on TrackId (an id or a ref).</summary>
public sealed record PartAddWaveOp : EditOp {
    public required string TrackId { get; init; }
    public required string File { get; init; }
    public int Pos { get; init; }
    public string? Ref { get; init; }
}
