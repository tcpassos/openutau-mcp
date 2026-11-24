using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenUtauMcp.Music.Song;

/// <summary>
/// Declarative song the LLM writes for ou_music_compose. Sections in order,
/// voices with melody+lyric lines per section, or voices derived from another voice (harmony, double, octave).
/// </summary>
public sealed record SongSpec {
    public string Title { get; init; } = "Untitled";
    public double Bpm { get; init; } = 120;
    public string TimeSig { get; init; } = "4/4";
    /// <summary>"Dm", "F", "E dorian"...</summary>
    public string Key { get; init; } = "C";
    public IReadOnlyList<SectionSpec> Sections { get; init; } = [];
    public IReadOnlyList<VoiceSpec> Voices { get; init; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static SongSpec Parse(string json) =>
        JsonSerializer.Deserialize<SongSpec>(json, JsonOptions) ?? throw new JsonException("The song spec is empty.");
}

public sealed record SectionSpec {
    public required string Name { get; init; }
    /// <summary>intro | verse | pre | chorus | drop | bridge | outro | other.</summary>
    public string Role { get; init; } = "other";
    public required int Bars { get; init; }
    /// <summary>Optional, one per bar; kept with the sections for the user and later tools.</summary>
    public IReadOnlyList<string>? Chords { get; init; }
}

public sealed record VoiceSpec {
    public required string Name { get; init; }
    /// <summary>Singer id or name; the tool's tracks map can override it.</summary>
    public string? Singer { get; init; }
    public IReadOnlyList<LineSpec>? Lines { get; init; }
    public DeriveSpec? Derive { get; init; }
    /// <summary>Humanization of this voice when the call asks for it: {preset, seed}.</summary>
    public VoiceHumanize? Humanize { get; init; }
}

public sealed record VoiceHumanize(string? Preset = null, int? Seed = null);

/// <summary>
/// One melodic line. Either lyrics+pitches+rhythm, or sameAs (repeat this voice's lines of another section).
/// </summary>
public sealed record LineSpec {
    public required string Section { get; init; }
    /// <summary>Start inside the section, "bar:beat" (1-based). Omitted = right after the previous line of the section.</summary>
    public string? At { get; init; }
    /// <summary>Hyphen-separated syllables: "Can-dles burn-ing" = Candles, +, burning, +.</summary>
    public string? Lyrics { get; init; }
    /// <summary>One per syllable: names or MIDI numbers; "~" repeats the previous pitch.</summary>
    public string? Pitches { get; init; }
    /// <summary>Durations: "1/8*8 3q", "r:1/8" is a rest, "*n" repeats.</summary>
    public string? Rhythm { get; init; }
    /// <summary>Rest after the line before the next one starts.</summary>
    public string? RestAfter { get; init; }
    /// <summary>Copy this voice's lines of another section to this section.</summary>
    public string? SameAs { get; init; }
}

public sealed record DeriveSpec {
    /// <summary>Voice of this spec to derive from.</summary>
    public string? From { get; init; }
    /// <summary>Existing part of the open project (ou_music_compose target append).</summary>
    public string? FromPart { get; init; }
    /// <summary>harmony | double | octave.</summary>
    public required string Kind { get; init; }
    /// <summary>harmony: third_below (default), third_above, fourth_below/above, fifth_below/above, sixth_below/above.</summary>
    public string? Interval { get; init; }
    /// <summary>Only notes in sections with these roles (e.g. ["chorus"]).</summary>
    public IReadOnlyList<string>? Roles { get; init; }
    /// <summary>double: detune in cents (default 6).</summary>
    public int? DetuneCents { get; init; }
    /// <summary>octave: up | down (default down).</summary>
    public string? Direction { get; init; }
    public int? Seed { get; init; }
}
