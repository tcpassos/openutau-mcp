using OpenUtauMcp.Music.Humanize;
using OpenUtauMcp.Music.Randomness;

namespace OpenUtauMcp.Music.Tests;

/// <summary>Product mode (PCG32 sub-streams): determinism, selection = crop, layer independence.</summary>
public class HumanizerProductTests {
    // Two phrases separated by a rest: [n0 n1 n2] rest [n3 n4 n5 n6].
    static readonly HNote[] notes = [
        new("n0", 1920, 480, 62, "Can"), new("n1", 2400, 480, 64, "+"), new("n2", 2880, 960, 69, "glass"),
        new("n3", 4800, 240, 65, "in"), new("n4", 5040, 240, 67, "the"), new("n5", 5280, 960, 72, "hall"),
        new("n6", 6240, 1440, 62, "night"),
    ];

    static HumanizeInput Input(IReadOnlySet<string>? selection = null) => new() {
        Notes = notes, Bpm = 124, Selection = selection,
        Sections = [new("verse1", Roles.Verse, 0, 4800), new("chorus1", Roles.Chorus, 4800, 20000)],
        Colors = new Dictionary<string, int> { [ColorRoles.Normal] = 0, [ColorRoles.Power] = 1, [ColorRoles.Soft] = 2 },
    };

    static HumanizeResult Run(HumanizeInput input, HumanizeParams? p = null, long seed = 42) =>
        Humanizer.Plan(input, p ?? new HumanizeParams(), new SubstreamRandom(seed));

    [Fact]
    public void SameSeed_SamePlan_DifferentSeed_DifferentPlan() {
        var a = Run(Input());
        var b = Run(Input());
        var c = Run(Input(), seed: 43);

        Assert.Equal(Describe(a), Describe(b));
        Assert.NotEqual(Describe(a), Describe(c));
    }

    [Fact]
    public void WholePhraseSelection_EqualsCropOfWholePart() {
        var whole = Run(Input());
        var part = Run(Input(new HashSet<string> { "n3", "n4", "n5", "n6" }));

        Assert.Empty(part.Warnings);
        foreach (var plan in part.Notes) {
            Assert.Equal(Describe(whole.Notes.Single(n => n.Id == plan.Id)), Describe(plan));
        }
        foreach (var (abbr, points) in part.Curves) {
            var expected = whole.Curves[abbr].Where(kv => kv.Key >= part.RangeFrom && kv.Key <= part.RangeTo);
            Assert.Equal(expected, points);
        }
        Assert.Equal(whole.BreathsToAdd.Where(b => b.Pos >= 4000), part.BreathsToAdd);
    }

    [Fact]
    public void PartialPhrase_Warns() {
        var result = Run(Input(new HashSet<string> { "n4" }));

        Assert.Contains(result.Warnings, w => w.StartsWith("PARTIAL_PHRASE"));
        Assert.Equal(["n4"], result.Notes.Select(n => n.Id));
    }

    [Fact]
    public void TurningALayerOff_DoesNotChangeTheOthers() {
        var all = Run(Input());
        var noVibrato = Run(Input(), new HumanizeParams().With(new() { ["vibrato"] = false }, null));

        Assert.All(noVibrato.Notes, n => Assert.Null(n.Vibrato));
        Assert.Equal(all.Notes.Select(n => Describe(n with { Vibrato = null })), noVibrato.Notes.Select(Describe));
        Assert.Equal(all.Curves["pitd"], noVibrato.Curves["pitd"]);
    }

    [Fact]
    public void PreservePitch_SkipsEveryPitchLayer() {
        var result = Run(Input(), new HumanizeParams().With(null, ["pitch"]));

        Assert.All(result.Notes, n => Assert.Null(n.Pitch));
        Assert.All(result.Notes, n => Assert.Null(n.Vibrato));
        Assert.False(result.Curves.ContainsKey("pitd"));
        Assert.True(result.Curves.ContainsKey("dyn"));
    }

    [Fact]
    public void Properties_GridBreathsColorsAndScoops() {
        var result = Run(Input());

        Assert.All(result.Curves.Values.SelectMany(c => c.Keys), x => Assert.Equal(0, x % Humanizer.Grid));
        // Rests of 4 and 2 beats before the phrases -> breaths of the maximum 0.75 beat (360 ticks), glued to each phrase.
        Assert.Equal([new BreathNote(1920 - 360, 360, 62, "AP"), new BreathNote(4800 - 360, 360, 65, "AP")], result.BreathsToAdd);
        // Verse = normal (0), chorus = power (1).
        Assert.Equal(0, result.Notes.Single(n => n.Id == "n0").Color);
        Assert.Equal(1, result.Notes.Single(n => n.Id == "n5").Color);
        // Scoop only on phrase starts (first point below the note at x = 0).
        Assert.True(result.Notes.Single(n => n.Id == "n3").Pitch![0] is { X: 0, Y: < 0 });
        Assert.True(result.Notes.Single(n => n.Id == "n4").Pitch![0].X < 0);
    }

    [Fact]
    public void Overrides_UnknownLayerOrParameter_Throw() {
        Assert.Throws<ArgumentException>(() => new HumanizeParams().With(new() { ["wobble"] = false }, null));
        Assert.Throws<ArgumentException>(() => new HumanizeParams().With(new() { ["vibrato"] = new System.Text.Json.Nodes.JsonObject { ["speed"] = 3 } }, null));
        var tuned = new HumanizeParams().With(new() { ["vibrato"] = new System.Text.Json.Nodes.JsonObject { ["maxLength"] = 50 } }, null);
        Assert.Equal(50, tuned.Vibrato.MaxLength);
    }

    static string Describe(NotePlan n) =>
        $"{n.Id}|{string.Join(",", n.Pitch?.Select(p => $"{p.X}:{p.Y}") ?? [])}|{n.Vibrato}|{n.Color}";

    static string Describe(HumanizeResult r) =>
        string.Join("\n", r.Notes.Select(Describe)) + "\n" +
        string.Join("\n", r.Curves.Select(c => $"{c.Key}:{string.Join(",", c.Value.Select(kv => $"{kv.Key}={kv.Value}"))}"));
}
