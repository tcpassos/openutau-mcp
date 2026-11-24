using OpenUtauMcp.Music.Humanize;
using OpenUtauMcp.Music.Theory;

namespace OpenUtauMcp.Music.Song;

/// <summary>
/// Derived voices: harmony (diatonic interval in the song's key), double (unison
/// with a slight detune) and octave. Lyrics and timing follow the source; breaths are not copied.
/// </summary>
public static class Arranger {
    static readonly Dictionary<string, int> intervals = new() {
        ["third_below"] = -2, ["third_above"] = 2, ["fourth_below"] = -3, ["fourth_above"] = 3,
        ["fifth_below"] = -4, ["fifth_above"] = 4, ["sixth_below"] = -5, ["sixth_above"] = 5,
    };

    public static IReadOnlyList<string> IntervalNames => intervals.Keys.ToList();

    public static List<ComposedNote> Derive(IReadOnlyList<ComposedNote> source, DeriveSpec d, Scale scale, IReadOnlyList<ComposedSection> sections) {
        var roles = d.Roles is { Count: > 0 } r ? r.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        if (roles is not null && roles.FirstOrDefault(x => !Roles.All.Contains(x)) is { } unknown) {
            throw new ArgumentException($"Unknown role '{unknown}'. Use {string.Join(", ", Roles.All)}.");
        }
        var notes = source.Where(n => !Breaths.IsBreathLyric(n.Lyric)
                                      && (roles is null || sections.Any(s => n.Pos >= s.From && n.Pos < s.To && roles.Contains(s.Role))))
            .ToList();
        switch (d.Kind) {
            case "harmony": {
                string name = d.Interval ?? "third_below";
                if (!intervals.TryGetValue(name, out int degrees)) {
                    throw new ArgumentException($"Unknown interval '{name}'. Use {string.Join(", ", intervals.Keys)}.");
                }
                // A harmony above B9 or below C-1 is dropped, like the octave voice.
                return notes.Select(n => n with { Tone = scale.Step(n.Tone, degrees) }).Where(n => n.Tone is >= 0 and <= 127).ToList();
            }
            case "double": {
                int detune = d.DetuneCents ?? 6;
                if (detune is < 0 or > 50) {
                    throw new ArgumentException("detuneCents must be 0..50.");
                }
                // Deterministic per seed: each note gets +detune or -detune.
                ulong state = (ulong)(d.Seed ?? 1) * 0x9E3779B97F4A7C15UL + 1;
                return notes.Select(n => {
                    state ^= state << 13;
                    state ^= state >> 7;
                    state ^= state << 17;
                    return n with { Tuning = (state & 1) == 0 ? detune : -detune };
                }).ToList();
            }
            case "octave": {
                int shift = (d.Direction ?? "down") switch {
                    "down" => -12,
                    "up" => 12,
                    var other => throw new ArgumentException($"Unknown direction '{other}'. Use up or down."),
                };
                return notes.Select(n => n with { Tone = n.Tone + shift }).Where(n => n.Tone is >= 0 and <= 127).ToList();
            }
            default:
                throw new ArgumentException($"Unknown derive kind '{d.Kind}'. Use harmony, double or octave.");
        }
    }
}
