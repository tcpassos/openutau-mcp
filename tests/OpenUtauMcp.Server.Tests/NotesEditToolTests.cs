using System.Text.Json;
using ModelContextProtocol.Protocol;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Contracts.Protocol;
using OpenUtauMcp.Server.Tools.Core;

namespace OpenUtauMcp.Server.Tests;

public class NotesEditToolTests {
    static string Text(CallToolResult r) => ((TextContentBlock)r.Content[0]).Text;

    static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static FakeBridge BridgeAcceptingEdits() => FakeBridge.WithProject()
        .On(Methods.EditApply, p => new EditApplyResult {
            Epoch = 1, Applied = 1, DryRun = false, PartRevs = new Dictionary<string, int> { ["p1"] = 5 }, StructureRev = 1,
            Created = new Dictionary<string, string> { ["@n1"] = "n91", ["@n0"] = "n90" },
        }, ContractsJson.Context.EditApplyResult)
        .On(Methods.NotesQuery, _ => new NotesQueryResult {
            PartId = "p1", PartPos = 15360, PartRev = 4, Epoch = 1, Total = 3,
            Notes = [
                new NoteDto { Id = "n7", Pos = 0, Dur = 480, Tone = 60, Lyric = "Can" },
                new NoteDto { Id = "n8", Pos = 480, Dur = 480, Tone = 62, Lyric = "+" },
                new NoteDto { Id = "n9", Pos = 1920, Dur = 960, Tone = 64, Lyric = "glass" },
            ],
        }, ContractsJson.Context.NotesQueryResult)
        .On(Methods.CatalogSinger, _ => new SingerInfo {
            Id = "s", Name = "LEONA", Type = "DiffSinger", Found = true,
            Effects = new Dictionary<string, string> { ["clr"] = Effects.Effective, ["tenc"] = Effects.NoEffect },
        }, ContractsJson.Context.SingerInfo);

    static EditApplyParams Applied(FakeBridge bridge) =>
        bridge.Calls.Single(c => c.method == Methods.EditApply).@params.Deserialize(ContractsJson.Context.EditApplyParams)!;

    [Fact]
    public async Task Update_MusicalUnits_BecomePartRelativeRawOps() {
        var bridge = BridgeAcceptingEdits();

        var result = await NotesEditTool.Run(bridge, "Lead",
            update: [new NoteUpdateInput("n7", At: "9:2.5", Dur: "1/8", Pitch: "C#5", Lyric: "crown")], ifRev: 4);

        Assert.False(result.IsError);
        var p = Applied(bridge);
        var op = Assert.IsType<NoteUpdateOp>(Assert.Single(p.Ops));
        Assert.Equal(720, op.Pos);        // bar 9 starts at 15360 = part start; beat 2.5 = +720
        Assert.Equal(240, op.Dur);
        Assert.Equal(73, op.Tone);
        Assert.Equal("crown", op.Lyric);
        Assert.Equal(4, p.IfRev!["p1"]);
        Assert.Equal("MCP: edit 1 note", p.UndoName);
    }

    [Fact]
    public async Task AddUpdateRemove_OneCall_BuildsAllOpsWithRefs() {
        var bridge = BridgeAcceptingEdits();

        var result = await NotesEditTool.Run(bridge, "p1",
            add: [
                new NoteAddInput("10:1", "1q", "D5", "oh", Vibrato: Json("\"off\""), PitchPoints: Json("[[-40,-100],[60,0,\"l\"]]"),
                    Expr: new Dictionary<string, double> { ["clr"] = 1 }),
                new NoteAddInput("10:2", "1q", "E5"),
            ],
            update: [new NoteUpdateInput(Select: new NoteFilter(Lyric: "^Can$"), Vibrato: Json("{\"depth\":30,\"in\":40}"),
                Expr: new Dictionary<string, JsonElement> { ["tenc"] = Json("10") })],
            remove: new NoteFilter(Ids: ["n9"]));

        Assert.False(result.IsError);
        var ops = Applied(bridge).Ops;
        var add = Assert.IsType<NoteAddOp>(ops[0]);
        Assert.Equal("@n0", add.Ref);
        Assert.Equal(0f, add.Vibrato!.Length);
        Assert.Equal(-10f, add.Pitch!.Points[0].Y);      // cents -> tenths of a semitone
        Assert.Equal("l", add.Pitch.Points[1].Shape);
        Assert.Equal(1f, add.Exprs!["clr"]);
        Assert.IsType<NoteAddOp>(ops[1]);
        var vib = Assert.IsType<NoteVibratoOp>(ops[2]);
        Assert.Equal(["n7"], vib.NoteIds);
        Assert.Equal(30f, vib.Vibrato!.Depth);
        Assert.IsType<NoteExprOp>(ops[3]);
        Assert.Equal(["n9"], Assert.IsType<NoteRemoveOp>(ops[4]).NoteIds);
        // Created ids come back in add order; tenc has no effect for this singer.
        Assert.Contains("\"created\":[\"n90\",\"n91\"]", Text(result));
        Assert.Contains("EXPRESSION_NO_EFFECT", Text(result));
    }

    [Fact]
    public async Task Update_PerPhonemeExpr_BecomesValues() {
        var bridge = BridgeAcceptingEdits();

        await NotesEditTool.Run(bridge, "p1",
            update: [new NoteUpdateInput("n9", Expr: new Dictionary<string, JsonElement> { ["clr"] = Json("[0, 2, null]") })]);

        var op = Assert.IsType<NoteExprOp>(Assert.Single(Applied(bridge).Ops));
        Assert.Equal([0f, 2f, null], op.Values!);
        Assert.Null(op.Value);
    }

    [Fact]
    public async Task Update_IdAndSelectTogether_IsRejected() {
        var result = await NotesEditTool.Run(BridgeAcceptingEdits(), "p1",
            update: [new NoteUpdateInput("n7", Select: new NoteFilter(Ids: ["n8"]), Lyric: "x")]);

        Assert.True(result.IsError);
        Assert.Contains("exactly one of 'id' or 'select'", Text(result));
    }

    [Fact]
    public async Task Update_PositionOutsidePart_IsRejectedBeforeCallingTheBridge() {
        var bridge = BridgeAcceptingEdits();

        var result = await NotesEditTool.Run(bridge, "p1", update: [new NoteUpdateInput("n7", At: "1:1")]);

        Assert.True(result.IsError);
        Assert.StartsWith("OUTSIDE_PART:", Text(result));
        Assert.DoesNotContain(bridge.Calls, c => c.method == Methods.EditApply);
    }

    [Fact]
    public async Task BridgeConflict_ReturnsCodeAndHint() {
        var bridge = FakeBridge.WithProject().Fail(Methods.EditApply, "CONFLICT", "Part p1 changed since it was read (partRev 4 -> 6).");

        var result = await NotesEditTool.Run(bridge, "p1", update: [new NoteUpdateInput("n7", Lyric: "x")], ifRev: 4);

        Assert.True(result.IsError);
        Assert.StartsWith("CONFLICT: Part p1 changed", Text(result));
        Assert.Contains("re-read it with ou_notes_get", Text(result));
        Assert.Contains("\"retryable\":true", Text(result));
    }

    [Fact]
    public async Task UnknownPartName_ListsParts() {
        var result = await NotesEditTool.Run(BridgeAcceptingEdits(), "Chorus", update: [new NoteUpdateInput("n7", Lyric: "x")]);

        Assert.True(result.IsError);
        Assert.Contains("NOT_FOUND", Text(result));
        Assert.Contains("\"name\":\"Lead\"", Text(result));
    }
}
