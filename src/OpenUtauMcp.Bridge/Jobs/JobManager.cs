using System.Text.Json.Nodes;
using OpenUtau.Core;
using OpenUtau.Core.SignalChain;
using OpenUtau.Core.Ustx;
using OpenUtauMcp.Bridge.Adapters;
using OpenUtauMcp.Bridge.Dispatch;
using OpenUtauMcp.Contracts;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Protocol;
using Serilog;

namespace OpenUtauMcp.Bridge.Jobs;

/// <summary>
/// Long operations. One job at a time. Export jobs call OpenUtau's own
/// exporter, which swallows its exceptions and only posts ErrorMessageNotifications; after the render task
/// ends, a marker is posted to the (FIFO) UI queue so every notification posted before it has been seen,
/// then the expected files are checked one by one. The official export cannot be cancelled: cancel only
/// stops waiting, and the bridge stays busy until the render really ends.
/// </summary>
public sealed class JobManager {
    static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);
    static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    const int WavHeaderBytes = 44;

    readonly object gate = new();
    readonly Dictionary<string, Job> jobs = new();
    Job? busy;
    long nextId = 1;

    sealed class Job(string id, string kind, string path) {
        public string Id { get; } = id;
        public string Kind { get; } = kind;
        public string Path { get; } = path;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string State { get; set; } = JobStates.Running;
        public JobProgress? Progress { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public JobResult? Result { get; set; }
        public JobError? Error { get; set; }
        public DateTime LastProgressEvent { get; set; }
        public CancellationTokenSource? Cancellation { get; init; }
        /// <summary>Stops work that does not watch <see cref="Cancellation"/> (the render engine's own token).</summary>
        public Action? CancelWork { get; set; }

        public JobDto ToDto() => new() {
            JobId = Id, Kind = Kind, State = State, Progress = Progress, StartedAt = StartedAt,
            EndedAt = EndedAt, Result = Result, Error = Error,
        };
    }

    public JobManager(Events.EventHub events) => events.Progress += OnProgress;

    /// <summary>Throws BUSY_JOB while an export is still rendering (it reads the live project).</summary>
    public void EnsureIdle() {
        lock (gate) {
            if (busy is { } job) {
                throw new BridgeException(ErrorCodes.BusyJob, $"Job {job.Id} ({job.Kind}) is still running.",
                    new JsonObject { ["jobId"] = job.Id });
            }
        }
    }

    public IReadOnlyList<JobDto> Recent() {
        lock (gate) {
            Prune();
            return jobs.Values.OrderBy(j => j.StartedAt).Select(j => j.ToDto()).ToList();
        }
    }

    public JobDto Start(JobStartParams p) {
        if (p.Kind is JobKinds.BatchLoadRenderedPitch or JobKinds.BatchRefreshRealCurves) {
            return StartBatch(p);
        }
        if (p.Kind == JobKinds.ExportCustom) {
            return StartCustomExport(p);
        }
        if (p.Kind is not (JobKinds.ExportMixdown or JobKinds.ExportStems)) {
            throw new BridgeException(ErrorCodes.CapabilityUnavailable, $"Unknown job kind '{p.Kind}'.");
        }
        if (string.IsNullOrWhiteSpace(p.Path) || !System.IO.Path.IsPathFullyQualified(p.Path)) {
            throw BridgeException.InvalidParams("'path' must be an absolute path.", "path");
        }
        if (!string.Equals(System.IO.Path.GetExtension(p.Path), ".wav", StringComparison.OrdinalIgnoreCase)) {
            throw new BridgeException(ErrorCodes.UnsupportedFormat, "Exports are WAV files: the path must end in .wav.");
        }
        Job job;
        lock (gate) {
            Prune();
            if (busy is { } running) {
                throw new BridgeException(ErrorCodes.BusyJob, $"Job {running.Id} ({running.Kind}) is still running.",
                    new JsonObject { ["jobId"] = running.Id });
            }
            job = new Job("j" + nextId++, p.Kind, System.IO.Path.GetFullPath(p.Path!));
            jobs[job.Id] = job;
            busy = job;
        }
        _ = Task.Run(() => RunExportAsync(job));
        return job.ToDto();
    }

    public JobDto Get(string jobId) => Find(jobId).ToDto();

    public async Task<JobDto> WaitAsync(string jobId, int timeoutMs, CancellationToken ct) {
        var job = Find(jobId);
        try {
            await job.Done.Task.WaitAsync(TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 0, 600_000)), ct).ConfigureAwait(false);
        } catch (TimeoutException) {
            // Still running: the caller gets the current state.
        }
        return job.ToDto();
    }

    public JobDto Cancel(string jobId) {
        var job = Find(jobId);
        lock (gate) {
            if (job.State == JobStates.Running && job.Cancellation is { } cts) {
                // Batch jobs stop the inference; a result already posted to OpenUtau stays (undo it with history.undo).
                // Custom exports stop rendering and delete the partial file.
                cts.Cancel();
                job.CancelWork?.Invoke();
                Finish(job, JobStates.Cancelled, null, new JobError(ErrorCodes.Cancelled, job.Kind == JobKinds.ExportCustom
                    ? "Cancelled: rendering stopped and no file was kept."
                    : "Cancelled. If OpenUtau already wrote the result, undo it with history.undo."));
            } else if (job.State == JobStates.Running) {
                Finish(job, JobStates.Cancelled, null, new JobError(ErrorCodes.Cancelled,
                    "Stopped waiting. OpenUtau's export cannot be interrupted and keeps rendering until it ends."));
            }
        }
        return job.ToDto();
    }

    static readonly string[] customFormats = ["wav16", "wav24", "wav32f"];

    JobDto StartCustomExport(JobStartParams p) {
        if (string.IsNullOrWhiteSpace(p.Path) || !System.IO.Path.IsPathFullyQualified(p.Path)) {
            throw BridgeException.InvalidParams("'path' must be an absolute path.", "path");
        }
        if (!string.Equals(System.IO.Path.GetExtension(p.Path), ".wav", StringComparison.OrdinalIgnoreCase)) {
            throw new BridgeException(ErrorCodes.UnsupportedFormat, "Exports are WAV files: the path must end in .wav.");
        }
        if (p.Format is { } format && !customFormats.Contains(format)) {
            throw BridgeException.InvalidParams($"Unknown format '{format}'. Use wav16, wav24 or wav32f.", "format");
        }
        if (p.Channels is not (null or 1 or 2)) {
            throw BridgeException.InvalidParams("'channels' is 1 or 2.", "channels");
        }
        if (p.FromTick is < 0 || p.FromTick is int f && p.ToTick is int t && t <= f) {
            throw BridgeException.InvalidParams("The range must start at 0 or later and end after it starts.", "fromTick");
        }
        var export = new CustomExport();
        Job job;
        lock (gate) {
            Prune();
            if (busy is { } running) {
                throw new BridgeException(ErrorCodes.BusyJob, $"Job {running.Id} ({running.Kind}) is still running.",
                    new JsonObject { ["jobId"] = running.Id });
            }
            job = new Job("j" + nextId++, p.Kind, System.IO.Path.GetFullPath(p.Path!)) { Cancellation = new CancellationTokenSource() };
            job.CancelWork = export.CancelRender;
            jobs[job.Id] = job;
            busy = job;
        }
        _ = Task.Run(() => RunCustomExportAsync(job, p, export));
        return job.ToDto();
    }

    async Task RunCustomExportAsync(Job job, JobStartParams p, CustomExport export) {
        var written = new List<(string? track, string file)>();
        var token = job.Cancellation!.Token;
        try {
            var plan = await UiDispatcher.RunAsync(project => {
                UndoScope.EnsureNoUserEdit();
                List<UTrack> tracks;
                if (p.TrackIds is { Count: > 0 } ids) {
                    tracks = ids.Select(id => BridgeHost.Ids.ResolveTrack(project, id)).Distinct().ToList();
                    if ((p.ApplyFaders ?? true) && tracks.FirstOrDefault(t => t.Muted) is { } muted) {
                        // The fader of a muted track is silent, so the file would be all zeros.
                        throw new BridgeException(ErrorCodes.TrackMuted,
                            $"Track '{muted.TrackName}' is muted, so with faders applied it would export silence. Unmute it or pass applyFaders:false.",
                            new JsonObject { ["trackId"] = BridgeHost.Ids.IdOf(muted) });
                    }
                } else {
                    tracks = project.tracks.Where(t => !t.Muted && project.parts.Any(x => x.trackNo == t.TrackNo)).ToList();
                }
                if (tracks.Count == 0) {
                    throw new BridgeException(ErrorCodes.RenderFailed, "There is no track to export (all muted or empty).");
                }
                int from = p.FromTick ?? 0;
                int to = p.ToTick ?? project.EndTick;
                if (to <= from) {
                    throw BridgeException.InvalidParams("The project has nothing to export in that range.", "toTick");
                }
                var targets = tracks.Select(t => (name: t.TrackName, no: t.TrackNo,
                    file: p.PerTrack ? PathManager.Inst.GetExportPath(job.Path, t) : job.Path)).ToList();
                return (project, from, to, fromMs: project.timeAxis.TickPosToMsPos(from), toMs: project.timeAxis.TickPosToMsPos(to), targets);
            }, CancellationToken.None).ConfigureAwait(false);

            string format = p.Format ?? "wav24";
            int channels = p.Channels ?? 2;
            bool faders = p.ApplyFaders ?? true;
            int done = 0;
            // One render per track: the engine renders a single track or all of them, and a mix of some
            // tracks (or one without faders) must not let muted tracks through.
            long errorSeq = BridgeHost.Events.ErrorSeq;
            var sources = new List<(string name, string file, ISignalSource source)>();
            foreach (var (name, no, file) in plan.targets) {
                token.ThrowIfCancellationRequested();
                SetProgress(job, 100.0 * done / plan.targets.Count, $"rendering {name}");
                var source = await export.RenderAsync(plan.project, plan.from, plan.to, no, faders, plan.fromMs, plan.toMs,
                    () => BridgeHost.Events.ErrorsSince(errorSeq), token).ConfigureAwait(false);
                sources.Add((name, file, source));
                done++;
            }
            SetProgress(job, 100, "writing");
            if (p.PerTrack) {
                foreach (var (name, file, source) in sources) {
                    written.Add((name, file));
                    CustomExport.Write(file, [source], plan.fromMs, plan.toMs, format, channels, token);
                }
            } else {
                written.Add((null, job.Path));
                CustomExport.Write(job.Path, sources.Select(s => s.source).ToList(), plan.fromMs, plan.toMs, format, channels, token);
            }
            Complete(job, new JobResult { Files = written.Select(w => Describe(new FileInfo(w.file), w.track)).ToList() }, null);
        } catch (Exception e) when (e is OperationCanceledException || token.IsCancellationRequested) {
            DeletePartial(written);
            Complete(job, null, new JobError(ErrorCodes.Cancelled, "Cancelled: rendering stopped and no file was kept."));
        } catch (BridgeException e) {
            DeletePartial(written);
            Complete(job, null, new JobError(e.Code, e.Message));
        } catch (Exception e) {
            Log.Error(e, $"{BridgeHost.LogPrefix} job {job.Id} failed");
            DeletePartial(written);
            Complete(job, null, new JobError(ErrorCodes.RenderFailed, $"The export failed (no file was kept): {e.Message}"));
        }
    }

    /// <summary>A failed or cancelled custom export keeps no file: a truncated WAV would look valid to other programs.</summary>
    static void DeletePartial(List<(string? track, string file)> written) {
        foreach (var (_, file) in written) {
            try {
                File.Delete(file);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                Log.Warning(e, $"{BridgeHost.LogPrefix} could not delete the partial export {file}");
            }
        }
    }

    void SetProgress(Job job, double pct, string info) {
        lock (gate) {
            job.Progress = new JobProgress(Math.Round(pct, 1), info);
        }
    }

    JobDto StartBatch(JobStartParams p) {
        if (string.IsNullOrEmpty(p.PartId)) {
            throw BridgeException.InvalidParams("Batch jobs need 'partId'.", "partId");
        }
        Job job;
        lock (gate) {
            Prune();
            if (busy is { } running) {
                throw new BridgeException(ErrorCodes.BusyJob, $"Job {running.Id} ({running.Kind}) is still running.",
                    new JsonObject { ["jobId"] = running.Id });
            }
            job = new Job("j" + nextId++, p.Kind, "") { Cancellation = new CancellationTokenSource() };
            jobs[job.Id] = job;
            busy = job;
        }
        _ = Task.Run(() => RunBatchAsync(job, p));
        return job.ToDto();
    }

    /// <summary>
    /// Async batch edits: RunAsync blocks this thread during inference and posts the
    /// result to the UI in its own undo group before returning. A marker posted after it (FIFO) proves the result
    /// has been applied; errors are ErrorMessageNotifications seen in that window; "changed" = the part's rev moved.
    /// </summary>
    async Task RunBatchAsync(Job job, JobStartParams p) {
        try {
            long errorSeq = BridgeHost.Events.ErrorSeq;
            var (project, part, notes, revBefore) = await UiDispatcher.RunAsync(project => {
                UndoScope.EnsureNoUserEdit();
                var part = BridgeHost.Ids.ResolveVoicePart(project, p.PartId!);
                var notes = BatchAdapter.Notes(project, part, p.NoteIds);
                // Checked here so OpenUtau does not pop its own error dialog for an unsupported renderer.
                var renderer = project.tracks[part.trackNo].RendererSettings?.Renderer;
                bool supported = job.Kind == JobKinds.BatchLoadRenderedPitch
                    ? renderer?.SupportsRenderPitch == true
                    : renderer?.SupportsRealCurve == true;
                if (!supported) {
                    throw new BridgeException(ErrorCodes.BatchFailed, job.Kind == JobKinds.BatchLoadRenderedPitch
                        ? "The track's renderer cannot generate a pitch curve (DiffSinger or ENUNU voicebanks can)."
                        : "The track's renderer does not produce real curves.");
                }
                EditAdapter.BackupBeforeFirstEdit(project);
                return (project, part, notes, BridgeHost.Revisions.PartRev(part));
            }, CancellationToken.None).ConfigureAwait(false);

            var edit = BatchAdapter.Create(
                job.Kind == JobKinds.BatchLoadRenderedPitch ? BatchNames.LoadRenderedPitch : BatchNames.RefreshRealCurves, null, null, null);
            var token = job.Cancellation!.Token;
            edit.RunAsync(project, part, notes, DocManager.Inst, (current, total) => {
                lock (gate) {
                    job.Progress = new JobProgress(total > 0 ? Math.Round(100.0 * current / total, 1) : null, $"{current}/{total} phrases");
                }
            }, token);
            // Everything RunAsync posted to the UI runs before this marker.
            int revAfter = await UiDispatcher.RunAsync(_ => BridgeHost.Revisions.PartRev(part), CancellationToken.None).ConfigureAwait(false);

            var errors = BridgeHost.Events.ErrorsSince(errorSeq);
            if (errors.Count > 0) {
                Complete(job, null, new JobError(ErrorCodes.BatchFailed, string.Join(" | ", errors)));
                return;
            }
            Complete(job, new JobResult {
                Changed = revAfter != revBefore, PartId = BridgeHost.Ids.IdOf(part), PartRev = revAfter,
            }, null);
        } catch (OperationCanceledException) {
            Complete(job, null, new JobError(ErrorCodes.Cancelled, "Cancelled."));
        } catch (BridgeException e) {
            Complete(job, null, new JobError(e.Code, e.Message));
        } catch (Exception e) {
            Log.Error(e, $"{BridgeHost.LogPrefix} job {job.Id} failed");
            Complete(job, null, new JobError(ErrorCodes.BatchFailed, $"The batch edit failed: {e.Message}"));
        }
    }

    async Task RunExportAsync(Job job) {
        try {
            long errorSeq = BridgeHost.Events.ErrorSeq;
            var started = DateTime.UtcNow.AddSeconds(-2);
            var (render, expected) = await UiDispatcher.RunAsync(project => {
                UndoScope.EnsureNoUserEdit();
                if (job.Kind == JobKinds.ExportMixdown) {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(job.Path)!);
                    return (PlaybackManager.Inst.RenderMixdown(project, job.Path), new List<(string?, string)> { (null, job.Path) });
                }
                var files = ExpectedStems(project, job.Path);
                return (PlaybackManager.Inst.RenderToFiles(project, job.Path), files);
            }, CancellationToken.None).ConfigureAwait(false);

            await render.ConfigureAwait(false);
            // Notifications posted by the render (errors, progress) run before this marker.
            await UiDispatcher.RunAsync(_ => 0, CancellationToken.None).ConfigureAwait(false);

            var errors = BridgeHost.Events.ErrorsSince(errorSeq);
            if (errors.Count > 0) {
                Complete(job, null, new JobError(ErrorCodes.RenderFailed, string.Join(" | ", errors)));
                return;
            }
            if (expected.Count == 0) {
                Complete(job, null, new JobError(ErrorCodes.RenderFailed, "There is no unmuted voice track with notes to export."));
                return;
            }
            var produced = new List<ExportedFile>();
            foreach (var (track, file) in expected) {
                var info = new FileInfo(file);
                if (!info.Exists || info.LastWriteTimeUtc < started || info.Length <= WavHeaderBytes) {
                    Complete(job, null, new JobError(ErrorCodes.RenderFailed, $"OpenUtau did not produce {file}."));
                    return;
                }
                produced.Add(Describe(info, track));
            }
            Complete(job, new JobResult { Files = produced }, null);
        } catch (BridgeException e) {
            Complete(job, null, new JobError(e.Code, e.Message));
        } catch (Exception e) {
            Log.Error(e, $"{BridgeHost.LogPrefix} job {job.Id} failed");
            Complete(job, null, new JobError(ErrorCodes.Internal, $"Unexpected error: {e.Message}"));
        }
    }

    /// <summary>Mirrors PlaybackManager.RenderToFiles: one file per unmuted track that renders audio.</summary>
    static List<(string?, string)> ExpectedStems(UProject project, string prefix) {
        var files = new List<(string?, string)>();
        foreach (var track in project.tracks) {
            bool hasNotes = project.parts.OfType<UVoicePart>().Any(p => p.trackNo == track.TrackNo && p.notes.Count > 0);
            if (!track.Muted && hasNotes) {
                files.Add((track.TrackName, PathManager.Inst.GetExportPath(prefix, track)));
            }
        }
        return files;
    }

    static ExportedFile Describe(FileInfo file, string? track) {
        int channels = 0, sampleRate = 0, bits = 16;
        try {
            using var stream = file.OpenRead();
            Span<byte> header = stackalloc byte[WavHeaderBytes];
            if (stream.Read(header) == WavHeaderBytes) {
                channels = BitConverter.ToInt16(header[22..24]);
                sampleRate = BitConverter.ToInt32(header[24..28]);
                bits = BitConverter.ToInt16(header[34..36]);
            }
        } catch (IOException) {
        }
        double bytesPerSecond = (double)sampleRate * channels * bits / 8;
        return new ExportedFile {
            Path = file.FullName,
            Bytes = file.Length,
            Seconds = bytesPerSecond > 0 ? Math.Round((file.Length - WavHeaderBytes) / bytesPerSecond, 2) : 0,
            Channels = channels,
            SampleRate = sampleRate,
            Track = track,
        };
    }

    void Complete(Job job, JobResult? result, JobError? error) {
        lock (gate) {
            if (job.State == JobStates.Running) {
                Finish(job, error is null ? JobStates.Succeeded : JobStates.Failed, result, error);
            }
            if (ReferenceEquals(busy, job)) {
                busy = null;
            }
        }
    }

    // Caller holds the lock.
    void Finish(Job job, string state, JobResult? result, JobError? error) {
        job.State = state;
        job.Result = result;
        job.Error = error;
        job.EndedAt = DateTimeOffset.Now;
        job.Done.TrySetResult();
        BridgeHost.Events.Publish(EventTopics.JobDone,
            System.Text.Json.JsonSerializer.SerializeToNode(job.ToDto(), ContractsJson.Context.JobDto));
    }

    void OnProgress(string info) {
        lock (gate) {
            if (busy is not { State: JobStates.Running } job) {
                return;
            }
            job.Progress = new JobProgress(null, info);
            if (DateTime.UtcNow - job.LastProgressEvent < ProgressInterval) {
                return;
            }
            job.LastProgressEvent = DateTime.UtcNow;
            BridgeHost.Events.Publish(EventTopics.JobProgress, new JsonObject { ["jobId"] = job.Id, ["info"] = info });
        }
    }

    Job Find(string jobId) {
        lock (gate) {
            return jobs.TryGetValue(jobId, out var job) ? job : throw BridgeException.NotFound("job", jobId, "unknown");
        }
    }

    // Caller holds the lock.
    void Prune() {
        foreach (var old in jobs.Values.Where(j => j.EndedAt is { } end && DateTimeOffset.Now - end > Retention && !ReferenceEquals(j, busy)).ToList()) {
            jobs.Remove(old.Id);
        }
    }
}
