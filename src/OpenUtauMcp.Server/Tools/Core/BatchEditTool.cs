using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Server.Bridge;
using OpenUtauMcp.Server.Tools.Audio;
using OpenUtauMcp.Server.Units;

namespace OpenUtauMcp.Server.Tools.Core;

public sealed record BatchArgsInput(
    [property: Description("quantize: grid as a duration, e.g. \"1/16\" (default) or \"1/8\".")] string? Grid = null,
    [property: Description("transpose: semitones (-48..48).")] int? Semitones = null,
    [property: Description("add_breath (default \"AP\"), add_tail / remove_tail (default \"-\"): lyric of the added or removed notes.")] string? Lyric = null);

[McpServerToolType]
public static class BatchEditTool {
    [McpServerTool(Name = "ou_batch_edit", Title = "Run a built-in OpenUtau batch edit (separate undo step)", ReadOnly = false, Idempotent = false, OpenWorld = false, Destructive = true)]
    [Description("Runs an OpenUtau batch edit on the selected notes (selector with only part = whole part). Each run is its own undo step. Edits may create or remove notes: the result lists created/removed ids. load_rendered_pitch and refresh_real_curves run as jobs; past waitSeconds you get a job id: call again with job. reset_all needs confirm. Names: quantize, auto_legato, fix_overlap, transpose, add_breath, add_tail, remove_tail, add_phonetic_hints, remove_phonetic_hints, bake_pitch, load_rendered_pitch, refresh_real_curves, reset_pitch, reset_expressions, reset_vibratos, clear_vibratos, clear_timings, reset_aliases, reset_all. Example: {\"name\":\"quantize\",\"select\":{\"part\":\"Lead\"},\"args\":{\"grid\":\"1/16\"}}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge,
        [Description("Batch edit name (see the description). Omit when passing job.")] string? name = null,
        [Description("Notes to edit: {part, range?, ids?, phrases?, lyric?}.")] NoteSelector? select = null,
        [Description("Arguments: {grid} for quantize, {semitones} for transpose, {lyric} for add_breath/add_tail/remove_tail.")] BatchArgsInput? args = null,
        [Description("Seconds to wait for load_rendered_pitch / refresh_real_curves (default 60).")] int waitSeconds = 60,
        [Description("Job id of a running load_rendered_pitch / refresh_real_curves, to keep waiting for it.")] string? job = null,
        [Description("Required true for reset_all (resets pitch, vibrato, expressions, timings and aliases).")] bool confirm = false,
        McpServer? server = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            if (job is not null) {
                if (name is not null || select is not null) {
                    throw new ToolInputException(ToolInputException.InvalidParams, "Pass either job (to wait) or name and select, not both.");
                }
                var running = await bridge.JobGetAsync(job, ct).ConfigureAwait(false);
                return DescribeJob(await RenderTool.WaitAsync(bridge, running, waitSeconds, progress, ct).ConfigureAwait(false));
            }
            if (name is null || select is null) {
                throw new ToolInputException(ToolInputException.InvalidParams, "Give name and select (or job).");
            }
            if (!BatchNames.Sync.Contains(name) && !BatchNames.Async.Contains(name)) {
                throw new ToolInputException(ToolInputException.InvalidParams,
                    $"Unknown batch edit '{name}'. Use {string.Join(", ", BatchNames.Sync.Concat(BatchNames.Async))}.");
            }
            if (name == "reset_all") {
                await Confirmation.RequireAsync(server, confirm,
                    "reset_all resets pitch, vibrato, expressions, timings and aliases of the selected notes.",
                    "Ask the user, then pass confirm:true.", ct).ConfigureAwait(false);
            }
            var ctx = await ProjectContext.LoadAsync(bridge, ct).ConfigureAwait(false);
            var part = ctx.ResolvePart(select.Part);
            if (part.Type != "voice") {
                throw new ToolInputException(ErrorCodes.WrongKind, $"Part {part.Id} is an audio part and has no notes.");
            }
            List<string>? noteIds = null;
            if (select.Range is not null || select.Ids is { Length: > 0 } || select.Phrases is { Length: > 0 } || select.Lyric is not null) {
                var notes = (await bridge.AllNotesAsync(part.Id, null, ct).ConfigureAwait(false)).Notes;
                noteIds = NotesGetTool.Filter(notes, part, ctx.Time, select.Range, select.Ids, select.Phrases, select.Lyric)
                    .Select(n => n.Id).ToList();
                if (noteIds.Count == 0) {
                    return ToolResult.Ok("No notes matched; nothing changed.", new JsonObject { ["changed"] = false });
                }
            }

            if (BatchNames.Async.Contains(name)) {
                var started = await bridge.JobStartAsync(new JobStartParams {
                    Kind = name == BatchNames.LoadRenderedPitch ? JobKinds.BatchLoadRenderedPitch : JobKinds.BatchRefreshRealCurves,
                    PartId = part.Id,
                    NoteIds = noteIds,
                }, ct).ConfigureAwait(false);
                return DescribeJob(await RenderTool.WaitAsync(bridge, started, waitSeconds, progress, ct).ConfigureAwait(false));
            }

            var result = await bridge.BatchRunAsync(new BatchRunParams {
                Name = name,
                PartId = part.Id,
                NoteIds = noteIds,
                Grid = args?.Grid is { } grid ? Durations.Parse(grid) : null,
                Semitones = args?.Semitones,
                Lyric = args?.Lyric,
            }, ct).ConfigureAwait(false);
            var data = new JsonObject {
                ["changed"] = result.Changed,
                ["created"] = new JsonArray(result.Created.Select(id => (JsonNode?)id).ToArray()),
                ["removed"] = new JsonArray(result.Removed.Select(id => (JsonNode?)id).ToArray()),
            };
            string scope = noteIds is null ? "all notes" : $"{noteIds.Count} notes";
            string summary = !result.Changed
                ? $"{name} on {scope} of {part.Id} ({part.Name}) changed nothing."
                : $"{name} applied to {scope} of {part.Id} ({part.Name}): {result.Created.Count} notes created, {result.Removed.Count} removed. " +
                  "It is a separate undo step.";
            return ToolResult.Ok(summary, data, result.Warnings,
                new JsonObject { ["epoch"] = result.Epoch, ["partRev"] = new JsonObject { [result.PartId] = result.PartRev } });
        });

