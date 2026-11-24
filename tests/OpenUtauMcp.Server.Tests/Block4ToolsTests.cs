using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Contracts.Protocol;
using OpenUtauMcp.Server.Bridge;
using OpenUtauMcp.Server.Safety;
using OpenUtauMcp.Server.Tools.Core;

namespace OpenUtauMcp.Server.Tests;

public class Block4ToolsTests {
    static string Text(CallToolResult r) => ((TextContentBlock)r.Content[0]).Text;

    static readonly string TempDir = Path.Combine(Path.GetTempPath(), "openutau-mcp-tests");

    // Part p1: bar 9 (15360) to bar 13; dyn has points at bar 9 beat 1 (0) and bar 10 beat 1 (40).
    static FakeBridge Bridge() => FakeBridge.WithProject()
        .On(Methods.EditApply, _ => new EditApplyResult {
            Epoch = 1, Applied = 1, DryRun = false, PartRevs = new Dictionary<string, int> { ["p1"] = 2 }, StructureRev = 1,
            Created = new Dictionary<string, string> { ["@t0"] = "t5", ["@p0"] = "p7" },
        }, ContractsJson.Context.EditApplyResult)
        .On(Methods.CurvesQuery, _ => new CurvesQueryResult {
            PartId = "p1", PartPos = 15360, PartRev = 1, Epoch = 1,
            Curves = [new CurveDto { Abbr = "dyn", Xs = [0, 1920], Ys = [0, 40], Min = -240, Max = 120 }],
        }, ContractsJson.Context.CurvesQueryResult)
        .On(Methods.CatalogSinger, _ => new SingerInfo {
            Id = "s", Name = "LEONA", Type = "DiffSinger", Found = true, Effects = new Dictionary<string, string>(),
        }, ContractsJson.Context.SingerInfo)
        .On(Methods.CatalogSingers, _ => new SingersCatalog {
            Total = 3, Singers = [new SingerSummary("leona", "LEONA", "DiffSinger", "Ubasti", "1.0", true)],
        }, ContractsJson.Context.SingersCatalog)
        .On(Methods.G2pQuery, _ => new G2pQueryResult {
            TrackId = "t1", Phonemizer = "DiffSingerEnglishPhonemizer",
            Words = [new G2pEntry("crown", ["k", "r", "aw", "n"]), new G2pEntry("ubasti", null)],
        }, ContractsJson.Context.G2pQueryResult)
        .On(Methods.ProjectExport, _ => new ProjectExportResult(Path.Combine(TempDir, "lead.ds"), 1234), ContractsJson.Context.ProjectExportResult)
        .On(Methods.Status, _ => new StatusResult {
            Project = new StatusProject { Name = "Test", ChangesSaved = true, Epoch = 1, StructureRev = 1, Tracks = 1, Parts = 1 },
            Playback = new StatusPlayback("stopped", 0),
            History = new HistoryState { CanUndo = false, CanRedo = false, ChangesSaved = true },
        }, ContractsJson.Context.StatusResult)
        .On(Methods.ProjectImportTracks, _ => new ProjectImportResult {
            Project = new StatusProject { Name = "Test", ChangesSaved = false, Epoch = 1, StructureRev = 2, Tracks = 3, Parts = 3 },
            BackupPath = "b.ustx", CreatedTracks = ["t2", "t3"], CreatedParts = ["p2", "p3"],
        }, ContractsJson.Context.ProjectImportResult);

    static List<EditOp> Ops(FakeBridge bridge) =>
        bridge.Calls.Single(c => c.method == Methods.EditApply).@params.Deserialize(ContractsJson.Context.EditApplyParams)!.Ops.ToList();

    [Fact]
    public async Task Curves_ShapeRamp_TwoPointsOverInclusiveRange() {
        var bridge = Bridge();

        var result = await CurvesEditTool.Run(bridge, "p1",
            [new CurveOpInput("shape", "cl01", Range: new RangeInput(Bars: "9"), Kind: "ramp", Start: 100, End: 0)]);

        Assert.False(result.IsError, Text(result));
        var op = Assert.IsType<CurveSetRangeOp>(Assert.Single(Ops(bridge)));
        Assert.Equal((0, 1915), (op.FromTick, op.ToTick));
        Assert.Equal([0, 1915], op.Xs);
        Assert.Equal([100, 0], op.Ys);
    }

