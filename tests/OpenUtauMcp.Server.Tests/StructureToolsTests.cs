using System.Text.Json;
using ModelContextProtocol.Protocol;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Contracts.Protocol;
using OpenUtauMcp.Server.Safety;
using OpenUtauMcp.Server.Tools;
using OpenUtauMcp.Server.Tools.Core;
using OpenUtauMcp.Server.Tools.Music;

namespace OpenUtauMcp.Server.Tests;

public class StructureToolsTests {
    static string Text(CallToolResult r) => ((TextContentBlock)r.Content[0]).Text;

    static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static FakeBridge Bridge(string? comment = null, int noteCount = 2) => new FakeBridge()
        .On(Methods.ProjectGet, _ => new ProjectDto {
            Epoch = 1, StructureRev = 1, Name = "Test", Resolution = 480, Comment = comment,
            Tempos = [new TempoDto(0, 120), new TempoDto(15360, 100)],
            TimeSignatures = [new TimeSignatureDto(0, 4, 4)],
            Tracks = [new TrackDto { Id = "t1", Index = 0, Name = "Vocal", VolumeDb = -2 }, new TrackDto { Id = "t2", Index = 1, Name = "Harmony" }],
            Parts = [new PartDto { Id = "p1", Name = "Lead", TrackId = "t1", Type = "voice", Pos = 15360, Dur = 7680, NoteCount = noteCount }],
        }, ContractsJson.Context.ProjectDto)
        .On(Methods.EditApply, p => new EditApplyResult {
            Epoch = 1, Applied = 1, DryRun = false, PartRevs = new Dictionary<string, int>(), StructureRev = 2,
            Created = new Dictionary<string, string> { ["@t0"] = "t9", ["@p0"] = "p9" },
        }, ContractsJson.Context.EditApplyResult)
        .On(Methods.NotesQuery, _ => new NotesQueryResult {
            PartId = "p1", PartPos = 15360, PartRev = 4, Epoch = 1, Total = 5,
            Notes = [
                new NoteDto { Id = "n1", Pos = 0, Dur = 480, Tone = 60, Lyric = "a" },
                new NoteDto { Id = "n2", Pos = 480, Dur = 480, Tone = 62, Lyric = "a" },
                new NoteDto { Id = "n3", Pos = 960, Dur = 480, Tone = 62, Lyric = "AP" },
                new NoteDto { Id = "n4", Pos = 1440, Dur = 480, Tone = 64, Lyric = "a" },
                new NoteDto { Id = "n5", Pos = 1920, Dur = 960, Tone = 64, Lyric = "a" },
            ],
        }, ContractsJson.Context.NotesQueryResult);

    static EditApplyParams Applied(FakeBridge bridge) =>
        bridge.Calls.Single(c => c.method == Methods.EditApply).@params.Deserialize(ContractsJson.Context.EditApplyParams)!;

    [Fact]
    public async Task TracksAdd_WithPhonemizerAndMix_ChainsOpsThroughRef() {
        var bridge = Bridge();

        var result = await TracksEditTool.Run(bridge, [
            new TrackOpInput("add", Name: "Lead", Singer: "LEONA", Phonemizer: "DiffSingerEnglishPhonemizer", VolumeDb: -3),
        ]);

        Assert.False(result.IsError, Text(result));
        var ops = Applied(bridge).Ops;
        Assert.Equal("@t0", Assert.IsType<TrackAddOp>(ops[0]).Ref);
        Assert.Equal("@t0", Assert.IsType<TrackUpdateOp>(ops[1]).TrackId);
        Assert.Equal(-3, Assert.IsType<TrackMixOp>(ops[2]).VolumeDb);
        Assert.Contains("\"created\":[\"t9\"]", Text(result));
    }

    [Fact]
    public async Task TracksUpdate_ByName_PositionIsOneBased() {
        var bridge = Bridge();

        await TracksEditTool.Run(bridge, [new TrackOpInput("update", Track: "harmony", Position: 1, Color: "Blue")]);

        var op = Assert.IsType<TrackUpdateOp>(Assert.Single(Applied(bridge).Ops));
        Assert.Equal("t2", op.TrackId);
        Assert.Equal(0, op.Position);
    }

