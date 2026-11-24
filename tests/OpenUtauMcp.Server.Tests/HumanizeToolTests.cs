using System.Text.Json;
using ModelContextProtocol.Protocol;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Contracts.Protocol;
using OpenUtauMcp.Server.Tools.Core;
using OpenUtauMcp.Server.Tools.Music;

namespace OpenUtauMcp.Server.Tests;

public class HumanizeToolTests {
    static string Text(CallToolResult r) => ((TextContentBlock)r.Content[0]).Text;

    static FakeBridge Bridge() => FakeBridge.WithProject()
        .On(Methods.NotesQuery, _ => new NotesQueryResult {
            PartId = "p1", PartPos = 15360, PartRev = 3, Epoch = 1, Total = 4,
            Notes = [
                new NoteDto { Id = "n1", Pos = 0, Dur = 480, Tone = 62, Lyric = "Can" },
                new NoteDto { Id = "n2", Pos = 480, Dur = 480, Tone = 65, Lyric = "+" },
                new NoteDto { Id = "n3", Pos = 960, Dur = 960, Tone = 69, Lyric = "glass" },
                new NoteDto { Id = "n4", Pos = 2880, Dur = 1440, Tone = 67, Lyric = "night" },
            ],
        }, ContractsJson.Context.NotesQueryResult)
        .On(Methods.CatalogSinger, _ => new SingerInfo {
            Id = "leona", Name = "LEONA", Type = "DiffSinger", Found = true,
            Colors = ["01: Core (Normal)", "02: CatNip (Power)", "03: CatNap (Soft)"],
            Effects = new Dictionary<string, string> {
                ["clr"] = Effects.Effective, ["velc"] = Effects.Effective, ["genc"] = Effects.Effective, ["pitd"] = Effects.Effective,
            },
        }, ContractsJson.Context.SingerInfo)
        .On(Methods.CurvesQuery, _ => new CurvesQueryResult { PartId = "p1", PartPos = 15360, PartRev = 3, Epoch = 1, Curves = [] },
            ContractsJson.Context.CurvesQueryResult)
        .On(Methods.EditApply, _ => new EditApplyResult {
            Epoch = 1, Applied = 1, DryRun = false, PartRevs = new Dictionary<string, int> { ["p1"] = 4 }, StructureRev = 1,
        }, ContractsJson.Context.EditApplyResult);

    static readonly SectionInput[] chorus = [new("chorus1", "chorus", Bars: "9-12")];

    [Fact]
    public async Task DryRun_ReportsSeedAndChangesNothing() {
        var bridge = Bridge();

        var result = await HumanizeTool.Run(bridge, new NoteSelector("p1"), seed: 7, sections: chorus, dryRun: true);

        Assert.False(result.IsError);
        Assert.Contains("seed 7", Text(result));
        Assert.DoesNotContain(bridge.Calls, c => c.method == Methods.EditApply);
    }

    [Fact]
    public async Task Apply_WritesPitchVibratoColorCurvesBreathAndHistory_InOneCall() {
        var bridge = Bridge();

        var result = await HumanizeTool.Run(bridge, new NoteSelector("p1"), seed: 7, sections: chorus);

        Assert.False(result.IsError, Text(result));
        var call = Assert.Single(bridge.Calls, c => c.method == Methods.EditApply);
        var p = call.@params.Deserialize(ContractsJson.Context.EditApplyParams)!;
        Assert.Equal(3, p.IfRev!["p1"]);
        Assert.Contains(p.Ops, op => op is ProjectExpressionsOp e && e.Ensure.Contains("velc"));
        var scoop = p.Ops.OfType<NotePitchOp>().First(op => op.NoteIds[0] == "n1").Pitch!;
        Assert.False(scoop.SnapFirst);  // keeps the attack from below after a rest
        Assert.True(p.Ops.OfType<NotePitchOp>().First(op => op.NoteIds[0] == "n2").Pitch!.SnapFirst);
        Assert.Contains(p.Ops, op => op is NoteExprOp { Abbr: "clr", Value: 1 });  // chorus -> power
        Assert.Contains(p.Ops, op => op is NoteAddOp { Lyric: "AP" });
        Assert.Contains(p.Ops, op => op is CurveSetRangeOp { Abbr: "dyn" });
        var history = Assert.IsType<PartCommentOp>(p.Ops[^1]);
        Assert.Contains("\"seed\":7", history.Comment);
        Assert.StartsWith(SongComments.Prefix, history.Comment);
        Assert.Contains("seed 7", Text(result));
    }

    [Fact]
    public async Task Conflict_RereadsAndRetriesOnce() {
        var bridge = Bridge();
        int applies = 0;
        bridge.On(Methods.EditApply, _ => {
            if (++applies == 1) {
                throw new Bridge.BridgeCallException("CONFLICT", "Part p1 changed since it was read (partRev 3 -> 4).", null);
            }
            return new EditApplyResult { Epoch = 1, Applied = 1, DryRun = false, PartRevs = new Dictionary<string, int> { ["p1"] = 5 }, StructureRev = 1 };
        }, ContractsJson.Context.EditApplyResult);

        var result = await HumanizeTool.Run(bridge, new NoteSelector("p1"), seed: 7, sections: chorus);

        Assert.False(result.IsError, Text(result));
        Assert.Equal(2, applies);
        Assert.Equal(2, bridge.Calls.Count(c => c.method == Methods.NotesQuery));  // read again before retrying
    }

    [Fact]
    public async Task UnknownLayer_IsAnInputError() {
        var layers = JsonDocument.Parse("{\"wobble\":false}").RootElement;

        var result = await HumanizeTool.Run(Bridge(), new NoteSelector("p1"), layers: layers);

        Assert.True(result.IsError);
        Assert.Contains("Unknown layer 'wobble'", Text(result));
    }
}
