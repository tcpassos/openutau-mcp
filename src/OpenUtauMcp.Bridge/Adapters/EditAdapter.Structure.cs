using OpenUtau.Api;
using OpenUtau.Core;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Ops;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>Track, part and project-level ops (F1b).</summary>
public static partial class EditAdapter {
    static readonly int[] beatUnits = [1, 2, 4, 8, 16, 32];

    static Action PlanTrackUpdate(PlanContext ctx, TrackUpdateOp t, int index) {
        string at = $"ops[{index}]";
        ctx.ValidateRef(t.TrackId, "track", index);
        USinger? singer = t.SingerId is { } singerId ? Catalog.FindSinger(singerId) : null;
        PhonemizerFactory? phonemizer = null;
        if (t.Phonemizer is { } phonemizerId) {
            phonemizer = FindPhonemizer(phonemizerId, $"{at}.phonemizer");
        }
        if (t.Renderer is { } renderer && Renderers.GetOrCreate(renderer) is null) {
            throw BridgeException.InvalidParams($"Unknown renderer '{renderer}'.", $"{at}.renderer");
        }
        if (t.Position is < 0) {
            throw BridgeException.OutOfRange($"{at}.position", t.Position.Value, 0, int.MaxValue);
        }
        if (t.Name is { Length: 0 }) {
            throw BridgeException.InvalidParams("Track name cannot be empty.", $"{at}.name");
        }
        return () => {
            var project = ctx.Project;
            var doc = PlanContext.Doc;
            var track = ctx.Track(t.TrackId);
            if (t.Name is { } name && name != track.TrackName) {
                doc.ExecuteCmd(new RenameTrackCommand(project, track, name));
            }
            if (t.Color is { } color && color != track.TrackColor) {
                doc.ExecuteCmd(new ChangeTrackColorCommand(project, track, color));
            }
            if (singer is not null) {
                ApplySinger(project, track, singer);
                ctx.Warnings.Add(new Warning(WarningCodes.DialogMayOpen,
                    "If the new singer has different voice colors, OpenUtau may ask to remap them the next time the project is opened.",
                    BridgeHost.Ids.IdOf(track)));
            }
            if (phonemizer is not null) {
                doc.ExecuteCmd(new TrackChangePhonemizerCommand(project, track, phonemizer.Create()));
            }
            if (t.Renderer is { } renderer) {
                if (track.Singer is { Found: true } s && !Renderers.GetSupportedRenderers(s.SingerType).Contains(renderer)) {
                    throw new InvalidOperationException($"renderer {renderer} does not support {s.SingerType} singers");
                }
                doc.ExecuteCmd(new TrackChangeRenderSettingCommand(project, track, new URenderSettings { renderer = renderer }));
            }
            if (t.Position is int position) {
                doc.ExecuteCmd(new SetTrackNoCommand(project, track, Math.Min(position, project.tracks.Count - 1)));
            }
        };
    }

    static Action PlanTrackRemove(PlanContext ctx, TrackRemoveOp r, int index) {
        ctx.ValidateRef(r.TrackId, "track", index);
        return () => {
            var project = ctx.Project;
            var track = ctx.Track(r.TrackId);
            var parts = project.parts.Where(p => p.trackNo == track.TrackNo).ToList();
            ctx.Removed.Add(BridgeHost.Ids.IdOf(track));
            ctx.Removed.AddRange(parts.Select(p => BridgeHost.Ids.IdOf(p)));
            PlanContext.Doc.ExecuteCmd(new RemoveTrackCommand(project, track));
        };
    }

    static Action PlanPartUpdate(PlanContext ctx, PartUpdateOp u, int index) {
        string at = $"ops[{index}]";
        ctx.ValidateRef(u.PartId, "anyPart", index);
        if (u.TrackId is { } trackId) {
            ctx.ValidateRef(trackId, "track", index);
        }
        if (u.Pos is < 0) {
            throw BridgeException.OutOfRange($"{at}.pos", u.Pos.Value, 0, int.MaxValue);
        }
        if (u.Dur is < 1) {
            throw BridgeException.OutOfRange($"{at}.dur", u.Dur.Value, 1, int.MaxValue);
        }
        return () => {
            var project = ctx.Project;
            var doc = PlanContext.Doc;
            var part = ctx.AnyPart(u.PartId);
            if (part is UVoicePart voice) {
                ctx.Touch(voice);
            }
            if (u.Name is { } name && name != part.name) {
                doc.ExecuteCmd(new RenamePartCommand(project, part, name));
            }
            if (u.Dur is int dur && dur != part.Duration) {
                int delta = dur - part.Duration;
                if (u.ResizeFromStart && part.position - delta < 0) {
                    throw new InvalidOperationException(
                        $"the new start would be before the start of the project: the part ends {part.End} ticks in, so the length can be at most {part.End}");
                }
                if (part is UVoicePart { notes.Count: > 0 } withNotes) {
                    // OpenUtau's resize commands do not protect notes; the UI does. Never leave notes outside the part.
                    if (u.ResizeFromStart && -delta > withNotes.notes.First().position) {
                        throw new InvalidOperationException(
                            $"the new start would cut notes: the first note starts {withNotes.notes.First().position} ticks after the part start");
                    }
                    if (!u.ResizeFromStart && dur < withNotes.notes.Max(n => n.End)) {
                        throw new InvalidOperationException(
                            $"the new length would cut notes: the last note ends {withNotes.notes.Max(n => n.End)} ticks into the part");
                    }
                }
                doc.ExecuteCmd(part switch {
                    UVoicePart v => new ResizeVoicePartCommand(project, v, delta, u.ResizeFromStart),
                    UWavePart w => new ResizeWavePartCommand(project, w, delta, u.ResizeFromStart),
                    _ => throw new InvalidOperationException("unknown part type"),
                });
            }
            int trackNo = u.TrackId is { } t ? ctx.Track(t).TrackNo : part.trackNo;
            int pos = u.Pos ?? part.position;
            if (pos != part.position || trackNo != part.trackNo) {
                doc.ExecuteCmd(new MovePartCommand(project, part, pos, trackNo));
            }
        };
    }

