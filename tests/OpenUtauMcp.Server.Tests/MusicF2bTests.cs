using System.Text.Json;
using ModelContextProtocol.Protocol;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Contracts.Protocol;
using OpenUtauMcp.Server.Bridge;
using OpenUtauMcp.Server.Safety;
using OpenUtauMcp.Server.Tools.Core;
using OpenUtauMcp.Server.Tools.Music;

namespace OpenUtauMcp.Server.Tests;

public class MusicF2bTests {
    static string Text(CallToolResult r) => ((TextContentBlock)r.Content[0]).Text;

    static readonly string TempDir = Path.Combine(Path.GetTempPath(), "openutau-mcp-tests");

    static JsonElement PorcelainCrown() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "porcelain_crown.song.json"))).RootElement.Clone();

    static readonly SingerInfo leona = new() {
        Id = "LEONA", Name = "LEONA", Type = "DiffSinger", Found = true,
        Colors = ["01: Core (Normal)", "02: CatNip (Power)", "03: CatNap (Soft)"],
        Effects = new Dictionary<string, string> { ["clr"] = Effects.Effective, ["velc"] = Effects.Effective, ["genc"] = Effects.Effective },
        DefaultPhonemizer = "OpenUtau.Core.DiffSinger.DiffSingerEnglishPhonemizer", Renderers = ["DIFFSINGER"],
    };

    static StatusResult Status() => new() {
        Project = new StatusProject { Name = "Test", ChangesSaved = true, Epoch = 1, StructureRev = 1, Tracks = 1, Parts = 1 },
        Playback = new StatusPlayback("stopped", 0),
        History = new HistoryState { CanUndo = false, CanRedo = false, ChangesSaved = true },
    };

    static FakeBridge Bridge() => FakeBridge.WithProject()
        .On(Methods.Status, _ => Status(), ContractsJson.Context.StatusResult)
        .On(Methods.CatalogSinger, _ => leona, ContractsJson.Context.SingerInfo)
        .On(Methods.ProjectNew, _ => new ProjectOpenResult {
            Project = Status().Project, BackupPath = "b.ustx",
        }, ContractsJson.Context.ProjectOpenResult)
        .On(Methods.EditApply, _ => new EditApplyResult {
            Epoch = 1, Applied = 1, DryRun = false, PartRevs = new Dictionary<string, int>(), StructureRev = 2,
            Created = new Dictionary<string, string> { ["@t0"] = "t5", ["@p0"] = "p7", ["@t1"] = "t6", ["@p1"] = "p8" },
        }, ContractsJson.Context.EditApplyResult)
        .On(Methods.NotesQuery, _ => new NotesQueryResult {
            PartId = "p1", PartPos = 15360, PartRev = 3, Epoch = 1, Total = 4,
            Notes = [
                new NoteDto { Id = "n1", Pos = 0, Dur = 480, Tone = 69, Lyric = "wear" },
                new NoteDto { Id = "n2", Pos = 480, Dur = 480, Tone = 67, Lyric = "a" },
                new NoteDto { Id = "n3", Pos = 960, Dur = 960, Tone = 65, Lyric = "crown" },
                new NoteDto { Id = "n4", Pos = 1800, Dur = 400, Tone = 74, Lyric = "oh" },
            ],
        }, ContractsJson.Context.NotesQueryResult)
        .On(Methods.CurvesQuery, _ => new CurvesQueryResult { PartId = "p1", PartPos = 15360, PartRev = 3, Epoch = 1, Curves = [] },
            ContractsJson.Context.CurvesQueryResult);

    static List<EditOp> Ops(FakeBridge bridge) =>
        bridge.Calls.Last(c => c.method == Methods.EditApply).@params.Deserialize(ContractsJson.Context.EditApplyParams)!.Ops.ToList();

    [Fact]
    public async Task Compose_DryRun_ValidatesAndChangesNothing() {
        var bridge = Bridge();

        var result = await ComposeTool.Run(bridge, new PathPolicy([]), song: PorcelainCrown(), dryRun: true);

        Assert.False(result.IsError, Text(result));
        Assert.Contains("Lead 222 notes, Harmony 90 notes, Chops 44 notes", Text(result));
        Assert.DoesNotContain(bridge.Calls, c => c.method is Methods.EditApply or Methods.ProjectNew);
    }

    /// <summary>At the tool level: SONG_SPEC_INVALID with the field path and the counts.</summary>
    [Fact]
    public async Task Compose_BadLine_ReportsIssues() {
        var spec = JsonDocument.Parse("""
            {"sections":[{"name":"v","role":"verse","bars":2}],
             "voices":[{"name":"Lead","singer":"LEONA","lines":[{"section":"v","lyrics":"la la la","pitches":"C4 D4","rhythm":"1q*3"}]}]}
            """).RootElement.Clone();

        var result = await ComposeTool.Run(Bridge(), new PathPolicy([]), song: spec);

        Assert.True(result.IsError);
        Assert.StartsWith("SONG_SPEC_INVALID", Text(result));
        Assert.Contains("\"path\":\"voices[0].lines[0]\"", Text(result));
        Assert.Contains("3 syllables, 2 pitches, 3 note durations", Text(result));
    }

    [Fact]
    public async Task Compose_NewProject_OnePlanWithRefsAndHumanization() {
        var bridge = Bridge();

        var result = await ComposeTool.Run(bridge, new PathPolicy([]), song: PorcelainCrown(), tracks: new() { ["Lead"] = "LEONA", ["Harmony"] = "LEONA", ["Chops"] = "LEONA" });

        Assert.False(result.IsError, Text(result));
        // A dry run against the open project comes first, so a bad voice fails before the project is replaced.
        var calls = bridge.Calls.Select(c => c.method).ToList();
        var applies = bridge.Calls.Where(c => c.method == Methods.EditApply)
            .Select(c => c.@params.Deserialize(ContractsJson.Context.EditApplyParams)!.DryRun).ToList();
        Assert.Equal([true, false], applies);
        Assert.True(calls.IndexOf(Methods.EditApply) < calls.IndexOf(Methods.ProjectNew));
        var ops = Ops(bridge);
        var project = Assert.IsType<ProjectUpdateOp>(ops[0]);
        Assert.Equal((124.0, 2), (project.Bpm!.Value, project.Key!.Value));
        Assert.Contains("\"name\":\"chorus1\",\"role\":\"chorus\",\"from\":\"21:1\",\"to\":\"29:1\"", project.Comment);
        Assert.Equal(["Lead", "Harmony", "Chops"], ops.OfType<TrackAddOp>().Select(t => t.Name));
        Assert.Equal(222 + 90 + 44, ops.OfType<NoteAddOp>().Count(a => a.Ref is not null));
        Assert.Contains(ops.OfType<NotePitchOp>(), p => p.NoteIds[0].StartsWith("@n0.", StringComparison.Ordinal));
        Assert.Contains(ops.OfType<ProjectExpressionsOp>(), e => e.SuggestedForSinger == "LEONA");
        Assert.Empty(ops.OfType<TrackRemoveOp>());   // the fake project's only track has a part, so it stays
        Assert.Equal(3, ops.OfType<PartCommentOp>().Count());
    }

    /// <summary>target file without OpenUtau: the writer produces a .ustx with the three voices.</summary>
    [Fact]
    public async Task Compose_FileOffline_WritesUstx() {
        var bridge = new FakeBridge().Fail(Methods.Status, BridgeCallException.Unavailable, "no pipe");
        string file = Path.Combine(TempDir, "compose-offline.ustx");

        var result = await ComposeTool.Run(bridge, new PathPolicy([TempDir]), song: PorcelainCrown(),
            tracks: new() { ["Lead"] = "LEONA", ["Harmony"] = "LEONA", ["Chops"] = "LEONA" }, target: "file", path: file, overwrite: true);

        Assert.False(result.IsError, Text(result));
        Assert.Contains("OFFLINE", Text(result));
        var lines = File.ReadAllLines(file);
        Assert.Equal("ustx_version: \"0.10\"", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("comment: ", StringComparison.Ordinal) && l.Contains("[openutau-mcp]"));
        var parts = JsonDocument.Parse(lines.Single(l => l.StartsWith("voice_parts: ", StringComparison.Ordinal))["voice_parts: ".Length..]).RootElement;
        Assert.Equal(3, parts.GetArrayLength());
        var lead = parts[0].GetProperty("notes").EnumerateArray().Where(n => n.GetProperty("lyric").GetString() != "AP").ToList();
        Assert.Equal(222, lead.Count);
        Assert.Equal(("Candles", 57, 15360), (lead[0].GetProperty("lyric").GetString(), lead[0].GetProperty("tone").GetInt32(), lead[0].GetProperty("position").GetInt32()));
        Assert.Contains(parts[0].GetProperty("curves").EnumerateArray(), c => c.GetProperty("abbr").GetString() == "dyn");
        Assert.Contains("[openutau-mcp]", parts[0].GetProperty("comment").GetString());
    }

    [Fact]
    public async Task Variations_Create_OnePlanWithDuplicateNoteRefs() {
        var bridge = Bridge();

        var result = await VariationsTool.Run(bridge, "create", "Lead",
            [new VariantInput("B", Seed: 11), new VariantInput("C", Preset: "backing", Seed: 12)]);

        Assert.False(result.IsError, Text(result));
        var ops = Ops(bridge);
        var adds = ops.OfType<TrackAddOp>().ToList();
        Assert.Equal([("Vocal [B]", "t1"), ("Vocal [C]", "t1")], adds.Select(a => (a.Name!, a.CopyFrom!)));
        Assert.All(ops.OfType<TrackMixOp>(), m => Assert.True(m.Mute));
        var dups = ops.OfType<PartDuplicateOp>().ToList();
        Assert.Equal(["@d0:", "@d1:"], dups.Select(d => d.NoteRefPrefix!));
        Assert.Contains(ops.OfType<NotePitchOp>(), p => p.NoteIds[0] == "@d1:n1");
        Assert.Contains(ops.OfType<PartCommentOp>(), c => c.PartId == "@p0" && c.Comment.Contains("\"variationOf\":\"t1\"") && c.Comment.Contains("\"label\":\"B\""));
        Assert.Contains("\"track\":\"t5\"", Text(result));
    }

    [Fact]
    public async Task Variations_Activate_MutesTheOthers() {
        var bridge = new FakeBridge().On(Methods.ProjectGet, _ => new ProjectDto {
            Epoch = 1, StructureRev = 1, Name = "T", Resolution = 480, TimeSignatures = [new TimeSignatureDto(0, 4, 4)],
            Tracks = [new TrackDto { Id = "t1", Index = 0, Name = "Lead" }, new TrackDto { Id = "t2", Index = 1, Name = "Lead [B]", Mute = true },
                      new TrackDto { Id = "t3", Index = 2, Name = "Lead [C]", Mute = true }],
            Parts = [new PartDto { Id = "p1", Name = "Lead", TrackId = "t1", Type = "voice", Pos = 0, Dur = 1920 },
                     new PartDto { Id = "p2", Name = "Lead [B]", TrackId = "t2", Type = "voice", Pos = 0, Dur = 1920 },
                     new PartDto { Id = "p3", Name = "Lead [C]", TrackId = "t3", Type = "voice", Pos = 0, Dur = 1920 }],
        }, ContractsJson.Context.ProjectDto).On(Methods.EditApply, _ => new EditApplyResult {
            Epoch = 1, Applied = 1, DryRun = false, PartRevs = new Dictionary<string, int>(), StructureRev = 1,
        }, ContractsJson.Context.EditApplyResult);

        var result = await VariationsTool.Run(bridge, "activate", "p2", variant: "c");

        Assert.False(result.IsError, Text(result));
        var mix = Ops(bridge).Cast<TrackMixOp>().Select(m => (m.TrackId, m.Mute)).ToList();
        Assert.Equal([("t1", (bool?)true), ("t2", true), ("t3", false)], mix);

        var promote = await VariationsTool.Run(bridge, "promote", "Lead", variant: "B");
        Assert.StartsWith("CONFIRMATION_REQUIRED", Text(promote));
    }

    [Fact]
    public async Task Analyze_FindsLeapsRawPhrasesAndOverlaps() {
        var bridge = Bridge();

        var result = await AnalyzeTool.Run(bridge, new NoteSelector("Lead"));

        Assert.False(result.IsError, Text(result));
        string text = Text(result);
        Assert.Contains("[\"n4\",\"9:4.75\",", text);
        Assert.Contains("D5\",9]]", text);
        Assert.Contains("n3 and n4 overlap", text);
        Assert.Contains("is raw", text);
        Assert.Contains("\"lowest\":\"F4\",\"highest\":\"D5\"", text);
    }
}
