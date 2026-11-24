namespace OpenUtauMcp.Music.Humanize;

/// <summary>A note of the part being humanized. Pos/Dur in ticks relative to the part.</summary>
public sealed record HNote(string Id, int Pos, int Dur, int Tone, string Lyric) {
    public int End => Pos + Dur;
    public bool IsBreath => Breaths.IsBreathLyric(Lyric);
}

public static class Breaths {
    static readonly HashSet<string> lyrics = new(StringComparer.OrdinalIgnoreCase) { "AP", "SP", "br", "breath" };

    public static bool IsBreathLyric(string lyric) => lyrics.Contains(lyric);
}

/// <summary>Section roles.</summary>
public static class Roles {
    public const string Intro = "intro", Verse = "verse", Pre = "pre", Chorus = "chorus", Drop = "drop",
        Bridge = "bridge", Outro = "outro", Other = "other";

    public static readonly string[] All = [Intro, Verse, Pre, Chorus, Drop, Bridge, Outro, Other];
}

/// <summary>A song section in absolute ticks [From, To).</summary>
public sealed record Section(string Name, string Role, int From, int To);

/// <summary>Abstract voice color roles, mapped to the singer's colors by the caller.</summary>
public static class ColorRoles {
    public const string Normal = "normal", Power = "power", Soft = "soft";
}

/// <summary>Pitch point in OpenUtau units: x in ms from the note start, y in tenths of a semitone.</summary>
public sealed record PitchPoint(double X, double Y, string Shape = "io");

public sealed record Vibrato(double Length, double Period, double Depth, double In, double Out, double Shift);

public sealed record HumanizeInput {
    public required IReadOnlyList<HNote> Notes { get; init; }
    /// <summary>Absolute tick where the part starts (sections are absolute).</summary>
    public int PartPos { get; init; }
    public double Bpm { get; init; } = 120;
    public IReadOnlyList<Section> Sections { get; init; } = [];
    /// <summary>Notes to change (ids); null = the whole part.</summary>
    public IReadOnlySet<string>? Selection { get; init; }
    /// <summary>Voice color index for each abstract role (normal/power/soft) or section name; missing = no clr.</summary>
    public IReadOnlyDictionary<string, int> Colors { get; init; } = new Dictionary<string, int>();
}

/// <summary>Planned values for one existing note (null = layer not applied to it).</summary>
public sealed record NotePlan(string Id) {
    public IReadOnlyList<PitchPoint>? Pitch { get; set; }
    public Vibrato? Vibrato { get; set; }
    public int? Color { get; set; }
}

public sealed record BreathNote(int Pos, int Dur, int Tone, string Lyric);

public sealed record HumanizeResult {
    public required IReadOnlyList<NotePlan> Notes { get; init; }
    public required IReadOnlyList<BreathNote> BreathsToAdd { get; init; }
    public required IReadOnlyList<string> BreathsToRemove { get; init; }
    /// <summary>Curve points on the 5-tick grid, part-relative, only inside [RangeFrom, RangeTo].</summary>
    public required IReadOnlyDictionary<string, SortedDictionary<int, int>> Curves { get; init; }
    public required int RangeFrom { get; init; }
    public required int RangeTo { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required IReadOnlyDictionary<string, int> LayerCounts { get; init; }
}
