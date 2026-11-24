using System.Globalization;
using System.Text.RegularExpressions;
using OpenUtauMcp.Music.Humanize;
using OpenUtauMcp.Music.Theory;

namespace OpenUtauMcp.Music.Song;

public sealed record ComposedNote(int Pos, int Dur, int Tone, string Lyric, int Tuning = 0);

public sealed record ComposedSection(string Name, string Role, int From, int To, IReadOnlyList<string>? Chords);

public sealed record ComposedVoice {
    public required string Name { get; init; }
    public string? Singer { get; init; }
    /// <summary>"lines", "harmony", "double" or "octave".</summary>
    public required string Kind { get; init; }
    public string? DerivedFrom { get; init; }
    public required IReadOnlyList<ComposedNote> Notes { get; init; }
    /// <summary>Preset to humanize with: the voice's own, else natural for lines and backing for derived voices.</summary>
    public required string HumanizePreset { get; init; }
    public int? HumanizeSeed { get; init; }
}

public sealed record ComposedSong {
    public required string Title { get; init; }
    public required double Bpm { get; init; }
    public required int BeatPerBar { get; init; }
    public required int BeatUnit { get; init; }
    public required Scale Scale { get; init; }
    public required string Key { get; init; }
    public required IReadOnlyList<ComposedSection> Sections { get; init; }
    public required IReadOnlyList<ComposedVoice> Voices { get; init; }
    /// <summary>End of the last section, in ticks.</summary>
    public required int EndTick { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    public IReadOnlyList<Section> HumanizeSections => Sections.Select(s => new Section(s.Name, s.Role, s.From, s.To)).ToList();
}

/// <summary>An error in the song spec, pointing at the field (e.g. "voices[0].lines[3]").</summary>
public sealed record SpecIssue(string Path, string Message);

public sealed class SongSpecException(IReadOnlyList<SpecIssue> issues)
    : Exception($"The song spec has {issues.Count} error(s): {string.Join("; ", issues.Take(5).Select(i => $"{i.Path}: {i.Message}"))}") {
    public IReadOnlyList<SpecIssue> Issues { get; } = issues;
}

/// <summary>
/// SongSpec to notes, the same way the Python prototype placed them: lines of a
/// section follow each other (start + durations + restAfter); syllables, pitches and non-rest durations must match.
/// </summary>
public static partial class Composer {
    public const int Ppq = 480;

