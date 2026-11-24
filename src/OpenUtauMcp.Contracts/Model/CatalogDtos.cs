namespace OpenUtauMcp.Contracts.Model;

public sealed record CurvesQueryParams {
    public required string PartId { get; init; }
    /// <summary>Curves to read; null = every curve with data.</summary>
    public IReadOnlyList<string>? Abbrs { get; init; }
    /// <summary>Part-relative tick range; null = whole part.</summary>
    public TickRange? Range { get; init; }
    /// <summary>Also return the "real" curves the renderer produced (after refresh_real_curves), in the curve's units.</summary>
    public bool Real { get; init; }
}

public sealed record CurvesQueryResult {
    public required string PartId { get; init; }
    public required int PartPos { get; init; }
    public required int PartRev { get; init; }
    public required int Epoch { get; init; }
    public required IReadOnlyList<CurveDto> Curves { get; init; }
}

/// <summary>Curve points in part-relative ticks; values between points are linear.</summary>
public sealed record CurveDto {
    public required string Abbr { get; init; }
    public required IReadOnlyList<int> Xs { get; init; }
    public required IReadOnlyList<int> Ys { get; init; }
    public float Min { get; init; }
    public float Max { get; init; }
    public float DefaultValue { get; init; }
    public IReadOnlyList<int>? RealXs { get; init; }
    public IReadOnlyList<int>? RealYs { get; init; }
}

public sealed record CatalogSingerParams {
    /// <summary>Singer id; or give TrackId to describe the singer of that track.</summary>
    public string? Id { get; init; }
    public string? TrackId { get; init; }
}

public static class Effects {
    public const string Effective = "effective";
    public const string NoEffect = "noEffect";
    public const string Unknown = "unknown";
}

public sealed record SingerInfo {
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required bool Found { get; init; }
    public string? DefaultPhonemizer { get; init; }
    public IReadOnlyList<string>? Renderers { get; init; }
    /// <summary>Voice colors (subbanks) as OpenUtau lists them; the "clr" value is the index.</summary>
    public IReadOnlyList<string>? Colors { get; init; }
    public DiffSingerInfo? DiffSinger { get; init; }
    /// <summary>Whether writing an expression changes the sound with this singer: effective | noEffect | unknown.</summary>
    public required IReadOnlyDictionary<string, string> Effects { get; init; }
}

public sealed record DiffSingerInfo {
    public IReadOnlyList<string>? Speakers { get; init; }
    public required IReadOnlyDictionary<string, bool> Embeds { get; init; }
    public bool HasPitchPredictor { get; init; }
    public bool HasVariancePredictor { get; init; }
}

public sealed record CatalogExpressionsParams(string TrackId);

public sealed record ExpressionsCatalog {
    public required string TrackId { get; init; }
    public string? Renderer { get; init; }
    public required IReadOnlyList<ExpressionInfo> Expressions { get; init; }
}

public sealed record ExpressionInfo {
    public required string Abbr { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public float Min { get; init; }
    public float Max { get; init; }
    public float DefaultValue { get; init; }
    /// <summary>Registered in project.expressions (curves can only be written when it is).</summary>
    public required bool Registered { get; init; }
    /// <summary>Suggested by the track's renderer (registers with project.expressions ensure).</summary>
    public bool Suggested { get; init; }
    public bool SupportedByRenderer { get; init; }
    public required string Effect { get; init; }
}

public sealed record ProjectNewParams {
    public string OnUnsaved { get; init; } = Model.OnUnsaved.Fail;
}

public sealed record CatalogListParams {
    /// <summary>Case-insensitive substring filter on id/name.</summary>
    public string? Query { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; } = 50;
}

public sealed record SingersCatalog {
    public required int Total { get; init; }
    public required IReadOnlyList<SingerSummary> Singers { get; init; }
}

public sealed record SingerSummary(string Id, string Name, string Type, string? Author, string? Version, bool Found);

public sealed record PhonemizersCatalog {
    public required int Total { get; init; }
    public required IReadOnlyList<PhonemizerSummary> Phonemizers { get; init; }
}

/// <summary>Id = factory id (class full name) accepted by track/note phonemizer ops.</summary>
public sealed record PhonemizerSummary(string Id, string Name, string? Tag, string? Language, string? Author);

public sealed record RenderersCatalog {
    /// <summary>Renderer names per singer type, e.g. DiffSinger -> [DIFFSINGER].</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> BySingerType { get; init; }
}

public sealed record G2pQueryParams {
    public required string TrackId { get; init; }
    public required IReadOnlyList<string> Words { get; init; }
}

public sealed record G2pQueryResult {
    public required string TrackId { get; init; }
    public string? Phonemizer { get; init; }
    /// <summary>Null when the track's phonemizer has no queryable dictionary.</summary>
    public IReadOnlyList<G2pEntry>? Words { get; init; }
}

/// <summary>Phonemes null = the word is not in the dictionary (the phonemizer guesses or fails).</summary>
public sealed record G2pEntry(string Word, IReadOnlyList<string>? Phonemes);

/// <summary>daw.*: OpenUtau's DAW link (plugin in the DAW); the bridge only drives OpenUtau's DawManager.</summary>
public sealed record DawState {
    /// <summary>Disconnected | Connecting | Connected | Reconnecting.</summary>
    public required string State { get; init; }
    public required IReadOnlyList<DawConnection> Connections { get; init; }
    /// <summary>Plugins found by the last scan (daw.scan).</summary>
    public IReadOnlyList<DawServerDto>? Servers { get; init; }
    public bool Vst3Installed { get; init; }
    public bool ClapInstalled { get; init; }
    /// <summary>Tempo the DAW last reported (null until a plugin sends it).</summary>
    public double? DawBpm { get; init; }
    public double ProjectBpm { get; init; }
    /// <summary>Whether this OpenUtau binds plugin instances to tracks by id (DAW protocol 1.3), so
    /// connections report their track and daw.bind works.</summary>
    public bool BindingSupported { get; init; }
}

public sealed record DawConnection(string Name, int Port, string State) {
    /// <summary>bound | unbound (no track yet) | missing (its track was deleted, or belongs to
    /// another project); null when this OpenUtau or the plugin does not report bindings.</summary>
    public string? Binding { get; init; }
    /// <summary>The MCP id of the track the instance plays, when bound.</summary>
    public string? TrackId { get; init; }
    public string? TrackName { get; init; }
    /// <summary>The DAW's own name for the track the instance sits on, when the host reports one.</summary>
    public string? HostTrackName { get; init; }
}

public sealed record DawServerDto(string Name, int Port, string ApiVersion, bool Compatible);

public sealed record DawTargetParams {
    /// <summary>Plugin name or port as listed by daw.scan; daw.disconnect without one disconnects all.</summary>
    public string? Server { get; init; }
    /// <summary>daw.bind: the MCP id of the track the instance should play.</summary>
    public string? TrackId { get; init; }
}
