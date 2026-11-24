using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenUtauMcp.Music.Theory;

/// <summary>Pitch names: C4 = 60, sharps and flats ("C#4", "Bb3"), or a MIDI number.</summary>
public static partial class PitchName {
    static readonly Dictionary<string, int> classes = new(StringComparer.OrdinalIgnoreCase) {
        ["C"] = 0, ["C#"] = 1, ["Db"] = 1, ["D"] = 2, ["D#"] = 3, ["Eb"] = 3, ["E"] = 4, ["Fb"] = 4, ["E#"] = 5, ["F"] = 5,
        ["F#"] = 6, ["Gb"] = 6, ["G"] = 7, ["G#"] = 8, ["Ab"] = 8, ["A"] = 9, ["A#"] = 10, ["Bb"] = 10, ["B"] = 11, ["Cb"] = 11, ["B#"] = 0,
    };

    static readonly string[] sharpNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    [GeneratedRegex(@"^([A-Ga-g][#b]?)(-?\d)$")]
    private static partial Regex NamePattern();

    public static bool TryParse(string text, out int midi) {
        midi = 0;
        string s = text.Trim();
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) {
            midi = n;
            return n is >= 0 and <= 127;
        }
        var m = NamePattern().Match(s);
        if (!m.Success || !classes.TryGetValue(m.Groups[1].Value, out int pc)) {
            return false;
        }
        int octave = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        // B#3 is C4 and Cb4 is B3: the octave number belongs to the letter.
        string letter = m.Groups[1].Value;
        int shift = letter.Equals("B#", StringComparison.OrdinalIgnoreCase) ? 12
            : letter.Equals("Cb", StringComparison.OrdinalIgnoreCase) ? -12 : 0;
        midi = 12 * (octave + 1) + pc + shift;
        return midi is >= 0 and <= 127;
    }

    public static int PitchClass(string name) =>
        classes.TryGetValue(name.Trim(), out int pc) ? pc : throw new FormatException($"Unknown note name '{name}'.");

    public static string Format(int midi) => $"{sharpNames[((midi % 12) + 12) % 12]}{midi / 12 - 1}";
}

public enum Mode { Major, Minor, HarmonicMinor, Dorian, Mixolydian }

/// <summary>A key: tonic pitch class plus mode, with diatonic steps (harmony lines).</summary>
public sealed record Scale(int Tonic, Mode Mode) {
    static readonly Dictionary<Mode, int[]> steps = new() {
        [Mode.Major] = [0, 2, 4, 5, 7, 9, 11],
        [Mode.Minor] = [0, 2, 3, 5, 7, 8, 10],
        [Mode.HarmonicMinor] = [0, 2, 3, 5, 7, 8, 11],
        [Mode.Dorian] = [0, 2, 3, 5, 7, 9, 10],
        [Mode.Mixolydian] = [0, 2, 4, 5, 7, 9, 10],
    };

    /// <summary>Pitch classes of the scale, from the tonic.</summary>
    public IReadOnlyList<int> PitchClasses => steps[Mode].Select(s => (Tonic + s) % 12).ToList();

    public bool Contains(int midi) => PitchClasses.Contains(((midi % 12) + 12) % 12);

    /// <summary>"Dm", "D minor", "F#", "Bb major", "E dorian", "A harmonic minor".</summary>
    public static Scale Parse(string text) {
        string s = text.Trim();
        Mode mode = Mode.Major;
        string tonic = s;
        int space = s.IndexOf(' ');
        if (space > 0) {
            tonic = s[..space];
            string m = s[(space + 1)..].Trim().ToLowerInvariant().Replace(" ", "");
            mode = m switch {
                "major" or "maj" => Mode.Major,
                "minor" or "min" => Mode.Minor,
                "harmonicminor" => Mode.HarmonicMinor,
                "dorian" => Mode.Dorian,
                "mixolydian" => Mode.Mixolydian,
                _ => throw new FormatException($"Unknown mode '{s[(space + 1)..]}'. Use major, minor, harmonic minor, dorian or mixolydian."),
            };
        } else if (s.Length > 1 && s.EndsWith('m')) {
            tonic = s[..^1];
            mode = Mode.Minor;
        }
        return new Scale(PitchName.PitchClass(tonic), mode);
    }

    /// <summary>
    /// Moves a pitch by scale degrees (negative = down). A pitch outside the scale first counts as the nearest scale
    /// pitch below it (C# in D minor counts as C), then the result keeps the original direction strictly.
    /// </summary>
    public int Step(int midi, int degrees) {
        var pcs = PitchClasses;
        int baseMidi = midi;
        while (!pcs.Contains(((baseMidi % 12) + 12) % 12)) {
            baseMidi--;
        }
        int index = pcs.ToList().IndexOf(((baseMidi % 12) + 12) % 12);
        int target = index + degrees;
        int octaves = (int)Math.Floor(target / 7.0);
        int degree = target - octaves * 7;
        int startOctaveBase = baseMidi - (((baseMidi % 12) + 12) % 12 - pcs[0] + 12) % 12;   // tonic at or below baseMidi
        int stepsFromTonic = (pcs[degree] - pcs[0] + 12) % 12;
        int result = startOctaveBase + octaves * 12 + stepsFromTonic;
        if (degrees < 0 && result >= midi) {
            result -= 12;
        } else if (degrees > 0 && result <= midi) {
            result += 12;
        }
        return result;
    }
}