    static Action PlanPartRemove(PlanContext ctx, PartRemoveOp r, int index) {
        ctx.ValidateRef(r.PartId, "anyPart", index);
        return () => {
            var part = ctx.AnyPart(r.PartId);
            ctx.Removed.Add(BridgeHost.Ids.IdOf(part));
            if (part is UVoicePart voice) {
                ctx.OverlapsBefore.Remove(voice);
            }
            PlanContext.Doc.ExecuteCmd(new RemovePartCommand(ctx.Project, part));
        };
    }

    static Action PlanProjectUpdate(PlanContext ctx, ProjectUpdateOp u, int index) {
        string at = $"ops[{index}]";
        var project = ctx.Project;
        void CheckBpm(double bpm, string path) {
            if (bpm is < 10 or > 1000) {
                throw BridgeException.OutOfRange(path, bpm, 10, 1000);
            }
        }
        void CheckSig(int num, int den, string path) {
            if (num is < 1 or > 32 || !beatUnits.Contains(den)) {
                throw BridgeException.InvalidParams($"Invalid time signature {num}/{den}.", path);
            }
        }
        if (u.Bpm is double b) {
            CheckBpm(b, $"{at}.bpm");
        }
        foreach (var t in u.TempoAdd ?? []) {
            CheckBpm(t.Bpm, $"{at}.tempoAdd");
            if (t.Tick <= 0) {
                throw BridgeException.InvalidParams("Tempo changes start after tick 0; use bpm for the initial tempo.", $"{at}.tempoAdd");
            }
        }
        if ((u.TempoAdd ?? []).GroupBy(t => t.Tick).FirstOrDefault(g => g.Count() > 1) is { } twiceTempo) {
            throw BridgeException.InvalidParams($"Two tempo changes at tick {twiceTempo.Key}.", $"{at}.tempoAdd");
        }
        foreach (int tick in u.TempoRemove ?? []) {
            if (tick == 0 || !project.tempos.Any(t => t.position == tick)) {
                throw BridgeException.InvalidParams($"There is no removable tempo change at tick {tick}.", $"{at}.tempoRemove");
            }
        }
        if (u.TimeSig is { } sig) {
            CheckSig(sig.BeatPerBar, sig.BeatUnit, $"{at}.timeSig");
        }
        foreach (var s in u.TimeSigAdd ?? []) {
            CheckSig(s.BeatPerBar, s.BeatUnit, $"{at}.timeSigAdd");
            if (s.Bar <= 0) {
                throw BridgeException.InvalidParams("Time signature changes start after bar 0; use timeSig for the first one.", $"{at}.timeSigAdd");
            }
        }
        if ((u.TimeSigAdd ?? []).GroupBy(t => t.Bar).FirstOrDefault(g => g.Count() > 1) is { } twiceSig) {
            throw BridgeException.InvalidParams($"Two time signature changes at bar {twiceSig.Key}.", $"{at}.timeSigAdd");
        }
        foreach (int bar in u.TimeSigRemove ?? []) {
            if (bar == 0 || !project.timeSignatures.Any(s => s.barPosition == bar)) {
                throw BridgeException.InvalidParams($"There is no removable time signature at bar {bar}.", $"{at}.timeSigRemove");
            }
        }
        if (u.Key is < 0 or > 11) {
            throw BridgeException.OutOfRange($"{at}.key", u.Key.Value, 0, 11);
        }
        if (u.Comment is not null) {
            ctx.Warnings.Add(new Warning(WarningCodes.NotUndoable, "The project comment (sections) is not undoable."));
        }
        return () => {
            var doc = PlanContext.Doc;
            if (u.Bpm is double bpm) {
                doc.ExecuteCmd(new BpmCommand(project, bpm));
            }
            foreach (int tick in u.TempoRemove ?? []) {
                doc.ExecuteCmd(new DelTempoChangeCommand(project, tick));
            }
            foreach (var t in u.TempoAdd ?? []) {
                // Adding where a change already exists replaces it (OpenUtau's command would add a second one).
                if (project.tempos.Any(x => x.position == t.Tick)) {
                    doc.ExecuteCmd(new DelTempoChangeCommand(project, t.Tick));
                }
                doc.ExecuteCmd(new AddTempoChangeCommand(project, t.Tick, t.Bpm));
            }
            if (u.TimeSig is { } first) {
                doc.ExecuteCmd(new TimeSignatureCommand(project, first.BeatPerBar, first.BeatUnit));
            }
            foreach (int bar in u.TimeSigRemove ?? []) {
                doc.ExecuteCmd(new DelTimeSigCommand(project, bar));
            }
            foreach (var s in u.TimeSigAdd ?? []) {
                if (project.timeSignatures.Any(x => x.barPosition == s.Bar)) {
                    doc.ExecuteCmd(new DelTimeSigCommand(project, s.Bar));
                }
                doc.ExecuteCmd(new AddTimeSigCommand(project, s.Bar, s.BeatPerBar, s.BeatUnit));
            }
            if (u.Key is int key) {
                doc.ExecuteCmd(new KeyCommand(project, key));
            }
            if (u.Comment is { } comment) {
                // Direct write: OpenUtau has no command for the project comment.
                string old = project.comment;
                ctx.OnRollback(() => project.comment = old);
                project.comment = comment;
            }
        };
    }
}
