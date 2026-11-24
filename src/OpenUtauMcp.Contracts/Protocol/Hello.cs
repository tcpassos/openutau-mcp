namespace OpenUtauMcp.Contracts.Protocol;

public sealed record VersionRange(int Min, int Max);

public sealed record ClientInfo(string Name, string Version);

public sealed record HelloParams {
    public required VersionRange Protocol { get; init; }
    public required ClientInfo Client { get; init; }
}

public sealed record HelloResult {
    public required int Protocol { get; init; }
    public required BridgeInfo Bridge { get; init; }
    public required OpenUtauInfo OpenUtau { get; init; }
    public required IReadOnlyList<string> Capabilities { get; init; }
    public required BridgeLimits Limits { get; init; }
    public required BridgeState State { get; init; }
}

public sealed record BridgeInfo {
    public required string Version { get; init; }
    /// <summary>Version of the OpenUtau.Core.dll the bridge was compiled against.</summary>
    public required string BuiltForCore { get; init; }
    /// <summary>SHA-256 of the OpenUtau.Core.dll the bridge was compiled against.</summary>
    public required string BuiltForCoreSha256 { get; init; }
    /// <summary>How the bridge was started: "earlyStart" (OpenUtau startup) or "batchEdit" (piano roll).</summary>
    public required string StartedBy { get; init; }
}

public sealed record OpenUtauInfo {
    public required string Version { get; init; }
    public required string CoreSha256 { get; init; }
    /// <summary>True when the running OpenUtau.Core.dll is the one the bridge was compiled against.</summary>
    public required bool Match { get; init; }
}

public sealed record BridgeLimits {
    public int MaxMessageBytes { get; init; } = ProtocolInfo.MaxMessageBytes;
    public int MaxOpsPerApply { get; init; } = ProtocolInfo.MaxOpsPerApply;
    public int MaxNotesPerQuery { get; init; } = ProtocolInfo.MaxNotesPerQuery;
    public int MaxCurvePoints { get; init; } = ProtocolInfo.MaxCurvePoints;
}

public sealed record BridgeState {
    public required bool Ready { get; init; }
    public required int Epoch { get; init; }
    public required int StructureRev { get; init; }
}

public sealed record PingResult {
    public required bool Ready { get; init; }
    public required int Epoch { get; init; }
    public double? UiLatencyMs { get; init; }
}
