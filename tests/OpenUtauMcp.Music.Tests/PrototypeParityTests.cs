using System.Text.Json;
using OpenUtauMcp.Music.Humanize;
using OpenUtauMcp.Music.Randomness;

namespace OpenUtauMcp.Music.Tests;

/// <summary>
/// With CPython's random and the prototype's draw order, the C# humanizer must reproduce the
/// parts the Python prototype wrote into tests/fixtures/porcelain-crown/porcelain_crown.ustx: "Lead D" (seed 7,
/// natural), "Harmonia" (23, backing) and "Vocal Chops" (5, chops). Curves of the prototype are compared after
/// the two documented changes: the fall's "end + 1" point becomes "end + 5", then x is snapped to the 5-tick grid.
/// </summary>
public class PrototypeParityTests {
    const int Bar = 1920;

    // Porcelain Crown sections (as the Python prototype listed them), absolute bars from 0.
    static readonly (string name, string role, int bars)[] layout = [
        ("intro", Roles.Intro, 8), ("verse1", Roles.Verse, 8), ("pre1", Roles.Pre, 4), ("chorus1", Roles.Chorus, 8),
        ("drop1", Roles.Drop, 8), ("verse2", Roles.Verse, 8), ("pre2", Roles.Pre, 4), ("chorus2", Roles.Chorus, 8),
        ("bridge", Roles.Bridge, 8), ("chorus3", Roles.Chorus, 8), ("drop2", Roles.Drop, 8), ("outro", Roles.Outro, 4),
    ];

    static IReadOnlyList<Section> Sections() {
        var list = new List<Section>();
        int bar = 0;
        foreach (var (name, role, bars) in layout) {
            list.Add(new Section(name, role, bar * Bar, (bar + bars) * Bar));
            bar += bars;
        }
        return list;
    }

    static readonly Dictionary<string, int> leonaColors = new() {
        [ColorRoles.Normal] = 0, [ColorRoles.Power] = 1, [ColorRoles.Soft] = 2,
    };

    /// <summary>voice_parts of the prototype .ustx (top-level "key: json" lines).</summary>
    static List<JsonElement> VoiceParts() {
        foreach (var line in File.ReadLines(Path.Combine("golden", "porcelain_crown.ustx"))) {
            if (line.StartsWith("voice_parts: ", StringComparison.Ordinal)) {
                return JsonDocument.Parse(line["voice_parts: ".Length..]).RootElement.EnumerateArray().ToList();
            }
        }
        throw new InvalidOperationException("voice_parts not found");
    }

    static List<HNote> InputNotes(JsonElement part) =>
        part.GetProperty("notes").EnumerateArray()
            .Where(n => n.GetProperty("lyric").GetString() != "AP")
            .Select((n, i) => new HNote($"x{i}", n.GetProperty("position").GetInt32(), n.GetProperty("duration").GetInt32(),
                n.GetProperty("tone").GetInt32(), n.GetProperty("lyric").GetString()!))
            .ToList();

