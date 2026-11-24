using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Server.Units;

namespace OpenUtauMcp.Server.Tests;

public class MusicalUnitsTests {
    static readonly TimeMap FourFour = TimeMap.Default;

    [Theory]
    [InlineData("1:1", 0)]
    [InlineData("17:1", 30720)]
    [InlineData("17:2.5", 31440)]
    [InlineData("17:3+60", 31740)]
    [InlineData("17", 17)]  // a bare integer is an absolute tick (bars are written "17:1")
    [InlineData("30720", 30720)]
    public void ParsePosition_FourFour_ReturnsAbsoluteTick(string text, int expected) =>
        Assert.Equal(expected, FourFour.ParsePosition(text));

    [Theory]
    [InlineData(0, "1:1")]
    [InlineData(31440, "17:2.5")]
    [InlineData(31500, "17:2.625")]
    [InlineData(31443, "17:2+243")]
    public void FormatPosition_FourFour_IsReadable(int tick, string expected) =>
        Assert.Equal(expected, FourFour.FormatPosition(tick));

    [Fact]
    public void Position_RoundTrip_WithTimeSignatureChange() {
        // 4/4 for bars 1-2, then 3/4 from bar 3 (0-based bar 2).
        var map = new TimeMap([new TimeSignatureDto(0, 4, 4), new TimeSignatureDto(2, 3, 4)]);

        Assert.Equal(3840, map.ParsePosition("3:1"));
        Assert.Equal(3840 + 1440, map.ParsePosition("4:1"));
        Assert.Equal("4:2", map.FormatPosition(3840 + 1440 + 480));
        Assert.Throws<UnitException>(() => map.ParsePosition("4:4"));
    }

    [Theory]
    [InlineData("240", 240)]
    [InlineData("0.5q", 240)]
    [InlineData("1/8", 240)]
    [InlineData("1/4.", 720)]
    [InlineData("3q", 1440)]
    public void ParseDuration_AcceptsAllForms(string text, int expected) =>
        Assert.Equal(expected, Durations.Parse(text));

    [Theory]
    [InlineData(240, "0.5q")]
    [InlineData(1440, "3q")]
    [InlineData(250, "250")]
    public void FormatDuration_UsesQuarters(int ticks, string expected) =>
        Assert.Equal(expected, Durations.Format(ticks));

    [Theory]
    [InlineData("A4", 69)]
    [InlineData("C4", 60)]
    [InlineData("C#5", 73)]
    [InlineData("Bb3", 58)]
    [InlineData("62", 62)]
    public void ParsePitch_NamesAndMidi(string text, int expected) =>
        Assert.Equal(expected, Pitches.Parse(text));

    [Fact]
    public void FormatPitch_UsesSharps() => Assert.Equal("C#4", Pitches.Format(61));

    [Theory]
    [InlineData("H4")]
    [InlineData("17:0")]
    public void BadInput_Throws(string text) =>
        Assert.Throws<UnitException>(() => {
            Pitches.Parse(text);
            FourFour.ParsePosition(text);
        });
}
