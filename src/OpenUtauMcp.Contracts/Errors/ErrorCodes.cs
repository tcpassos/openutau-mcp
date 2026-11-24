namespace OpenUtauMcp.Contracts.Errors;

/// <summary>Symbolic application error codes (error.data.code).</summary>
public static class ErrorCodes {
    public const string ProtocolMismatch = "PROTOCOL_MISMATCH";
    public const string VersionMismatch = "VERSION_MISMATCH";
    public const string NotReady = "NOT_READY";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string MethodNotFound = "METHOD_NOT_FOUND";
    public const string CapabilityUnavailable = "CAPABILITY_UNAVAILABLE";
    public const string InvalidParams = "INVALID_PARAMS";
    public const string OutOfRange = "OUT_OF_RANGE";
    public const string NotFound = "NOT_FOUND";
    public const string StaleEpoch = "STALE_EPOCH";
    public const string Conflict = "CONFLICT";
    public const string WrongKind = "WRONG_KIND";
    public const string UnknownExpression = "UNKNOWN_EXPRESSION";
    public const string BusyUserEditing = "BUSY_USER_EDITING";
    public const string BusyJob = "BUSY_JOB";
    public const string UiUnresponsive = "UI_UNRESPONSIVE";
    public const string UnsavedChanges = "UNSAVED_CHANGES";
    public const string IoError = "IO_ERROR";
    public const string UnsupportedFormat = "UNSUPPORTED_FORMAT";
    public const string RenderFailed = "RENDER_FAILED";
    public const string TrackMuted = "TRACK_MUTED";
    public const string BatchFailed = "BATCH_FAILED";
    public const string PhonemizerTimeout = "PHONEMIZER_TIMEOUT";
    public const string NoSinger = "NO_SINGER";
    public const string SingerNotFound = "SINGER_NOT_FOUND";
    public const string Cancelled = "CANCELLED";
    public const string Timeout = "TIMEOUT";
    public const string LimitExceeded = "LIMIT_EXCEEDED";
    public const string OpFailed = "OP_FAILED";
    public const string Internal = "INTERNAL";

    /// <summary>Codes a client may retry automatically (after re-reading when relevant).</summary>
    public static bool IsRetryable(string code) => code is NotReady or Conflict or BusyUserEditing
        or BusyJob or UiUnresponsive or PhonemizerTimeout or Timeout;
}

/// <summary>Non-fatal warning codes (result.warnings[].code).</summary>
public static class WarningCodes {
    public const string Clamped = "CLAMPED";
    public const string SnappedToGrid = "SNAPPED_TO_GRID";
    public const string RealtimePitchOn = "REALTIME_PITCH_ON";
    public const string NotUndoable = "NOT_UNDOABLE";
    public const string DerivedStale = "DERIVED_STALE";
    public const string Overlap = "OVERLAP";
    public const string PartGrown = "PART_GROWN";
    public const string DialogMayOpen = "DIALOG_MAY_OPEN";
    public const string SeparateUndoStep = "SEPARATE_UNDO_STEP";
}

public sealed record Warning(string Code, string Message, string? Ref = null);