    static CallToolResult DescribeJob(JobDto dto) {
        var data = new JsonObject { ["job"] = dto.JobId, ["kind"] = dto.Kind, ["status"] = dto.State };
        if (dto.Progress?.Info is { } info) {
            data["progress"] = info;
        }
        if (dto.Result is { } r) {
            data["changed"] = r.Changed;
            if (r.PartId is { } partId && r.PartRev is int rev) {
                data["partRev"] = new JsonObject { [partId] = rev };
            }
        }
        return dto.State switch {
            JobStates.Succeeded when dto.Result?.Changed == true => ToolResult.Ok(
                $"{Name(dto.Kind)} finished and wrote its result to the part. It is a separate undo step.", data,
                [new Warning(WarningCodes.SeparateUndoStep,
                    "OpenUtau records each batch edit as its own history item: one more Ctrl+Z (or ou_history undo) to revert it.",
                    dto.Result.PartId)]),
            JobStates.Succeeded => ToolResult.Ok($"{Name(dto.Kind)} finished; the renderer returned nothing to write.", data),
            JobStates.Running => ToolResult.Ok($"{Name(dto.Kind)} is still running (job {dto.JobId}). Call ou_batch_edit with job to keep waiting.", data),
            JobStates.Cancelled => ToolResult.Ok($"Job {dto.JobId} was cancelled.", data),
            _ => ToolResult.Error(dto.Error?.Code ?? ErrorCodes.BatchFailed, dto.Error?.Message ?? "The batch edit failed.",
                "The track's renderer or voicebank could not do it (see the message). load_rendered_pitch needs a DiffSinger voicebank with a working pitch model (dspitch); refresh_real_curves needs a variance model.", data),
        };
    }

    static string Name(string kind) => kind == JobKinds.BatchLoadRenderedPitch ? BatchNames.LoadRenderedPitch : BatchNames.RefreshRealCurves;
}
