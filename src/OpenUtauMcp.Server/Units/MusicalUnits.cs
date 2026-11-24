using System.Globalization;
using System.Text.RegularExpressions;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Protocol;

namespace OpenUtauMcp.Server.Units;

/// <summary>
/// Converts between absolute ticks and "bar:beat" positions using the project's time signatures.
/// Bars and beats are 1-based; beats may be fractional.
/// Accepted: "17:1", "17:2.5", "17:3+60" (beat plus ticks), "17" (bar start) or an integer absolute tick.
/// </summary>
public sealed partial class TimeMap {
    const int TicksPerWhole = ProtocolInfo.TicksPerQuarter * 4;

    sealed record Segment(int FirstBar, int StartTick, int BeatPerBar, int BeatUnit) {
        public int BeatTicks => TicksPerWhole / BeatUnit;
        public int BarTicks => BeatPerBar * BeatTicks;
    }

    readonly List<Segment> segments = new();

    public TimeMap(IReadOnlyList<TimeSignatureDto>? signatures) {
        var sorted = (signatures is { Count: > 0 } ? signatures : [new TimeSignatureDto(0, 4, 4)])
            .OrderBy(s => s.Bar).ToList();
        if (sorted[0].Bar != 0) {
            sorted.Insert(0, new TimeSignatureDto(0, 4, 4));
        }
        int tick = 0;
        for (int i = 0; i < sorted.Count; i++) {
            var s = sorted[i];
            if (i > 0) {
                var prev = segments[^1];
                tick = prev.StartTick + (s.Bar - prev.FirstBar) * prev.BarTicks;
            }
            segments.Add(new Segment(s.Bar, tick, s.BeatPerBar, s.BeatUnit));
        }
    }

    public static TimeMap Default { get; } = new(null);

    [GeneratedRegex(@"^\s*(?<bar>\d+)\s*(:\s*(?<beat>\d+(\.\d+)?)\s*)?(\+\s*(?<ticks>-?\d+)\s*)?$")]
    private static partial Regex PositionPattern();

    /// <summary>Parses a position to an absolute tick. Throws <see cref="UnitException"/> on bad input.</summary>
    public int ParsePosition(string text) {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int raw) && !text.Contains(':')) {
            // A bare integer is an absolute tick (bars must be written as "17:1").
            if (raw < 0) {
                throw new UnitException(UnitException.BadPosition, $"Position '{text}' is negative.");
            }
            return raw;
        }
        var m = PositionPattern().Match(text);
        if (!m.Success) {
            throw new UnitException(UnitException.BadPosition,
                $"Cannot read position '{text}'. Use \"bar:beat\" like \"17:1\", \"17:2.5\", \"17:3+60\", or an absolute tick.");
        }
        int bar = int.Parse(m.Groups["bar"].Value, CultureInfo.InvariantCulture);
        double beat = m.Groups["beat"].Success ? double.Parse(m.Groups["beat"].Value, CultureInfo.InvariantCulture) : 1;
        int extra = m.Groups["ticks"].Success ? int.Parse(m.Groups["ticks"].Value, CultureInfo.InvariantCulture) : 0;
        if (bar < 1 || beat < 1) {
            throw new UnitException(UnitException.BadPosition, $"Position '{text}': bars and beats start at 1.");
        }
        var seg = SegmentForBar(bar - 1);
        if (beat >= seg.BeatPerBar + 1) {
            throw new UnitException(UnitException.BadPosition,
                $"Position '{text}': bar {bar} has only {seg.BeatPerBar} beats ({seg.BeatPerBar}/{seg.BeatUnit}).");
        }
        double tick = BarStartTick(bar - 1) + (beat - 1) * seg.BeatTicks + extra;
        return (int)Math.Round(tick, MidpointRounding.ToEven);
    }

    /// <summary>Formats an absolute tick as "bar:beat" (exact fractional beat when short, else "bar:beat+ticks").</summary>
    public string FormatPosition(int tick) {
        var seg = segments.Last(s => s.StartTick <= Math.Max(tick, 0));
        int barsIn = (tick - seg.StartTick) / seg.BarTicks;
        int bar = seg.FirstBar + barsIn;
        int inBar = tick - seg.StartTick - barsIn * seg.BarTicks;
        int beatIndex = inBar / seg.BeatTicks;
        int rest = inBar - beatIndex * seg.BeatTicks;
        if (rest == 0) {
            return $"{bar + 1}:{beatIndex + 1}";
        }
        double fraction = rest / (double)seg.BeatTicks;
        double rounded = Math.Round(fraction, 3);
        if (Math.Abs(rounded - fraction) < 1e-9) {
            return string.Create(CultureInfo.InvariantCulture, $"{bar + 1}:{beatIndex + 1 + rounded:0.###}");
        }
        return $"{bar + 1}:{beatIndex + 1}+{rest}";
    }

    /// <summary>Absolute tick where the 0-based bar starts.</summary>
    public int BarStartTick(int bar0) {
        var seg = SegmentForBar(bar0);
        return seg.StartTick + (bar0 - seg.FirstBar) * seg.BarTicks;
    }

    Segment SegmentForBar(int bar0) => segments.Last(s => s.FirstBar <= Math.Max(bar0, 0));

    /// <summary>[from, to) of a bar range like "17-24" or "17".</summary>
    public (int from, int to) ParseBars(string text) {
        var parts = text.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || !parts.All(p => int.TryParse(p, out int b) && b >= 1)) {
            throw new UnitException(UnitException.BadPosition, $"Cannot read bars '{text}'. Use \"17\" or \"17-24\".");
        }
        int first = int.Parse(parts[0], CultureInfo.InvariantCulture);
        int last = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : first;
        if (last < first) {
            throw new UnitException(UnitException.BadPosition, $"Bars '{text}': the end is before the start.");
        }
        return (BarStartTick(first - 1), BarStartTick(last));
    }
}

