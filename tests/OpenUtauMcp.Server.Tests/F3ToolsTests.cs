using System.Text.Json;
using ModelContextProtocol.Protocol;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Protocol;
using OpenUtauMcp.Server.Safety;
using OpenUtauMcp.Server.Tools.Audio;
using OpenUtauMcp.Server.Tools.Core;

namespace OpenUtauMcp.Server.Tests;

public class F3ToolsTests {
    static string Text(CallToolResult r) => ((TextContentBlock)r.Content[0]).Text;

    static readonly string TempDir = Path.Combine(Path.GetTempPath(), "openutau-mcp-tests");

    static FakeBridge Bridge(DawState? daw = null) => FakeBridge.WithProject()
        .On(Methods.Status, _ => new StatusResult {
            Project = new StatusProject { Name = "T", ChangesSaved = true, Epoch = 1, StructureRev = 1, Tracks = 1, Parts = 1 },
            Playback = new StatusPlayback("stopped", 0),
            History = new HistoryState { CanUndo = false, CanRedo = false, ChangesSaved = true },
        }, ContractsJson.Context.StatusResult)
        .On(Methods.JobsStart, _ => new JobDto { JobId = "j1", Kind = JobKinds.ExportCustom, State = JobStates.Running, StartedAt = DateTimeOffset.Now },
            ContractsJson.Context.JobDto)
        .On(Methods.JobsWait, _ => new JobDto {
            JobId = "j1", Kind = JobKinds.ExportCustom, State = JobStates.Succeeded, StartedAt = DateTimeOffset.Now,
            Result = new JobResult { Files = [new ExportedFile { Path = "x.wav", Bytes = 100, Seconds = 15.48, Channels = 2 }] },
        }, ContractsJson.Context.JobDto)
        .On(Methods.DawGet, _ => daw ?? new DawState { State = "Disconnected", Connections = [], ProjectBpm = 120 }, ContractsJson.Context.DawState)
        .On(Methods.DawScan, _ => daw ?? new DawState { State = "Disconnected", Connections = [], ProjectBpm = 120 }, ContractsJson.Context.DawState);

    static JobStartParams Started(FakeBridge bridge) =>
        bridge.Calls.Single(c => c.method == Methods.JobsStart).@params.Deserialize(ContractsJson.Context.JobStartParams)!;

    [Fact]
    public async Task Render_RangeTracksFormat_UsesTheCustomExporter() {
        var bridge = Bridge();

        var result = await RenderTool.Run(bridge, new PathPolicy([TempDir]), "export", path: Path.Combine(TempDir, "chorus.wav"), overwrite: true,
            range: new RangeInput(Bars: "17-24"), tracks: ["Vocal"], format: "wav32f");

        Assert.False(result.IsError, Text(result));
        var p = Started(bridge);
        Assert.Equal((JobKinds.ExportCustom, 16 * 1920, 24 * 1920, "wav32f", false), (p.Kind, p.FromTick!.Value, p.ToTick!.Value, p.Format, p.PerTrack));
        Assert.Equal(["t1"], p.TrackIds);
    }

    [Fact]
    public async Task Render_PlainExport_KeepsOpenUtausExporter() {
        var bridge = Bridge();

        await RenderTool.Run(bridge, new PathPolicy([TempDir]), "export", scope: "stems", path: Path.Combine(TempDir, "song.wav"));

        Assert.Equal(JobKinds.ExportStems, Started(bridge).Kind);
    }

    [Fact]
    public async Task Render_OpenRange_RunsToTheEnd() {
        var bridge = Bridge();

        await RenderTool.Run(bridge, new PathPolicy([TempDir]), "export", path: Path.Combine(TempDir, "tail.wav"), overwrite: true,
            range: new RangeInput(From: "33:1"), channels: 1);

        var p = Started(bridge);
        Assert.Equal((32 * 1920, (int?)null, 1), (p.FromTick!.Value, p.ToTick, p.Channels!.Value));
    }

    [Fact]
    public async Task Daw_Status_WarnsAboutTempoAndMissingPlugin() {
        var bridge = Bridge(new DawState {
            State = "Connected", Connections = [new DawConnection("Bitwig", 50123, "Connected")], ProjectBpm = 124, DawBpm = 120,
        });

        var result = await DawTool.Run(bridge, "status");

        Assert.False(result.IsError, Text(result));
        Assert.StartsWith("Connected: Bitwig port 50123.", Text(result));
        Assert.Contains("BPM_DIFFERS", Text(result));
        Assert.Contains("PLUGIN_NOT_INSTALLED", Text(result));
    }

    [Fact]
    public async Task Daw_Scan_NothingRunning_SaysWhatToDo() {
        var result = await DawTool.Run(Bridge(), "scan");

        Assert.StartsWith("No OpenUtau plugin is running in a DAW.", Text(result));
        Assert.Contains("\"servers\":[]", Text(result));
    }

    [Fact]
    public async Task Daw_Status_ShowsEachInstancesTrackAndWarnsAboutSharedAndMissingOnes() {
        var bridge = Bridge(new DawState {
            State = "Connected", ProjectBpm = 120, ClapInstalled = true, BindingSupported = true,
            Connections = [
                new DawConnection("A", 1, "Connected") { Binding = "bound", TrackId = "t1", TrackName = "Vocal", HostTrackName = "Lead Vox" },
                new DawConnection("B", 2, "Connected") { Binding = "bound", TrackId = "t1", TrackName = "Vocal" },
                new DawConnection("C", 3, "Connected") { Binding = "missing" },
            ],
        });

        var result = await DawTool.Run(bridge, "status");

        string text = Text(result);
        Assert.False(result.IsError, text);
        Assert.StartsWith("Connected: A port 1 plays Vocal, B port 2 plays Vocal, C port 3 (its track is gone).", text);
        Assert.Contains("\"track\":{\"id\":\"t1\",\"name\":\"Vocal\"}", text);
        Assert.Contains("\"dawTrack\":\"Lead Vox\"", text);
        Assert.Contains("DUPLICATE_BINDING", text);
        Assert.Contains("TRACK_MISSING", text);
        Assert.DoesNotContain("PLUGIN_NOT_INSTALLED", text);
    }

    [Fact]
    public async Task Daw_Bind_ResolvesTheTrackAndSendsItsId() {
        var bridge = Bridge().On(Methods.DawBind, _ => new DawState {
            State = "Connected", ProjectBpm = 120, ClapInstalled = true, BindingSupported = true,
            Connections = [new DawConnection("A", 55704, "Connected") { Binding = "bound", TrackId = "t1", TrackName = "Vocal" }],
        }, ContractsJson.Context.DawState);

        var result = await DawTool.Run(bridge, "bind", server: "55704", track: "#1");

        Assert.False(result.IsError, Text(result));
        Assert.StartsWith("'A' now plays Vocal", Text(result));
        var sent = bridge.Calls.Single(c => c.method == Methods.DawBind).@params.Deserialize(ContractsJson.Context.DawTargetParams)!;
        Assert.Equal(("55704", "t1"), (sent.Server, sent.TrackId));
    }

    [Fact]
    public async Task Daw_Bind_WithoutTrack_IsRefusedBeforeReachingOpenUtau() {
        var bridge = Bridge();

        var result = await DawTool.Run(bridge, "bind", server: "55704");

        Assert.True(result.IsError);
        Assert.DoesNotContain(bridge.Calls, c => c.method == Methods.DawBind);
    }
}