    [Theory]
    [InlineData(1, 0, 7u, "natural")]   // Lead D, notes taken from Lead C (same melody, not humanized)
    [InlineData(3, 3, 23u, "backing")]  // Harmonia
    [InlineData(4, 4, 5u, "chops")]     // Vocal Chops
    public void Humanizer_ReproducesPrototype(int expectedIndex, int inputIndex, uint seed, string preset) {
        var parts = VoiceParts();
        var expected = parts[expectedIndex];
        var input = new HumanizeInput {
            Notes = InputNotes(parts[inputIndex]), PartPos = 0, Bpm = 124, Sections = Sections(), Colors = leonaColors,
        };

        var result = Humanizer.Plan(input, HumanizeParams.FromPreset(preset), new SharedRandom(new PythonRandom(seed)), legacyTail: true);

        var sung = expected.GetProperty("notes").EnumerateArray().Where(n => n.GetProperty("lyric").GetString() != "AP").ToList();
        Assert.Equal(sung.Count, result.Notes.Count);
        for (int i = 0; i < sung.Count; i++) {
            AssertNote(sung[i], result.Notes[i], i);
        }

        var breaths = expected.GetProperty("notes").EnumerateArray().Where(n => n.GetProperty("lyric").GetString() == "AP")
            .Select(n => (n.GetProperty("position").GetInt32(), n.GetProperty("duration").GetInt32(), n.GetProperty("tone").GetInt32()))
            .ToList();
        Assert.Equal(breaths, result.BreathsToAdd.Select(b => (b.Pos, b.Dur, b.Tone)).ToList());

        foreach (var curve in expected.GetProperty("curves").EnumerateArray()) {
            string abbr = curve.GetProperty("abbr").GetString()!;
            var raw = curve.GetProperty("xs").EnumerateArray().Select(x => x.GetInt32())
                .Zip(curve.GetProperty("ys").EnumerateArray().Select(y => y.GetInt32()))
                .ToDictionary(p => p.First, p => (double)p.Second);
            if (abbr == "pitd") {
                MoveFallEnds(raw);
            }
            var want = Humanizer.Quantize(raw);
            var got = result.Curves[abbr];
            var diffs = want.Keys.Union(got.Keys).Order()
                .Where(x => !want.TryGetValue(x, out int w) || !got.TryGetValue(x, out int g) || w != g)
                .Select(x => $"{x}: want {(want.TryGetValue(x, out int w) ? w : "-")} got {(got.TryGetValue(x, out int g) ? g : "-")}")
                .ToList();
            Assert.True(diffs.Count == 0, $"{abbr}: {diffs.Count} differences, first: {string.Join("; ", diffs.Take(5))}");
        }
    }

    static void AssertNote(JsonElement want, NotePlan got, int i) {
        var points = want.GetProperty("pitch").GetProperty("data").EnumerateArray()
            .Select(p => (x: p.GetProperty("x").GetDouble(), y: p.GetProperty("y").GetDouble())).ToList();
        Assert.True(got.Pitch is not null, $"note {i}: no pitch planned");
        Assert.Equal(points.Count, got.Pitch!.Count);
        for (int k = 0; k < points.Count; k++) {
            Assert.True(points[k].x == got.Pitch[k].X && Math.Abs(points[k].y - got.Pitch[k].Y) < 1e-9,
                $"note {i} point {k}: want ({points[k].x}, {points[k].y}) got ({got.Pitch[k].X}, {got.Pitch[k].Y})");
        }

        var vib = want.GetProperty("vibrato");
        double length = vib.GetProperty("length").GetDouble();
        Assert.NotNull(got.Vibrato);
        Assert.Equal(length, got.Vibrato!.Length);
        if (length > 0) {
            Assert.Equal((vib.GetProperty("period").GetDouble(), vib.GetProperty("depth").GetDouble(), vib.GetProperty("in").GetDouble(),
                          vib.GetProperty("out").GetDouble(), vib.GetProperty("shift").GetDouble()),
                (got.Vibrato.Period, got.Vibrato.Depth, got.Vibrato.In, got.Vibrato.Out, got.Vibrato.Shift));
        }

        int color = want.GetProperty("phoneme_expressions").EnumerateArray()
            .Where(e => e.GetProperty("abbr").GetString() == "clr").Select(e => (int)e.GetProperty("value").GetDouble())
            .FirstOrDefault();
        Assert.True(color == (got.Color ?? 0), $"note {i}: clr want {color} got {got.Color}");
    }

    /// <summary>The prototype wrote the point after a fall at end + 1; the product writes it at end + 5.</summary>
    static void MoveFallEnds(Dictionary<int, double> raw) {
        foreach (int x in raw.Keys.Where(x => x % 5 == 1 && raw[x] == 0 && raw.ContainsKey(x - 1)).ToList()) {
            raw.Remove(x);
            raw[x + 4] = 0;
        }
    }
}
