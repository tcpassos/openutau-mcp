using System.Text.Json;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Contracts.Protocol;
using OpenUtauMcp.Server.Tools.Core;

namespace OpenUtauMcp.Server.Tests;

public class CurvesToolTests {
    static FakeBridge Bridge() => FakeBridge.WithProject()
        .On(Methods.EditApply, _ => new EditApplyResult {
            Epoch = 1, Applied = 1, DryRun = false, PartRevs = new Dictionary<string, int> { ["p1"] = 2 }, StructureRev = 1,
        }, ContractsJson.Context.EditApplyResult)
        .On(Methods.CatalogSinger, _ => new SingerInfo {
            Id = "s", Name = "LEONA", Type = "DiffSinger", Found = true, Effects = new Dictionary<string, string>(),
        }, ContractsJson.Context.SingerInfo);

    static IReadOnlyList<EditOp> Ops(FakeBridge bridge) =>
        bridge.Calls.Single(c => c.method == Methods.EditApply).@params.Deserialize(ContractsJson.Context.EditApplyParams)!.Ops;

    [Fact]
    public async Task Set_DefaultMode_IsRangeBetweenFirstAndLastPoint() {
        var bridge = Bridge();

        await CurvesEditTool.Run(bridge, "p1", [new CurveOpInput("set", "dyn", [new("9:2", -30), new("10:1", 10)])]);

        var op = Assert.IsType<CurveSetRangeOp>(Assert.Single(Ops(bridge)));
        Assert.Equal(480, op.FromTick);
        Assert.Equal(1920, op.ToTick);
        Assert.Equal([480, 1920], op.Xs);
    }

    [Fact]
    public async Task Set_Step_HoldsValueUntilNextPoint_AndEnsurePrependsRegistration() {
        var bridge = Bridge();

        await CurvesEditTool.Run(bridge, "p1",
            [new CurveOpInput("set", "velc", [new("9:1", 90), new("9:2", 110)], Mode: "replace_all", Interp: "step")], ensure: true);

        var ops = Ops(bridge);
        var ensure = Assert.IsType<ProjectExpressionsOp>(ops[0]);
        Assert.Equal(["velc"], ensure.Ensure);
        Assert.Equal("t1", ensure.SuggestedFor);
        var set = Assert.IsType<CurveReplaceOp>(ops[1]);
        Assert.Equal([0, 475, 480], set.Xs);
        Assert.Equal([90, 90, 110], set.Ys);
    }

    [Fact]
    public async Task Clear_WithBars_SendsPartRelativeRange() {
        var bridge = Bridge();

        await CurvesEditTool.Run(bridge, "p1", [new CurveOpInput("clear", "pitd", Range: new RangeInput(Bars: "10"))]);

        var op = Assert.IsType<CurveClearOp>(Assert.Single(Ops(bridge)));
        Assert.Equal(1920, op.FromTick);
        Assert.Equal(3835, op.ToTick);  // inclusive: the point at the start of bar 11 is kept
    }

    [Fact]
    public void Stats_UseDefaultOutsidePoints_AndLinearBetween() {
        var curve = new CurveDto { Abbr = "dyn", Xs = [100, 200], Ys = [0, 100], DefaultValue = -10 };

        Assert.Equal(-10, CurvesGetTool.Sample(curve, 50));
        Assert.Equal(50, CurvesGetTool.Sample(curve, 150));
        var (min, max, _) = CurvesGetTool.Stats(curve, 100, 200);
        Assert.Equal(0, min);
        Assert.True(max >= 95);
    }
}