    public static ComposedSong Compose(SongSpec spec) {
        var issues = new List<SpecIssue>();
        var warnings = new List<string>();

        int beatPerBar = 4, beatUnit = 4;
        var sig = spec.TimeSig.Split('/');
        if (sig.Length != 2 || !int.TryParse(sig[0], out beatPerBar) || !int.TryParse(sig[1], out beatUnit)
            || beatPerBar is < 1 or > 32 || beatUnit is not (1 or 2 or 4 or 8 or 16 or 32)) {
            issues.Add(new SpecIssue("timeSig", $"Cannot read time signature '{spec.TimeSig}'. Use \"4/4\", \"3/4\", \"6/8\"."));
            (beatPerBar, beatUnit) = (4, 4);
        }
        int beatTicks = Ppq * 4 / beatUnit, barTicks = beatPerBar * beatTicks;
        Scale scale;
        try {
            scale = Scale.Parse(spec.Key);
        } catch (FormatException e) {
            issues.Add(new SpecIssue("key", e.Message));
            scale = new Scale(0, Mode.Major);
        }
        if (spec.Bpm is < 10 or > 1000) {
            issues.Add(new SpecIssue("bpm", $"BPM {spec.Bpm} is outside 10..1000."));
        }

        var sections = new List<ComposedSection>();
        int tick = 0;
        for (int i = 0; i < spec.Sections.Count; i++) {
            var s = spec.Sections[i];
            if (!Roles.All.Contains(s.Role)) {
                issues.Add(new SpecIssue($"sections[{i}].role", $"Unknown role '{s.Role}'. Use {string.Join(", ", Roles.All)}."));
            }
            if (s.Bars < 1) {
                issues.Add(new SpecIssue($"sections[{i}].bars", "A section needs at least one bar."));
            }
            if (sections.Any(x => x.Name == s.Name)) {
                issues.Add(new SpecIssue($"sections[{i}].name", $"Section name '{s.Name}' is used twice."));
            }
            if (s.Chords is { } chords && chords.Count != s.Bars) {
                warnings.Add($"sections[{i}] ({s.Name}): {chords.Count} chords for {s.Bars} bars.");
            }
            sections.Add(new ComposedSection(s.Name, s.Role, tick, tick + Math.Max(1, s.Bars) * barTicks, s.Chords));
            tick += Math.Max(1, s.Bars) * barTicks;
        }
        if (sections.Count == 0) {
            issues.Add(new SpecIssue("sections", "The song needs at least one section."));
        }
        var byName = sections.ToDictionary(s => s.Name);

        var voices = new List<ComposedVoice>();
        var lineVoices = new Dictionary<string, List<(int lineIndex, string section, List<ComposedNote> notes, int start)>>();
        for (int v = 0; v < spec.Voices.Count; v++) {
            var voice = spec.Voices[v];
            if (spec.Voices.Take(v).Any(x => x.Name == voice.Name)) {
                issues.Add(new SpecIssue($"voices[{v}].name", $"Voice name '{voice.Name}' is used twice."));
            }
            if ((voice.Lines is { Count: > 0 }) == (voice.Derive is not null)) {
                issues.Add(new SpecIssue($"voices[{v}]", "A voice has either lines or derive."));
                continue;
            }
            if (voice.Lines is not { } lines) {
                continue;
            }
            var placed = new List<(int lineIndex, string section, List<ComposedNote> notes, int start)>();
            var cursor = new Dictionary<string, int>();
            for (int l = 0; l < lines.Count; l++) {
                string path = $"voices[{v}].lines[{l}]";
                var line = lines[l];
                if (!byName.TryGetValue(line.Section, out var section)) {
                    issues.Add(new SpecIssue(path, $"Unknown section '{line.Section}'."));
                    continue;
                }
                if (line.SameAs is { } source) {
                    if (!byName.TryGetValue(source, out var from)) {
                        issues.Add(new SpecIssue(path, $"sameAs: unknown section '{source}'."));
                        continue;
                    }
                    var copies = placed.Where(p => p.section == source).ToList();
                    if (copies.Count == 0) {
                        issues.Add(new SpecIssue(path, $"sameAs: this voice has no lines in '{source}' before this line."));
                        continue;
                    }
                    int shift = section.From - from.From;
                    foreach (var c in copies) {
                        placed.Add((l, section.Name, c.notes.Select(n => n with { Pos = n.Pos + shift }).ToList(), c.start + shift));
                    }
                    cursor[section.Name] = cursor.GetValueOrDefault(source, from.From) + shift;
                    continue;
                }
                var notes = PlaceLine(line, path, section, beatTicks, barTicks, cursor.GetValueOrDefault(section.Name, section.From),
                    issues, out int next, out int start);
                if (notes is null) {
                    continue;
                }
                cursor[section.Name] = next;
                foreach (var n in notes.Where(n => !scale.Contains(n.Tone))) {
                    warnings.Add($"{path}: {PitchName.Format(n.Tone)} ('{n.Lyric}') is outside {spec.Key}.");
                }
                placed.Add((l, section.Name, notes, start));
            }
            var all = placed.SelectMany(p => p.notes).OrderBy(n => n.Pos).ToList();
            for (int k = 1; k < all.Count; k++) {
                if (all[k].Pos < all[k - 1].Pos + all[k - 1].Dur) {
                    issues.Add(new SpecIssue($"voices[{v}]", $"Notes overlap at tick {all[k].Pos} ('{all[k - 1].Lyric}' and '{all[k].Lyric}')."));
                    break;
                }
            }
            lineVoices[voice.Name] = placed;
            voices.Add(new ComposedVoice {
                Name = voice.Name, Singer = voice.Singer, Kind = "lines", Notes = all,
                HumanizePreset = voice.Humanize?.Preset ?? "natural", HumanizeSeed = voice.Humanize?.Seed,
            });
        }
        for (int v = 0; v < spec.Voices.Count; v++) {
            var voice = spec.Voices[v];
            if (voice.Derive is not { } d || voice.Lines is { Count: > 0 }) {
                continue;
            }
            if (d.FromPart is not null) {
                // Derived from a part of the open project: the tool resolves it (target append).
                voices.Add(new ComposedVoice {
                    Name = voice.Name, Singer = voice.Singer, Kind = d.Kind, DerivedFrom = d.FromPart, Notes = [],
                    HumanizePreset = voice.Humanize?.Preset ?? "backing", HumanizeSeed = voice.Humanize?.Seed,
                });
                continue;
            }
            var source = voices.FirstOrDefault(x => x.Name == d.From && x.Kind == "lines");
            if (source is null) {
                issues.Add(new SpecIssue($"voices[{v}].derive.from", $"'{d.From}' is not a voice with lines in this spec."));
                continue;
            }
            try {
                var notes = Arranger.Derive(source.Notes, d, scale, sections);
                voices.Add(new ComposedVoice {
                    Name = voice.Name, Singer = voice.Singer ?? source.Singer, Kind = d.Kind, DerivedFrom = source.Name, Notes = notes,
                    HumanizePreset = voice.Humanize?.Preset ?? "backing", HumanizeSeed = voice.Humanize?.Seed,
                });
            } catch (ArgumentException e) {
                issues.Add(new SpecIssue($"voices[{v}].derive", e.Message));
            }
        }
        if (issues.Count > 0) {
            throw new SongSpecException(issues);
        }
        // Keep the spec's voice order (derived voices were composed after the voices they derive from).
        voices = voices.OrderBy(v => spec.Voices.ToList().FindIndex(s => s.Name == v.Name)).ToList();
        return new ComposedSong {
            Title = spec.Title, Bpm = spec.Bpm, BeatPerBar = beatPerBar, BeatUnit = beatUnit, Scale = scale, Key = spec.Key,
            Sections = sections, Voices = voices, EndTick = tick, Warnings = warnings,
        };
    }