    [Fact]
    public async Task TracksMix_ReportsPreviousValues() {
        var bridge = Bridge();

        var result = await TracksEditTool.Run(bridge, [new TrackOpInput("mix", Track: "#1", VolumeDb: 0)]);

        Assert.Contains("\"mixBefore\":{\"t1\":{\"volumeDb\":-2", Text(result));
    }

    [Fact]
    public async Task TracksRemove_WithNotes_NeedsExactNameConfirm() {
        var bridge = Bridge();

        var refused = await TracksEditTool.Run(bridge, [new TrackOpInput("remove", Track: "t1")]);
        Assert.True(refused.IsError);
        Assert.StartsWith("CONFIRMATION_REQUIRED", Text(refused));
        Assert.DoesNotContain(bridge.Calls, c => c.method == Methods.EditApply);

        var ok = await TracksEditTool.Run(bridge, [new TrackOpInput("remove", Track: "t1", Confirm: "Vocal")]);
        Assert.False(ok.IsError);
        Assert.Equal("t1", Assert.IsType<TrackRemoveOp>(Assert.Single(Applied(bridge).Ops)).TrackId);
    }

    [Fact]
    public async Task TracksEdit_FieldNotUsedByOp_IsRejected() {
        var result = await TracksEditTool.Run(Bridge(), [new TrackOpInput("mix", Track: "t1", Singer: "LEONA")]);

        Assert.True(result.IsError);
        Assert.Contains("'singer' is not used by op 'mix'", Text(result));
    }

    [Fact]
    public async Task PartsAdd_FromTo_BecomesAbsolutePosAndDuration() {
        var bridge = Bridge();

        var result = await PartsEditTool.Run(bridge, new PathPolicy([]), [new PartOpInput("add", Track: "Vocal", From: "1:1", To: "9:1", Name: "Verse")]);

        Assert.False(result.IsError, Text(result));
        var op = Assert.IsType<PartAddOp>(Assert.Single(Applied(bridge).Ops));
        Assert.Equal(("t1", 0, 15360, "@p0"), (op.TrackId, op.Pos, op.Dur, op.Ref));
    }

    [Fact]
    public async Task PartsUpdate_ResizeFromStart_KeepsEnd() {
        var bridge = Bridge();

        await PartsEditTool.Run(bridge, new PathPolicy([]), [new PartOpInput("update", Part: "Lead", From: "8:1", ResizeFromStart: true)]);

        var op = Assert.IsType<PartUpdateOp>(Assert.Single(Applied(bridge).Ops));
        Assert.Null(op.Pos);
        Assert.Equal(7680 + 1920, op.Dur);   // one bar earlier, same end
        Assert.True(op.ResizeFromStart);
    }

    [Fact]
    public async Task PartsDuplicate_WithoutTarget_CreatesTrackFirst() {
        var bridge = Bridge();

        await PartsEditTool.Run(bridge, new PathPolicy([]), [new PartOpInput("duplicate", Part: "p1", From: "17:1")]);

        var ops = Applied(bridge).Ops;
        Assert.Equal("t1", Assert.IsType<TrackAddOp>(ops[0]).CopyFrom);
        var dup = Assert.IsType<PartDuplicateOp>(ops[1]);
        Assert.Equal(("@t0", 30720), (dup.TrackId, dup.Pos));
    }

    [Fact]
    public async Task PartsRemove_LargePart_NeedsConfirm() {
        var result = await PartsEditTool.Run(Bridge(noteCount: 80), new PathPolicy([]), [new PartOpInput("remove", Part: "p1")]);

        Assert.True(result.IsError);
        Assert.StartsWith("CONFIRMATION_REQUIRED", Text(result));
    }