    [Fact]
    public async Task Curves_ShapeArc_PeaksWherePeakAtSays() {
        var bridge = Bridge();

        await CurvesEditTool.Run(bridge, "p1",
            [new CurveOpInput("shape", "dyn", Range: new RangeInput(Bars: "9"), Kind: "arc", Start: 0, End: 0, Peak: 60, PeakAt: 0.25)]);

        var op = Assert.IsType<CurveSetRangeOp>(Assert.Single(Ops(bridge)));
        int top = op.Ys.Max();
        Assert.Equal(60, top);
        int x = op.Xs[op.Ys.ToList().IndexOf(top)];
        Assert.InRange(x, 400, 560);
        Assert.Equal(0, op.Ys[0]);
        Assert.Equal(0, op.Ys[^1]);
    }

    [Fact]
    public async Task Curves_TransformScalesExistingPointsAndClamps() {
        var bridge = Bridge();

        await CurvesEditTool.Run(bridge, "p1",
            [new CurveOpInput("transform", "dyn", Range: new RangeInput("9:1", "11:1"), Scale: 4, Offset: -10)]);

        var op = Assert.IsType<CurveSetRangeOp>(Assert.Single(Ops(bridge)));
        Assert.Equal([0, 1920, 3835], op.Xs);
        Assert.Equal([-10, 120, -10], op.Ys);   // 40*4-10 = 150 clamps to 120; past the last point the curve is its default (0)
    }

    [Fact]
    public async Task Curves_CopyShiftsTheSpan() {
        var bridge = Bridge();

        await CurvesEditTool.Run(bridge, "p1",
            [new CurveOpInput("copy", "dyn", Range: new RangeInput(Bars: "9"), To: "11:1")]);

        var op = Assert.IsType<CurveSetRangeOp>(Assert.Single(Ops(bridge)));
        Assert.Equal((3840, 3840 + 1915), (op.FromTick, op.ToTick));
        Assert.Equal([3840, 3840 + 1915], op.Xs);
        Assert.Equal(0, op.Ys[0]);
    }

    [Fact]
    public async Task Catalog_SingersPagesAndG2pMarksMissingWords() {
        var bridge = Bridge();

        var singers = await CatalogGetTool.Run(bridge, "singers", limit: 1);
        Assert.Contains("\"next\":\"o:1\"", Text(singers));

        var g2p = await CatalogGetTool.Run(bridge, "g2p", track: "t1", words: ["crown", "ubasti"]);
        Assert.Contains("\"crown\":\"k r aw n\",\"ubasti\":null", Text(g2p));
        Assert.StartsWith("1 of 2 words have phonemes", Text(g2p));
    }

    [Fact]
    public async Task Save_ExportDsNeedsPartAndChecksExtension() {
        var policy = new PathPolicy([TempDir]);

        var missingPart = await ProjectSaveTool.Run(Bridge(), policy, "export", Path.Combine(TempDir, "lead.ds"), format: "ds");
        Assert.Contains("pass 'part'", Text(missingPart));

        var wrongExt = await ProjectSaveTool.Run(Bridge(), policy, "export", Path.Combine(TempDir, "lead.mid"), format: "ds", part: "Lead");
        Assert.StartsWith("UNSUPPORTED_FORMAT", Text(wrongExt));

        var bridge = Bridge();
        var ok = await ProjectSaveTool.Run(bridge, policy, "export", Path.Combine(TempDir, "lead.ds"), format: "ds", part: "Lead", overwrite: true);
        Assert.False(ok.IsError, Text(ok));
        var p = bridge.Calls.Single(c => c.method == Methods.ProjectExport).@params.Deserialize(ContractsJson.Context.ProjectExportParams)!;
        Assert.Equal(("ds", "p1"), (p.Format, p.PartId));
    }