    static List<ComposedNote>? PlaceLine(LineSpec line, string path, ComposedSection section, int beatTicks, int barTicks, int cursor,
                                         List<SpecIssue> issues, out int next, out int start) {
        next = cursor;
        start = cursor;
        if (line.Lyrics is null || line.Pitches is null || line.Rhythm is null) {
            issues.Add(new SpecIssue(path, "A line needs lyrics, pitches and rhythm (or sameAs)."));
            return null;
        }
        int count = issues.Count;
        if (line.At is { } at) {
            if (TryParseAt(at, beatTicks, barTicks, out int offset)) {
                start = section.From + offset;
            } else {
                issues.Add(new SpecIssue($"{path}.at", $"Cannot read '{at}'. Use \"bar:beat\" inside the section, e.g. \"1:1\" or \"3:2.5\"."));
            }
        }
        var syllables = Syllables(line.Lyrics);
        var pitches = new List<int>();
        foreach (var token in line.Pitches.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) {
            if (token == "~") {
                if (pitches.Count == 0) {
                    issues.Add(new SpecIssue($"{path}.pitches", "\"~\" needs a pitch before it."));
                    break;
                }
                pitches.Add(pitches[^1]);
            } else if (PitchName.TryParse(token, out int midi)) {
                pitches.Add(midi);
            } else {
                issues.Add(new SpecIssue($"{path}.pitches", $"Cannot read pitch '{token}'. Use names like \"A3\", \"C#4\", \"Bb4\" or MIDI numbers."));
                break;
            }
        }
        var rhythm = Rhythm.Parse(line.Rhythm, $"{path}.rhythm", issues);
        int rest = 0;
        if (line.RestAfter is { } r && !Rhythm.TryParseDuration(r, out rest)) {
            issues.Add(new SpecIssue($"{path}.restAfter", $"Cannot read duration '{r}'."));
        }
        if (issues.Count > count) {
            return null;
        }
        int notesInRhythm = rhythm.Count(x => !x.rest);
        if (syllables.Count != pitches.Count || syllables.Count != notesInRhythm) {
            issues.Add(new SpecIssue(path,
                $"'{line.Lyrics}': {syllables.Count} syllables, {pitches.Count} pitches, {notesInRhythm} note durations; they must match " +
                $"(syllables: {string.Join(" ", syllables)})."));
            return null;
        }
        var notes = new List<ComposedNote>();
        int t = start, k = 0;
        foreach (var (dur, isRest) in rhythm) {
            if (!isRest) {
                notes.Add(new ComposedNote(t, dur, pitches[k], syllables[k]));
                k++;
            }
            t += dur;
        }
        if (notes.Count > 0 && notes[^1].Pos + notes[^1].Dur > section.To) {
            issues.Add(new SpecIssue(path, $"The line ends {notes[^1].Pos + notes[^1].Dur - section.To} ticks after section '{section.Name}' ends."));
            return null;
        }
        if (start < section.From) {
            issues.Add(new SpecIssue(path, $"The line starts before section '{section.Name}'."));
            return null;
        }
        next = t + rest;
        return notes;
    }

