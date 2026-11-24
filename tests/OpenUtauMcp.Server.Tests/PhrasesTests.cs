using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Server.Tools.Core;

namespace OpenUtauMcp.Server.Tests;

public class PhrasesTests {
    static NoteDto N(string id, int pos, int dur, string lyric) => new() { Id = id, Pos = pos, Dur = dur, Tone = 60, Lyric = lyric };

    [Fact]
    public void Detect_SplitsOnGapsAndBreaths_JoinsWordsSkippingContinuations() {
        var notes = new[] {
            N("n1", 0, 240, "AP"),
            N("n2", 240, 240, "Candles"), N("n3", 480, 240, "+"), N("n4", 720, 480, "burn"),
            N("n5", 1440, 480, "glass"),                 // gap before: new phrase
            N("n6", 1920, 240, "AP"), N("n7", 2160, 240, "Now"),  // breath splits even without gap
        };

        var (phrases, phraseOf) = Phrases.Detect(notes);

        Assert.Equal(3, phrases.Count);
        Assert.Equal("Candles burn", phrases[0].Text);
        Assert.Equal("glass", phrases[1].Text);
        Assert.Equal(3, phraseOf["n7"]);
        Assert.False(phraseOf.ContainsKey("n1"));
    }
}
