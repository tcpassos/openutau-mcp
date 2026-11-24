using System.Text.Json;
using OpenUtauMcp.Music.Song;
using OpenUtauMcp.Music.Theory;

namespace OpenUtauMcp.Music.Tests;

public class ComposerTests {
    static SongSpec PorcelainCrown() => SongSpec.Parse(File.ReadAllText(Path.Combine("golden", "porcelain_crown.song.json")));

    static List<(string lyric, int tone, int pos, int dur)> Golden(string voice) {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine("golden", "porcelain-crown-notes.json")));
        return doc.RootElement.GetProperty(voice).EnumerateArray()
            .Select(n => (n[0].GetString()!, n[1].GetInt32(), n[2].GetInt32(), n[3].GetInt32())).ToList();
    }

    /// <summary>The SongSpec reproduces the prototype's notes exactly (position, duration, pitch, lyric).</summary>
    [Theory]
    [InlineData("Lead", "Lead")]
    [InlineData("Harmony", "Harmony")]
    [InlineData("Chops", "Chops")]
    public void PorcelainCrown_MatchesPrototype(string voice, string golden) {
        var song = Composer.Compose(PorcelainCrown());

        var expected = Golden(golden);
        var actual = song.Voices.Single(v => v.Name == voice).Notes.Select(n => (n.Lyric, n.Tone, n.Pos, n.Dur)).ToList();
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++) {
            Assert.True(expected[i] == actual[i], $"{voice} note {i}: expected {expected[i]}, got {actual[i]}");
        }
    }

    [Fact]
    public void PorcelainCrown_SectionsAndSongLength() {
        var song = Composer.Compose(PorcelainCrown());

        Assert.Equal(12, song.Sections.Count);
        Assert.Equal(("chorus1", 20 * 1920, 28 * 1920), (song.Sections[3].Name, song.Sections[3].From, song.Sections[3].To));
        Assert.Equal(84 * 1920, song.EndTick);
        Assert.Contains(song.Warnings, w => w.Contains("C#4") && w.Contains("outside Dm"));
        Assert.Equal(("backing", 23), (song.Voices.Single(v => v.Name == "Harmony").HumanizePreset, song.Voices.Single(v => v.Name == "Harmony").HumanizeSeed));
    }

    /// <summary>A wrong syllable count points at the line and gives the counts.</summary>
    [Fact]
    public void WrongCount_PointsAtTheLine() {
        var spec = SongSpec.Parse("""
            {"sections":[{"name":"verse1","role":"verse","bars":2}],
             "voices":[{"name":"Lead","lines":[
               {"section":"verse1","lyrics":"Can-dles burn","pitches":"A3 A3 D4","rhythm":"1q*3"},
               {"section":"verse1","lyrics":"in the hall","pitches":"A3 B3","rhythm":"1q*3"}]}]}
            """);

        var e = Assert.Throws<SongSpecException>(() => Composer.Compose(spec));

        var issue = Assert.Single(e.Issues);
        Assert.Equal("voices[0].lines[1]", issue.Path);
        Assert.Contains("3 syllables, 2 pitches, 3 note durations", issue.Message);
    }

    [Fact]
    public void LineLongerThanSection_IsAnError() {
        var spec = SongSpec.Parse("""
            {"sections":[{"name":"a","bars":1}],"voices":[{"name":"V","lines":[{"section":"a","lyrics":"la la","pitches":"C4 D4","rhythm":"3q 2q"}]}]}
            """);

        var e = Assert.Throws<SongSpecException>(() => Composer.Compose(spec));

        Assert.Contains("ends 480 ticks after section 'a' ends", e.Issues[0].Message);
    }

    [Fact]
    public void AtAndTildeAndRests() {
        var spec = SongSpec.Parse("""
            {"timeSig":"3/4","sections":[{"name":"a","bars":3}],"voices":[{"name":"V","lines":[
              {"section":"a","at":"2:2","lyrics":"oh + yeah","pitches":"C4 ~ E4","rhythm":"1/8 r:1/8 1/8 1q"}]}]}
            """);

        var notes = Composer.Compose(spec).Voices[0].Notes;

        Assert.Equal([(1920 * 3 / 4 + 480, 240, 60, "oh"), (1440 + 480 + 480, 240, 60, "+"), (2640, 480, 64, "yeah")],
            notes.Select(n => (n.Pos, n.Dur, n.Tone, n.Lyric)));
    }

    [Theory]
    [InlineData("Dm", 69, -2, 65)]    // A4 -> F4
    [InlineData("Dm", 74, -2, 70)]    // D5 -> Bb4
    [InlineData("Dm", 72, -2, 69)]    // C5 -> A4
    [InlineData("Dm", 61, -2, 57)]    // C#4 counts as C4 -> A3
    [InlineData("C", 60, 2, 64)]      // C4 -> E4
    [InlineData("C", 67, 4, 74)]      // G4 -> D5 (fifth above)
    public void Scale_StepsDiatonically(string key, int pitch, int degrees, int expected) =>
        Assert.Equal(expected, Scale.Parse(key).Step(pitch, degrees));

    [Theory]
    [InlineData("A3", 57)]
    [InlineData("C#4", 61)]
    [InlineData("Bb4", 70)]
    [InlineData("60", 60)]
    public void PitchName_Parses(string text, int midi) {
        Assert.True(PitchName.TryParse(text, out int value));
        Assert.Equal(midi, value);
    }
}
