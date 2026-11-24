using OpenUtau.Core;

using OpenUtau.Core.SignalChain;
using OpenUtau.Core.Ustx;

namespace OpenUtauMcp.Bridge.Jobs;

/// <summary>
/// export.custom: the bridge's own exporter over OpenUtau's RenderEngine. Renders
/// the chosen tracks over a tick range, then writes exactly that span (no leading silence: the file starts at
/// FromTick) as 16-bit, 24-bit or 32-bit float WAV, mono (left channel, like OpenUtau's stems) or stereo.
/// Cancellation cancels the render itself through the token source the engine checks.
/// </summary>
public sealed class CustomExport {
    public const int SampleRate = 44100;
    const int Chunk = 4096;

    // RenderEngine is internal to OpenUtau.Core: reached by reflection (the bridge only runs on the Core build it was
    // compiled for, so a signature change shows up at install time; at run time it becomes a clear RENDER_FAILED).
    static readonly Type? engineType = typeof(PlaybackManager).Assembly.GetType("OpenUtau.Core.Render.RenderEngine");

    delegate Tuple<WaveMix, List<Fader>> RenderMixdownFn(TaskScheduler uiScheduler, ref CancellationTokenSource? cancellation,
                                                       bool wait, bool applyMixFx, MixPlanner planner);

    CancellationTokenSource? renderCancellation;

    /// <summary>The engine's token source for the running render; cancelling it stops rendering phrases.</summary>
    public CancellationTokenSource? RenderCancellation => Volatile.Read(ref renderCancellation);

    /// <summary>
    /// Cancels the running render. Between two tracks the engine may already have disposed the previous token source;
    /// that render is over, and the job's own token stops the next one.
    /// </summary>
    public void CancelRender() {
        try {
            RenderCancellation?.Cancel();
        } catch (ObjectDisposedException) {
        }
    }

    /// <summary>
    /// Renders one track over the range in OpenUtau's export mode (wait = true: full quality, the same phrases as its own
    /// exporter; playback mode renders previews). A delegate with a real ref parameter lets the engine store its token
    /// source in <see cref="renderCancellation"/> as soon as it starts, so cancelling stops the render while it runs.
    /// </summary>
    public async Task<ISignalSource> RenderAsync(UProject project, int fromTick, int toTick, int trackNo, bool applyFaders,
                                                 double fromMs, double toMs, Func<IReadOnlyList<string>> errors, CancellationToken ct) {
        if (engineType is null) {
            throw new InvalidOperationException("This OpenUtau build has no RenderEngine to export with.");
        }
        object engine = Activator.CreateInstance(engineType, project, fromTick, toTick, trackNo, null, -1)
                        ?? throw new InvalidOperationException("Could not create OpenUtau's RenderEngine.");
        var method = engineType.GetMethod("RenderMixdown")
                     ?? throw new InvalidOperationException("OpenUtau's RenderEngine has no RenderMixdown.");
        var render = (RenderMixdownFn)Delegate.CreateDelegate(typeof(RenderMixdownFn), engine, method);
        using var link = ct.Register(CancelRender);
        var planner = new MixPlanner();
        Tuple<WaveMix, List<Fader>> result;
        try {
            result = await Task.Run(() => render(DocManager.Inst.MainScheduler, ref renderCancellation, true, applyFaders, planner),
                CancellationToken.None).ConfigureAwait(false);
        } catch (Exception e) when (!ct.IsCancellationRequested) {
            var cause = e is AggregateException a ? a.Flatten().InnerExceptions.FirstOrDefault() ?? e : e;
            var reported = errors();
            throw new BridgeException(Contracts.Errors.ErrorCodes.RenderFailed,
                reported.Count > 0 ? string.Join(" | ", reported) : $"Render failed: {cause.Message}");
        }
        ct.ThrowIfCancellationRequested();
        // Without faders: the track's raw source, as OpenUtau's own stems read it. A fader at unity still applies its
        // constant-power pan law (-3 dB per channel at center).
        ISignalSource mix = applyFaders || trackNo < 0 ? result.Item1 : planner.GetTrackSource(trackNo);
        long startFrame = (long)Math.Round(fromMs * SampleRate / 1000);
        long frames = Math.Max(0, (long)Math.Round(toMs * SampleRate / 1000) - startFrame);
        if (!mix.IsReady((int)(startFrame * 2), (int)(frames * 2))) {
            var reported = errors();
            throw new BridgeException(Contracts.Errors.ErrorCodes.RenderFailed,
                reported.Count > 0 ? string.Join(" | ", reported) : "OpenUtau did not render every phrase of the range.");
        }
        return mix;
    }

    /// <summary>Reads [fromMs, toMs) of the stereo sources (summed) and writes the WAV. Returns the frames written.</summary>
    public static long Write(string path, IReadOnlyList<ISignalSource> sources, double fromMs, double toMs, string format, int channels,
                             CancellationToken ct) {
        long startFrame = (long)Math.Round(fromMs * SampleRate / 1000);
        long frames = Math.Max(0, (long)Math.Round(toMs * SampleRate / 1000) - startFrame);
        int bits = format switch { "wav16" => 16, "wav24" => 24, "wav32f" => 32, _ => throw new ArgumentException($"Unknown format '{format}'.") };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(file);
        WriteHeader(writer, frames, channels, bits, float32: format == "wav32f");
        var mix = new float[Chunk * 2];
        var scratch = new float[Chunk * 2];
        for (long done = 0; done < frames; done += Chunk) {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(Chunk, frames - done);
            Array.Clear(mix, 0, n * 2);
            int position = (int)((startFrame + done) * 2);
            foreach (var source in sources) {
                Array.Clear(scratch, 0, n * 2);
                if (!source.IsReady(position, n * 2)) {
                    throw new InvalidOperationException("The render did not finish for the requested range.");
                }
                source.Mix(position, scratch, 0, n * 2);
                for (int i = 0; i < n * 2; i++) {
                    mix[i] += scratch[i];
                }
            }
            for (int f = 0; f < n; f++) {
                for (int c = 0; c < channels; c++) {
                    float sample = mix[f * 2 + (channels == 1 ? 0 : c)];
                    switch (bits) {
                        case 16:
                            writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
                            break;
                        case 24: {
                            int v = (int)Math.Round(Math.Clamp(sample, -1f, 1f) * 8388607);
                            writer.Write((byte)(v & 0xFF));
                            writer.Write((byte)((v >> 8) & 0xFF));
                            writer.Write((byte)((v >> 16) & 0xFF));
                            break;
                        }
                        default:
                            writer.Write(sample);
                            break;
                    }
                }
            }
        }
        return frames;
    }

    /// <summary>Canonical 44-byte header (PCM, or IEEE float with format tag 3).</summary>
    static void WriteHeader(BinaryWriter w, long frames, int channels, int bits, bool float32) {
        int blockAlign = channels * bits / 8;
        long dataBytes = frames * blockAlign;
        w.Write("RIFF"u8);
        w.Write((uint)(36 + dataBytes));
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16u);
        w.Write((ushort)(float32 ? 3 : 1));
        w.Write((ushort)channels);
        w.Write((uint)SampleRate);
        w.Write((uint)(SampleRate * blockAlign));
        w.Write((ushort)blockAlign);
        w.Write((ushort)bits);
        w.Write("data"u8);
        w.Write((uint)dataBytes);
    }
}