    [Fact]
    public async Task Open_ImportTracksReportsNewIds() {
        var bridge = Bridge();

        var result = await ProjectOpenTool.Run(bridge, new PathPolicy([TempDir]), "import_tracks",
            paths: [Path.Combine(TempDir, "a.ustx")], importTempo: true);

        Assert.False(result.IsError, Text(result));
        var p = bridge.Calls.Single(c => c.method == Methods.ProjectImportTracks).@params.Deserialize(ContractsJson.Context.ProjectImportParams)!;
        Assert.True(p.ImportTempo);
        Assert.Contains("\"createdTracks\":[\"t2\",\"t3\"]", Text(result));
        Assert.Contains("Existing ids stay valid", Text(result));
    }

    [Fact]
    public async Task Parts_AddAudioWithoutTrack_CreatesTrackThenWavePart() {
        var bridge = Bridge();

        var result = await PartsEditTool.Run(bridge, new PathPolicy([TempDir]),
            [new PartOpInput("add_audio", File: Path.Combine(TempDir, "beat.wav"), From: "9:1")]);

        Assert.False(result.IsError, Text(result));
        var ops = Ops(bridge);
        Assert.Equal(("beat", "@t0"), (Assert.IsType<TrackAddOp>(ops[0]).Name, ((TrackAddOp)ops[0]).Ref));
        var wave = Assert.IsType<PartAddWaveOp>(ops[1]);
        Assert.Equal(("@t0", 15360), (wave.TrackId, wave.Pos));
    }

    /// <summary>A compact read of 200 notes stays within about 4000 tokens.</summary>
    [Fact]
    public async Task CompactRead_200Notes_FitsTheTokenBudget() {
        string[] lyrics = ["wear", "your", "porcelain", "+", "crown", "AP", "the", "night"];
        var bridge = FakeBridge.WithProject().On(Methods.NotesQuery, _ => new NotesQueryResult {
            PartId = "p1", PartPos = 15360, PartRev = 1, Epoch = 1, Total = 200,
            Notes = Enumerable.Range(0, 200).Select(i => new NoteDto {
                Id = $"n{i + 1}", Pos = i * 240, Dur = 240, Tone = 60 + i % 12, Lyric = lyrics[i % lyrics.Length],
            }).ToList(),
        }, ContractsJson.Context.NotesQueryResult);

        var result = await NotesGetTool.Run(bridge, new NoteSelector("Lead"));

        int chars = Text(result).Length;
        Assert.Contains("200 of 200 notes", Text(result));
        Assert.True(chars / 4 <= 4000, $"200 compact notes are about {chars / 4} tokens ({chars} characters).");
    }

    /// <summary>The tool list stays small enough for the model's context (about 4 characters per token, 500 per tool).</summary>
    [Fact]
    public async Task ToolList_FitsTheTokenBudget() {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IBridgeClient>(new FakeBridge());
        builder.Services.AddSingleton(new PathPolicy([]));
        Program.AddServer(builder.Services)
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        using var host = builder.Build();
        await host.StartAsync();
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));

        var tools = await client.ListToolsAsync();
        var relaxed = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var sizes = tools.Select(t => (t.Name, chars: JsonSerializer.Serialize(t.ProtocolTool, relaxed).Length)).OrderByDescending(x => x.chars).ToList();
        int chars = sizes.Sum(x => x.chars);
        if (Environment.GetEnvironmentVariable("OPENUTAU_MCP_DUMP_TOOLS") is { Length: > 0 } dump) {
            File.WriteAllText(dump, JsonSerializer.Serialize(tools.Select(t => t.ProtocolTool), relaxed));
        }
        await host.StopAsync();

        Assert.Equal(22, tools.Count);
        // The budget started at 9000 tokens for 18 tools and scales to 500 per tool.
        Assert.True(chars / 4 <= 500 * tools.Count, $"tools/list is about {chars / 4} tokens ({chars} characters): " +
            string.Join(", ", sizes.Select(x => $"{x.Name} {x.chars / 4}")));
    }
}
