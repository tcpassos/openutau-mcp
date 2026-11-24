using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Server.Bridge;
using OpenUtauMcp.Server.Safety;

namespace OpenUtauMcp.Server.Tools.Audio;

[McpServerToolType]
public static class RenderTool {
    static readonly string[] wav = [".wav"];
    static readonly TimeSpan PollSlice = TimeSpan.FromSeconds(2);

    [McpServerTool(Name = "ou_render", Title = "Export audio", ReadOnly = false, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Exports WAV: mixdown (one file) or stems (one file per track; path is the name prefix). Without range/tracks/format/channels/applyFaders it uses OpenUtau's own exporter (16-bit, whole project); with any of them, the bridge's exporter: a bar range, chosen tracks, wav16/wav24/wav32f, mono or stereo, faders on or off, and cancel really stops it. Past waitSeconds it returns a job id (action status or wait). Example: {\"action\":\"export\",\"path\":\"C:/Songs/chorus.wav\",\"range\":{\"bars\":\"17-24\"},\"tracks\":[\"Lead\"],\"format\":\"wav32f\"}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge, PathPolicy paths,
        [Description("export | status | wait | cancel")] string action,
        [Description("mixdown (default) | stems")] string scope = "mixdown",
        [Description("Absolute .wav path (export).")] string? path = null,
        [Description("Job id (status, wait, cancel).")] string? job = null,
        [Description("Seconds to wait for the export to finish.")] int waitSeconds = 120,
        [Description("Allow replacing an existing file.")] bool overwrite = false,
        [Description("Only this span; the file starts at its beginning.")] Core.RangeInput? range = null,
        [Description("Tracks to export (id, name or #n; default: unmuted tracks).")] string[]? tracks = null,
        [Description("wav16 | wav24 (default for custom) | wav32f")] string? format = null,
        [Description("1 or 2 (default 2).")] int? channels = null,
        [Description("Apply volume, pan and track FX (default true).")] bool? applyFaders = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            JobDto dto;
            switch (action) {
                case "export":
                    if (path is null) {
                        throw new ToolInputException(ToolInputException.InvalidParams, "'path' is required for export.");
                    }
                    if (scope is not ("mixdown" or "stems")) {
                        throw new ToolInputException(ToolInputException.InvalidParams, $"Unknown scope '{scope}'. Use mixdown or stems.");
                    }
                    bool custom = range is not null || tracks is { Length: > 0 } || format is not null || channels is not null || applyFaders is not null;
                    var status = await bridge.StatusAsync(ct).ConfigureAwait(false);
                    // Stem file names are derived from the prefix, so overwrite is only checked for mixdown.
                    string target = paths.Check(path, status.Project.FilePath, wav, write: true, overwrite || scope == "stems");
                    JobStartParams start;
                    if (custom) {
                        var ctx = await ProjectContext.LoadAsync(bridge, ct).ConfigureAwait(false);
                        int? from = null, to = null;
                        if (range is not null) {
                            var whole = new PartDto { Id = "", Name = "", TrackId = "", Type = "voice", Pos = 0, Dur = int.MaxValue / 2 };
                            (from, to) = Core.NotesGetTool.ResolveRange(ctx.Time, range, whole);
                            if (range.To is null && range.Bars is null) {
                                to = null;   // open end = end of the project
                            }
                        }
                        var chosen = tracks?.Select(ctx.ResolveTrack).ToList();
                        if ((applyFaders ?? true) && chosen?.FirstOrDefault(t => t.Mute) is { } muted) {
                            throw new ToolInputException(ErrorCodes.TrackMuted,
                                $"Track '{muted.Name}' is muted, so with faders applied it would export silence.",
                                "Unmute it (ou_tracks_edit mix) or pass applyFaders:false.");
                        }
                        start = new JobStartParams {
                            Kind = JobKinds.ExportCustom, Path = target, FromTick = from, ToTick = to,
                            TrackIds = chosen?.Select(t => t.Id).ToList(),
                            Format = format, Channels = channels, ApplyFaders = applyFaders, PerTrack = scope == "stems",
                        };
                    } else {
                        start = new JobStartParams { Kind = scope == "stems" ? JobKinds.ExportStems : JobKinds.ExportMixdown, Path = target };
                    }
                    dto = await bridge.JobStartAsync(start, ct).ConfigureAwait(false);
                    dto = await WaitAsync(bridge, dto, waitSeconds, progress, ct).ConfigureAwait(false);
                    break;
                case "status":
                    dto = await bridge.JobGetAsync(RequireJob(job), ct).ConfigureAwait(false);
                    break;
                case "wait":
                    dto = await bridge.JobGetAsync(RequireJob(job), ct).ConfigureAwait(false);
                    dto = await WaitAsync(bridge, dto, waitSeconds, progress, ct).ConfigureAwait(false);
                    break;
                case "cancel":
                    dto = await bridge.JobCancelAsync(RequireJob(job), ct).ConfigureAwait(false);
                    break;
                default:
                    throw new ToolInputException(ToolInputException.InvalidParams, $"Unknown action '{action}'. Use export, status, wait or cancel.");
            }
            return Describe(dto);
        });

    internal static async Task<JobDto> WaitAsync(IBridgeClient bridge, JobDto dto, int waitSeconds, IProgress<ProgressNotificationValue>? progress,
                                        CancellationToken ct) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, 600));
        int tick = 0;
        while (dto.State == JobStates.Running && DateTime.UtcNow < deadline) {
            var slice = deadline - DateTime.UtcNow < PollSlice ? deadline - DateTime.UtcNow : PollSlice;
            dto = await bridge.JobWaitAsync(dto.JobId, slice, ct).ConfigureAwait(false);
            progress?.Report(new ProgressNotificationValue { Progress = ++tick, Message = dto.Progress?.Info ?? "Working..." });
        }
        return dto;
    }

    static CallToolResult Describe(JobDto dto) {
        var data = new JsonObject { ["job"] = dto.JobId, ["kind"] = dto.Kind, ["status"] = dto.State };
        if (dto.Progress?.Info is { } info) {
            data["progress"] = info;
        }
        if (dto.Result is { } result) {
            data["files"] = new JsonArray(result.Files.Select(f => (JsonNode?)new JsonObject {
                ["path"] = f.Path, ["track"] = f.Track, ["seconds"] = f.Seconds, ["channels"] = f.Channels, ["bytes"] = f.Bytes,
            }).ToArray());
        }
        return dto.State switch {
            JobStates.Succeeded => ToolResult.Ok($"Exported {dto.Result?.Files.Count ?? 0} file(s).", data),
            JobStates.Running => ToolResult.Ok($"Still rendering (job {dto.JobId}). Check again with action=wait.", data),
            JobStates.Cancelled => ToolResult.Ok(dto.Kind == JobKinds.ExportCustom
                ? $"Cancelled job {dto.JobId}: rendering stopped and no file was kept."
                : $"Stopped waiting for job {dto.JobId}; OpenUtau finishes the export in the background.", data),
            _ => ToolResult.Error(dto.Error?.Code ?? "RENDER_FAILED", dto.Error?.Message ?? "The export failed.",
                dto.Error?.Code is null or "RENDER_FAILED" or "IO_ERROR"
                    ? "Check that the output file is writable and that the tracks have singers and are not all muted (ou_project_get), then retry."
                    : null, data),
        };
    }

    static string RequireJob(string? job) =>
        job ?? throw new ToolInputException(ToolInputException.InvalidParams, "'job' is required (the job id returned by export).");
}
