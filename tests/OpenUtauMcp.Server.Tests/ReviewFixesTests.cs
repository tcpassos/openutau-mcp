using System.Text.Json;
using ModelContextProtocol.Protocol;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Contracts.Protocol;
using OpenUtauMcp.Music.Song;
using OpenUtauMcp.Server.Safety;
using OpenUtauMcp.Server.Tools.Audio;
using OpenUtauMcp.Server.Tools.Core;
using OpenUtauMcp.Server.Tools.Music;

namespace OpenUtauMcp.Server.Tests;

/// <summary>Regression tests for the findings of the F1b-F3 code review.</summary>
public class ReviewFixesTests {
    static string Text(CallToolResult r) => ((TextContentBlock)r.Content[0]).Text;

    static readonly string TempDir = Path.Combine(Path.GetTempPath(), "openutau-mcp-tests");

    static readonly EditApplyResult Applied = new() {
        Epoch = 1, Applied = 1, DryRun = false, PartRevs = new Dictionary<string, int>(), StructureRev = 2,
    };

    static FakeBridge Project(IReadOnlyList<TrackDto> tracks, IReadOnlyList<PartDto> parts) => new FakeBridge()
        .On(Methods.ProjectGet, _ => new ProjectDto {
            Epoch = 1, StructureRev = 1, Name = "T", Resolution = 480, FilePath = Path.Combine(TempDir, "t.ustx"),
            Tempos = [new TempoDto(0, 120)], TimeSignatures = [new TimeSignatureDto(0, 4, 4)], Tracks = tracks, Parts = parts,
        }, ContractsJson.Context.ProjectDto)
        .On(Methods.Status, _ => new StatusResult {
            Project = new StatusProject { Name = "T", ChangesSaved = true, Epoch = 1, StructureRev = 1, Tracks = tracks.Count, Parts = parts.Count },
            Playback = new StatusPlayback("stopped", 0),
            History = new HistoryState { CanUndo = false, CanRedo = false, ChangesSaved = true },
        }, ContractsJson.Context.StatusResult)
        .On(Methods.EditApply, _ => Applied, ContractsJson.Context.EditApplyResult);

    static List<EditOp> Ops(FakeBridge bridge) =>
        bridge.Calls.Last(c => c.method == Methods.EditApply).@params.Deserialize(ContractsJson.Context.EditApplyParams)!.Ops.ToList();

    static PartDto Part(string id, string track, int pos, string name = "Lead") =>
        new() { Id = id, Name = name, TrackId = track, Type = "voice", Pos = pos, Dur = 1920 };

    [Fact]
    public async Task Promote_BaseTrackWithOtherParts_SwapsOnlyThatPart() {
        var bridge = Project(
            [new TrackDto { Id = "t1", Index = 0, Name = "Lead" }, new TrackDto { Id = "t2", Index = 1, Name = "Lead [B]", Mute = true },
             new TrackDto { Id = "t3", Index = 2, Name = "Lead [C]", Mute = true }],
            [Part("p1", "t1", 0, "Verse"), Part("p9", "t1", 7680, "Chorus"), Part("p2", "t2", 0), Part("p3", "t3", 0)]);

        var result = await VariationsTool.Run(bridge, "promote", "p1", variant: "B", confirm: true);

        Assert.False(result.IsError, Text(result));
        var ops = Ops(bridge);
        Assert.DoesNotContain(ops, op => op is TrackRemoveOp { TrackId: "t1" });
        Assert.Contains(ops, op => op is PartRemoveOp { PartId: "p1" });
        var move = Assert.Single(ops.OfType<PartUpdateOp>());
        Assert.Equal(("p2", "t1", "Verse"), (move.PartId, move.TrackId, move.Name));
        Assert.Equal(["t2", "t3"], ops.OfType<TrackRemoveOp>().Select(r => r.TrackId));
        Assert.Contains(ops, op => op is TrackMixOp { TrackId: "t1", Mute: false });
    }

    [Fact]
    public async Task Promote_BaseTrackWithOnlyThatPart_ReplacesTheTrack() {
        var bridge = Project(
            [new TrackDto { Id = "t1", Index = 0, Name = "Lead" }, new TrackDto { Id = "t2", Index = 1, Name = "Lead [B]", Mute = true }],
            [Part("p1", "t1", 0), Part("p2", "t2", 0)]);

        var result = await VariationsTool.Run(bridge, "promote", "p1", variant: "B", confirm: true);

        Assert.False(result.IsError, Text(result));
        var ops = Ops(bridge);
        Assert.Equal(["t1"], ops.OfType<TrackRemoveOp>().Select(r => r.TrackId));
        Assert.Contains(ops, op => op is TrackUpdateOp { TrackId: "t2", Name: "Lead" });
    }

    [Fact]
    public async Task Activate_BaseTrackWithOtherParts_Warns() {
        var bridge = Project(
            [new TrackDto { Id = "t1", Index = 0, Name = "Lead" }, new TrackDto { Id = "t2", Index = 1, Name = "Lead [B]", Mute = true }],
            [Part("p1", "t1", 0), Part("p9", "t1", 7680, "Chorus"), Part("p2", "t2", 0)]);

        var result = await VariationsTool.Run(bridge, "activate", "p1", variant: "B");

        Assert.Contains("TRACK_MUTED", Text(result));
    }

    [Fact]
    public async Task Render_MutedTrackWithFaders_FailsBeforeStarting() {
        var bridge = Project([new TrackDto { Id = "t1", Index = 0, Name = "Lead", Mute = true }], [Part("p1", "t1", 0)]);

        var result = await RenderTool.Run(bridge, new PathPolicy([TempDir]), "export", path: Path.Combine(TempDir, "m.wav"), overwrite: true,
            tracks: ["Lead"]);

        Assert.True(result.IsError);
        Assert.Contains("TRACK_MUTED", Text(result));
        Assert.DoesNotContain(bridge.Calls, c => c.method == Methods.JobsStart);
    }

    [Fact]
    public async Task PartsUpdate_ResizeFromStartPastBarOne_IsRejected() {
        var bridge = Project([new TrackDto { Id = "t1", Index = 0, Name = "Lead" }], [Part("p1", "t1", 1920)]);

        var result = await PartsEditTool.Run(bridge, new PathPolicy([]),
            [new PartOpInput("update", Part: "p1", Length: "12q", ResizeFromStart: true)]);

        Assert.True(result.IsError);
        Assert.Contains("at most 8q", Text(result));
        Assert.DoesNotContain(bridge.Calls, c => c.method == Methods.EditApply);
    }

    [Fact]
    public async Task AddAudio_NameWithExistingTrack_IsRejected() {
        Directory.CreateDirectory(TempDir);
        string wav = Path.Combine(TempDir, "a.wav");
        File.WriteAllBytes(wav, [0]);
        var bridge = Project([new TrackDto { Id = "t1", Index = 0, Name = "Lead" }], [Part("p1", "t1", 0)]);

        var result = await PartsEditTool.Run(bridge, new PathPolicy([TempDir]),
            [new PartOpInput("add_audio", File: wav, Track: "Lead", Name: "Guide")]);

        Assert.True(result.IsError);
        Assert.Contains("'name' names the new track", Text(result));
    }

    [Theory]
    [InlineData("word - word")]
    [InlineData("word -- word")]
    public void Syllables_LoneDashIsPunctuation(string text) {
        Assert.Equal(["word", "word"], LyricsSetTool.Syllables(text));
        Assert.Equal(["word", "word"], Composer.Syllables(text));
    }
}