    [Fact]
    public async Task ProjectSet_ConvertsUnitsAndWritesSections() {
        var bridge = Bridge(comment: "my notes");

        var result = await ProjectSetTool.Run(bridge, bpm: 128,
            tempoChanges: new TempoChangesInput([new TempoAtInput("17:1", 140)], ["9:1"]),
            timeSig: Json("{\"add\":[{\"bar\":33,\"sig\":\"3/4\"}]}"),
            key: "Dm",
            expressions: new ExpressionsEnsureInput(["velc"]),
            sections: [new SectionInput("verse1", "verse", Bars: "9-16")]);

        Assert.False(result.IsError, Text(result));
        var ops = Applied(bridge).Ops;
        var u = Assert.IsType<ProjectUpdateOp>(ops[0]);
        Assert.Equal(128, u.Bpm);
        Assert.Equal(new TempoDto(30720, 140), Assert.Single(u.TempoAdd!));
        Assert.Equal([15360], u.TempoRemove);
        Assert.Equal(new TimeSignatureDto(32, 3, 4), Assert.Single(u.TimeSigAdd!));
        Assert.Equal(2, u.Key);
        Assert.StartsWith("my notes\n[openutau-mcp] ", u.Comment);
        var block = SongComments.Read(u.Comment)!;
        Assert.Equal("minor", (string?)block["mode"]);
        Assert.Equal("9:1", (string?)block["sections"]![0]!["from"]);
        Assert.Equal("17:1", (string?)block["sections"]![0]!["to"]);
        var e = Assert.IsType<ProjectExpressionsOp>(ops[1]);
        Assert.Equal(("velc", "t1"), (e.Ensure[0], e.SuggestedFor));
    }

    [Fact]
    public async Task ProjectSet_KeyOnly_DoesNotTouchComment() {
        var bridge = Bridge();

        await ProjectSetTool.Run(bridge, key: "Bb");

        var u = Assert.IsType<ProjectUpdateOp>(Assert.Single(Applied(bridge).Ops));
        Assert.Equal(10, u.Key);
        Assert.Null(u.Comment);
    }

    [Theory]
    [InlineData("C", 0)]
    [InlineData("f#m", 6)]
    [InlineData("Ebmaj", 3)]
    [InlineData("11", 11)]
    public void ParseKey_AcceptsNamesAndNumbers(string text, int tonic) =>
        Assert.Equal(tonic, ProjectSetTool.ParseKey(text).tonic);

    [Fact]
    public void Syllables_HyphensMelismaAndHints() =>
        Assert.Equal(["Wear", "your", "porcelain", "+", "+", "crown", "+", "[f o n]"],
            LyricsSetTool.Syllables("Wear your por-ce-lain \"crown,\" + [f o n]"));

    [Fact]
    public async Task Lyrics_SkipsBreathsAndChangesOnlyDifferentNotes() {
        var bridge = Bridge();

        var result = await LyricsSetTool.Run(bridge, new NoteSelector("Lead"), "a can-dle +");

        Assert.False(result.IsError, Text(result));
        var p = Applied(bridge);
        Assert.Equal(4, p.IfRev!["p1"]);
        var ops = p.Ops.Cast<NoteUpdateOp>().Select(o => (o.NoteId, o.Lyric)).ToList();
        Assert.Equal([("n2", "candle"), ("n4", "+"), ("n5", "+")], ops);   // n1 already "a"; n3 is a breath
        Assert.Contains("\"assigned\":4", Text(result));
    }

    [Fact]
    public async Task Lyrics_CountMismatch_ReportsAlignmentAndChangesNothing() {
        var bridge = Bridge();

        var result = await LyricsSetTool.Run(bridge, new NoteSelector("Lead"), "one two");

        Assert.True(result.IsError);
        Assert.StartsWith("LYRICS_COUNT_MISMATCH: 2 syllables for 4 notes.", Text(result));
        Assert.Contains("\"alignment\":[[\"n1\",\"9:1\",\"one\"]", Text(result));
        Assert.DoesNotContain(bridge.Calls, c => c.method == Methods.EditApply);
    }

    [Fact]
    public async Task Lyrics_Pad_FillsRemainingNotesWithPlus() {
        var bridge = Bridge();

        await LyricsSetTool.Run(bridge, new NoteSelector("Lead"), "one two", onMismatch: "pad");

        var lyrics = Applied(bridge).Ops.Cast<NoteUpdateOp>().Select(o => o.Lyric).ToList();
        Assert.Equal(["one", "two", "+", "+"], lyrics);
    }
}
