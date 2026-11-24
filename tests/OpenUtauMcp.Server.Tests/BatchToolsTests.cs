using System.Text.Json;
using ModelContextProtocol.Protocol;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Protocol;
using OpenUtauMcp.Server.Tools.Core;

namespace OpenUtauMcp.Server.Tests;

public class BatchToolsTests {
    static string Text(CallToolResult r) => ((TextContentBlock)r.Content[0]).Text;

    static FakeBridge Bridge(string jobState = JobStates.Succeeded, bool changed = true) => FakeBridge.WithProject()
        .On(Methods.NotesQuery, _ => new NotesQueryResult {
            PartId = "p1", PartPos = 15360, PartRev = 4, Epoch = 1, Total = 3,
            Notes = [
                new NoteDto { Id = "n1", Pos = 0, Dur = 470, Tone = 69, Lyric = "a" },
                new NoteDto { Id = "n2", Pos = 470, Dur = 490, Tone = 72, Lyric = "b" },
                new NoteDto { Id = "n3", Pos = 1920, Dur = 480, Tone = 72, Lyric = "c" },
            ],
        }, ContractsJson.Context.NotesQueryResult)
        .On(Methods.BatchRun, _ => new BatchRunResult {
            Epoch = 1, PartId = "p1", PartRev = 5, Changed = true, Created = ["n4", "n5"], Removed = ["n1", "n2"],
            Warnings = [new Warning(WarningCodes.SeparateUndoStep, "separate", "p1")],
        }, ContractsJson.Context.BatchRunResult)
        .On(Methods.JobsStart, _ => new JobDto {
            JobId = "j1", Kind = JobKinds.BatchLoadRenderedPitch, State = JobStates.Running, StartedAt = DateTimeOffset.Now,
        }, ContractsJson.Context.JobDto)
        .On(Methods.JobsWait, _ => new JobDto {
            JobId = "j1", Kind = JobKinds.BatchLoadRenderedPitch, State = jobState, StartedAt = DateTimeOffset.Now,
            Result = jobState == JobStates.Succeeded ? new JobResult { Changed = changed, PartId = "p1", PartRev = 6 } : null,
            Error = jobState == JobStates.Failed ? new JobError(ErrorCodes.BatchFailed, "Current renderer doesn't support generating pitch curve") : null,
        }, ContractsJson.Context.JobDto)
        .On(Methods.CurvesQuery, _ => new CurvesQueryResult {
            PartId = "p1", PartPos = 15360, PartRev = 4, Epoch = 1,
            Curves = [
                new CurveDto { Abbr = "dyn", Xs = [0, 480], Ys = [0, 10], Min = -240, Max = 120, RealXs = [0, 240, 480], RealYs = [-20, -10, 0] },
                new CurveDto { Abbr = "ene", Xs = [], Ys = [], Min = -100, Max = 100 },
            ],
        }, ContractsJson.Context.CurvesQueryResult);

    static T Sent<T>(FakeBridge bridge, string method, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        bridge.Calls.Single(c => c.method == method).@params.Deserialize(type)!;

    [Fact]
    public async Task Sync_RangeSelectsNotes_GridParsed_ReportsCreatedRemoved() {
        var bridge = Bridge();

        var result = await BatchEditTool.Run(bridge, "quantize", new NoteSelector("Lead", Range: new RangeInput(Bars: "9")),
            new BatchArgsInput(Grid: "1/16"));

        Assert.False(result.IsError, Text(result));
        var p = Sent(bridge, Methods.BatchRun, ContractsJson.Context.BatchRunParams);
        Assert.Equal(("quantize", "p1", 120), (p.Name, p.PartId, p.Grid));
        Assert.Equal(["n1", "n2"], p.NoteIds);
        string text = Text(result);
        Assert.Contains("\"created\":[\"n4\",\"n5\"],\"removed\":[\"n1\",\"n2\"]", text);
        Assert.Contains("SEPARATE_UNDO_STEP", text);
    }

    [Fact]
    public async Task Sync_PartOnlySelector_SendsNoNoteIds() {
        var bridge = Bridge();

        await BatchEditTool.Run(bridge, "fix_overlap", new NoteSelector("Lead"));

        Assert.Null(Sent(bridge, Methods.BatchRun, ContractsJson.Context.BatchRunParams).NoteIds);
        Assert.DoesNotContain(bridge.Calls, c => c.method == Methods.NotesQuery);
    }

    [Fact]
    public async Task ResetAll_NeedsConfirm() {
        var bridge = Bridge();

        var result = await BatchEditTool.Run(bridge, "reset_all", new NoteSelector("Lead"));

        Assert.StartsWith("CONFIRMATION_REQUIRED", Text(result));
        Assert.DoesNotContain(bridge.Calls, c => c.method == Methods.BatchRun);
    }

    [Fact]
    public async Task Async_StartsJob_WarnsSeparateUndoStep() {
        var bridge = Bridge();

        var result = await BatchEditTool.Run(bridge, "load_rendered_pitch", new NoteSelector("Lead"));

        Assert.False(result.IsError, Text(result));
        var p = Sent(bridge, Methods.JobsStart, ContractsJson.Context.JobStartParams);
        Assert.Equal((JobKinds.BatchLoadRenderedPitch, "p1", (string?)null), (p.Kind, p.PartId, p.Path));
        Assert.Contains("SEPARATE_UNDO_STEP", Text(result));
        Assert.Contains("\"partRev\":{\"p1\":6}", Text(result));
    }

    [Fact]
    public async Task Async_Failure_IsBatchFailed() {
        var result = await BatchEditTool.Run(Bridge(JobStates.Failed), "load_rendered_pitch", new NoteSelector("Lead"));

        Assert.True(result.IsError);
        Assert.StartsWith("BATCH_FAILED: Current renderer doesn't support", Text(result));
    }

    [Fact]
    public async Task Async_StillRunning_ReturnsJob() {
        var result = await BatchEditTool.Run(Bridge(JobStates.Running), "refresh_real_curves", new NoteSelector("Lead"), waitSeconds: 0);

        Assert.False(result.IsError);
        Assert.Contains("still running (job j1)", Text(result));
    }

    [Fact]
    public async Task CurvesGet_Real_ReadsRendererCurve() {
        var bridge = Bridge();

        var result = await CurvesGetTool.Run(bridge, "Lead", curves: ["real:dyn", "real:ene"], points: true);

        Assert.False(result.IsError, Text(result));
        var q = Sent(bridge, Methods.CurvesQuery, ContractsJson.Context.CurvesQueryParams);
        Assert.True(q.Real);
        string text = Text(result);
        Assert.Contains("\"curve\":\"real:dyn\"", text);
        Assert.Contains("[\"9:1.5\",-10]", text);
        Assert.DoesNotContain("\"curve\":\"dyn\"", text);
        Assert.Contains("No real curve yet", text);
    }
}