    static bool TryParseAt(string at, int beatTicks, int barTicks, out int offset) {
        offset = 0;
        var parts = at.Split(':');
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int bar) || bar < 1) {
            return false;
        }
        double beat = 1;
        if (parts.Length == 2 && (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out beat) || beat < 1)) {
            return false;
        }
        offset = (bar - 1) * barTicks + (int)Math.Round((beat - 1) * beatTicks);
        return true;
    }

    [GeneratedRegex(@"\[[^\]]*\]|[^\s\[]+")]
    private static partial Regex TokenPattern();

    /// <summary>
    /// "Can-dles burn-ing" = Candles, +, burning, +. A lone "+" is a held note; "[f o n]" stays one syllable.
    /// Punctuation is kept as written, as the prototype did ("crack," stays "crack,").
    /// </summary>
    public static List<string> Syllables(string text) {
        var result = new List<string>();
        foreach (Match m in TokenPattern().Matches(text)) {
            string token = m.Value;
            if (token.StartsWith('[')) {
                result.Add(token);
                continue;
            }
            if (token.Trim('+').Length == 0) {
                result.AddRange(Enumerable.Repeat("+", token.Length));
                continue;
            }
            var pieces = token.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length == 0) {
                continue;   // a dash on its own ("word - word") is punctuation, not a syllable
            }
            result.Add(string.Concat(pieces));
            result.AddRange(Enumerable.Repeat("+", pieces.Length - 1));
        }
        return result;
    }
}

/// <summary>Rhythm strings: "1/8*8 3q r:1/8 1q." (fractions of a whole note, quarters "q", dotted ".", ticks; "*n" repeats).</summary>
public static partial class Rhythm {
    [GeneratedRegex(@"^(?<rest>r:)?(?<dur>[^*]+)(\*(?<n>\d+))?$")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^(?<num>\d+)/(?<den>\d+)(?<dot>\.?)$")]
    private static partial Regex FractionPattern();

    public static List<(int dur, bool rest)> Parse(string text, string path, List<SpecIssue> issues) {
        var list = new List<(int, bool)>();
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) {
            var m = TokenPattern().Match(token);
            if (!m.Success || !TryParseDuration(m.Groups["dur"].Value, out int dur)) {
                issues.Add(new SpecIssue(path, $"Cannot read rhythm token '{token}'. Use \"1/8\", \"1/4.\", \"3q\", \"r:1/8\", \"1/8*4\"."));
                return list;
            }
            int n = m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) : 1;
            for (int i = 0; i < n; i++) {
                list.Add((dur, m.Groups["rest"].Success));
            }
        }
        return list;
    }

    public static bool TryParseDuration(string text, out int ticks) {
        ticks = 0;
        string s = text.Trim();
        if (s.EndsWith('q') && double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double quarters) && quarters > 0) {
            ticks = (int)Math.Round(quarters * Composer.Ppq);
            return true;
        }
        var f = FractionPattern().Match(s);
        if (f.Success) {
            int num = int.Parse(f.Groups["num"].Value, CultureInfo.InvariantCulture);
            int den = int.Parse(f.Groups["den"].Value, CultureInfo.InvariantCulture);
            if (num < 1 || den < 1) {
                return false;
            }
            double whole = Composer.Ppq * 4.0 * num / den;
            ticks = (int)Math.Round(f.Groups["dot"].Value == "." ? whole * 1.5 : whole);
            return ticks > 0;
        }
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) && ticks > 0;
    }
}