/// <summary>Durations: integer ticks, "&lt;n&gt;q" quarters, or a whole-note fraction "1/8", dotted "1/4.".</summary>
public static partial class Durations {
    [GeneratedRegex(@"^\s*(?<num>\d+)\s*/\s*(?<den>\d+)\s*(?<dot>\.)?\s*$")]
    private static partial Regex FractionPattern();

    public static int Parse(string text) {
        string t = text.Trim();
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ticks)) {
            return ticks > 0 ? ticks : throw new UnitException(UnitException.BadDuration, $"Duration '{text}' must be positive.");
        }
        if (t.EndsWith('q') && double.TryParse(t[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double quarters) && quarters > 0) {
            return (int)Math.Round(quarters * ProtocolInfo.TicksPerQuarter, MidpointRounding.ToEven);
        }
        var m = FractionPattern().Match(t);
        if (m.Success) {
            int num = int.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture);
            int den = int.Parse(m.Groups["den"].Value, CultureInfo.InvariantCulture);
            if (num > 0 && den > 0) {
                double value = ProtocolInfo.TicksPerQuarter * 4.0 * num / den * (m.Groups["dot"].Success ? 1.5 : 1);
                return (int)Math.Round(value, MidpointRounding.ToEven);
            }
        }
        throw new UnitException(UnitException.BadDuration,
            $"Cannot read duration '{text}'. Use ticks (240), quarters (\"0.5q\") or a fraction (\"1/8\", dotted \"1/4.\").");
    }

    /// <summary>Formats ticks as quarters, e.g. 240 -> "0.5q", 1440 -> "3q"; falls back to ticks.</summary>
    public static string Format(int ticks) {
        double quarters = ticks / (double)ProtocolInfo.TicksPerQuarter;
        double rounded = Math.Round(quarters, 3);
        return Math.Abs(rounded - quarters) < 1e-9
            ? string.Create(CultureInfo.InvariantCulture, $"{rounded:0.###}q")
            : ticks.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>Pitches in scientific notation (C4 = 60) or MIDI numbers.</summary>
public static partial class Pitches {
    static readonly string[] sharpNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    static readonly Dictionary<char, int> letters = new() { ['C'] = 0, ['D'] = 2, ['E'] = 4, ['F'] = 5, ['G'] = 7, ['A'] = 9, ['B'] = 11 };

    [GeneratedRegex(@"^\s*(?<letter>[A-Ga-g])(?<acc>#|b)?(?<oct>-?\d)\s*$")]
    private static partial Regex NamePattern();

    public static int Parse(string text) {
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int midi)) {
            return midi is >= 0 and <= 127 ? midi : throw new UnitException(UnitException.BadPitch, $"MIDI pitch {midi} is outside 0..127.");
        }
        var m = NamePattern().Match(text);
        if (!m.Success) {
            throw new UnitException(UnitException.BadPitch, $"Cannot read pitch '{text}'. Use a name like \"A4\", \"C#5\", \"Bb3\" or a MIDI number.");
        }
        int value = letters[char.ToUpperInvariant(m.Groups["letter"].Value[0])]
                    + m.Groups["acc"].Value switch { "#" => 1, "b" => -1, _ => 0 }
                    + (int.Parse(m.Groups["oct"].Value, CultureInfo.InvariantCulture) + 1) * 12;
        return value is >= 0 and <= 127 ? value : throw new UnitException(UnitException.BadPitch, $"Pitch '{text}' is outside the MIDI range.");
    }

    public static string Format(int midi) => $"{sharpNames[((midi % 12) + 12) % 12]}{midi / 12 - 1}";
}

public sealed class UnitException(string code, string message) : Exception(message) {
    public const string BadPosition = "BAD_POSITION";
    public const string BadDuration = "BAD_DURATION";
    public const string BadPitch = "BAD_PITCH";

    public string Code { get; } = code;
}
