using System.Reflection;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using OpenUtauMcp.Bridge.Dispatch;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>
/// Data OpenUtau derives asynchronously from the notes: phonemes (phonemizer worker) and render phrases with the
/// final pitch (phrase builder worker). Reads may wait for them (awaitMs); when they are not ready in time the
/// current data is returned with DERIVED_STALE.
/// </summary>
public static class DerivedAdapter {
    const int MaxAwaitMs = 15000;
    const int PollMs = 50;
    const int PitchInterval = 5;

    static readonly MethodInfo? waitPhraseSource =
        typeof(UVoicePart).GetMethod("WaitPhraseSource", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    public static async Task<NotesQueryResult> NotesQueryAsync(NotesQueryParams p, CancellationToken ct) {
        bool phonemes = p.Fields?.Contains("phonemes", StringComparer.OrdinalIgnoreCase) == true;
        Warning? stale = null;
        if (phonemes && !await WaitPhonemesAsync(p.PartId, p.AwaitMs, ct).ConfigureAwait(false)) {
            stale = new Warning(WarningCodes.DerivedStale,
                "OpenUtau has not finished phonemizing this part; phonemes may be outdated. Retry with a larger awaitMs.", p.PartId);
        }
        var result = await UiDispatcher.RunAsync(project => NotesAdapter.Query(project, p), ct).ConfigureAwait(false);
        return stale is null ? result : result with { Warnings = [stale] };
    }

    public static async Task<PhrasesQueryResult> PhrasesQueryAsync(PhrasesQueryParams p, CancellationToken ct) {
        if (p.StepTicks == 0) {
            p = p with { StepTicks = PitchInterval };  // omitted by a raw client
        }
        if (p.StepTicks < PitchInterval || p.StepTicks % PitchInterval != 0) {
            throw BridgeException.InvalidParams($"stepTicks must be a positive multiple of {PitchInterval}.", "stepTicks");
        }
        var series = p.Series is { Count: > 0 } s ? s : ["pitch"];
        foreach (var name in series) {
            if (!SeriesNames.Contains(name)) {
                throw BridgeException.InvalidParams($"Unknown series '{name}'. Use {string.Join(", ", SeriesNames)}.", "series");
            }
        }
        var warnings = new List<Warning>();
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Clamp(p.AwaitMs ?? 0, 0, MaxAwaitMs));
        bool ready = await WaitPhonemesAsync(p.PartId, p.AwaitMs, ct).ConfigureAwait(false);
        if (ready) {
            var part = await UiDispatcher.RunAsync(project => BridgeHost.Ids.ResolveVoicePart(project, p.PartId), ct).ConfigureAwait(false);
            var left = deadline - DateTime.UtcNow;
            ready = waitPhraseSource is not null && await Task.Run(
                () => (bool)waitPhraseSource.Invoke(part, [left > TimeSpan.Zero ? left : TimeSpan.Zero])!, ct).ConfigureAwait(false);
        }
        if (!ready) {
            warnings.Add(new Warning(WarningCodes.DerivedStale,
                "OpenUtau has not finished building this part's phrases; values may be outdated or missing. Retry with a larger awaitMs.",
                p.PartId));
        }
        return await UiDispatcher.RunAsync(project => Read(project, p, series, warnings), ct).ConfigureAwait(false);
    }

    static readonly string[] SeriesNames = ["pitch", "pitchBase", "dynamics", "tension", "breathiness", "voicing", "gender"];

    static PhrasesQueryResult Read(UProject project, PhrasesQueryParams p, IReadOnlyList<string> series, List<Warning> warnings) {
        var ids = BridgeHost.Ids;
        var part = ids.ResolveVoicePart(project, p.PartId);
        RenderPhrase[] phrases;
        lock (part) {
            phrases = part.renderPhrases.ToArray();
        }
        var byStart = part.notes.ToLookup(n => n.position + part.position);
        int step = p.StepTicks / PitchInterval;
        var list = new List<PhraseDto>();
        foreach (var phrase in phrases) {
            if (p.Range is { } r && (phrase.end <= r.FromTick || phrase.position >= r.ToTick)) {
                continue;
            }
            var values = new Dictionary<string, IReadOnlyList<float>>();
            foreach (var name in series) {
                float[]? raw = name switch {
                    "pitch" => phrase.pitches,
                    "pitchBase" => phrase.pitchesBeforeDeviation,
                    "dynamics" => phrase.dynamics,
                    "tension" => phrase.tension,
                    "breathiness" => phrase.breathiness,
                    "voicing" => phrase.voicing,
                    "gender" => phrase.gender,
                    _ => null,
                };
                if (raw is null) {
                    continue;
                }
                var sampled = new List<float>(raw.Length / step + 1);
                for (int i = 0; i < raw.Length; i += step) {
                    sampled.Add(MathF.Round(raw[i], 1));
                }
                values[name] = sampled;
            }
            list.Add(new PhraseDto {
                StartTick = phrase.position,
                EndTick = phrase.end,
                SeriesStartTick = phrase.position - phrase.leading,
                NoteIds = phrase.notes.SelectMany(rn => byStart[phrase.position + rn.position])  // RenderNote positions are phrase-relative
                   .Distinct().Select(n => ids.IdOf(n, part)).ToList(),
                Series = values,
            });
        }
        return new PhrasesQueryResult {
            PartId = ids.IdOf(part),
            PartRev = BridgeHost.Revisions.PartRev(part),
            Epoch = ids.Epoch,
            StepTicks = p.StepTicks,
            Phrases = list,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    /// <summary>Polls PhonemesUpToDate on the UI thread until true or awaitMs elapses. Never blocks the UI thread.</summary>
    static async Task<bool> WaitPhonemesAsync(string partId, int? awaitMs, CancellationToken ct) {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Clamp(awaitMs ?? 0, 0, MaxAwaitMs));
        while (true) {
            bool ready = await UiDispatcher.RunAsync(project => BridgeHost.Ids.ResolveVoicePart(project, partId).PhonemesUpToDate, ct)
                .ConfigureAwait(false);
            if (ready || DateTime.UtcNow >= deadline) {
                return ready;
            }
            await Task.Delay(PollMs, ct).ConfigureAwait(false);
        }
    }
}
