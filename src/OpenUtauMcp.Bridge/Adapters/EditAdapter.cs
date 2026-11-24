using System.Text.Json.Nodes;
using OpenUtau.Api;
using OpenUtau.Core;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Contracts.Protocol;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>
/// edit.apply: plan every op first (validation; nothing runs if any op
/// is invalid), then execute all of them in ONE undo group; any failure rolls the whole group back.
/// UI thread only.
/// </summary>
public static partial class EditAdapter {
    const int DeferValidateThreshold = 200;
    const int MaxTuningCents = 1200;
    static readonly HashSet<string> pitchShapes = ["io", "l", "i", "o", "sp"];

    sealed record PlannedOp(int Index, Action Execute);

    public static EditApplyResult Apply(UProject project, EditApplyParams p) {
        if (p.Ops.Count == 0) {
            throw BridgeException.InvalidParams("'ops' is empty.", "ops");
        }
        if (p.Ops.Count > ProtocolInfo.MaxOpsPerApply) {
            throw new BridgeException(ErrorCodes.LimitExceeded, $"At most {ProtocolInfo.MaxOpsPerApply} ops per call.");
        }
        UndoScope.EnsureNoUserEdit();
        // The official exporter reads the live project from another thread.
        BridgeHost.Jobs.EnsureIdle();
        CheckIfRev(project, p.IfRev);

        var ctx = new PlanContext(project);
        var plan = p.Ops.Select((op, i) => Plan(ctx, op, i)).ToList();

        if (!p.DryRun) {
            BackupBeforeFirstEdit(project);
            string name = string.IsNullOrWhiteSpace(p.UndoName) ? $"MCP: {plan.Count} edit(s)" : p.UndoName;
            bool structural = p.Ops.Any(op => op is TrackAddOp or PartAddOp or PartDuplicateOp or ProjectExpressionsOp
                or TrackUpdateOp or TrackRemoveOp or PartUpdateOp or PartRemoveOp or ProjectUpdateOp or PartAddWaveOp);
            int current = -1;
            try {
                UndoScope.Run(name, deferValidate: structural || plan.Count > DeferValidateThreshold, () => {
                    foreach (var op in plan) {
                        current = op.Index;
                        op.Execute();
                    }
                    current = -1;
                    CheckOverlaps(ctx, p.AllowOverlap);
                });
            } catch (Exception e) when (e is not BridgeException { Code: ErrorCodes.BusyUserEditing }) {
                ctx.RestoreDirectWrites();
                string where = current >= 0 ? $"Op {current} failed" : "The plan failed";
                throw new BridgeException(ErrorCodes.OpFailed, $"{where} and nothing was changed: {e.Message}",
                    new JsonObject { ["opIndex"] = current, ["cause"] = e.Message });
            }
        }

        var ids = BridgeHost.Ids;
        return new EditApplyResult {
            Epoch = ids.Epoch,
            Applied = plan.Count,
            DryRun = p.DryRun,
            PartRevs = ctx.OverlapsBefore.Keys.Where(project.parts.Contains)
                .ToDictionary(part => ids.IdOf(part), part => BridgeHost.Revisions.PartRev(part)),
            StructureRev = BridgeHost.Revisions.StructureRev,
            Created = ctx.Created.Count > 0 ? ctx.Created : null,
            Removed = ctx.Removed.Count > 0 ? ctx.Removed : null,
            Warnings = ctx.Warnings.Count > 0 ? ctx.Warnings : null,
        };
    }

    static PlannedOp Plan(PlanContext ctx, EditOp op, int index) {
        try {
            return new PlannedOp(index, op switch {
                NoteUpdateOp o => PlanNoteUpdate(ctx, o, index),
                NoteAddOp o => PlanNoteAdd(ctx, o, index),
                NoteRemoveOp o => PlanNoteRemove(ctx, o, index),
                NoteVibratoOp o => PlanNoteVibrato(ctx, o, index),
                NotePitchOp o => PlanNotePitch(ctx, o, index),
                NoteExprOp o => PlanNoteExpr(ctx, o, index),
                CurveReplaceOp o => PlanCurve(ctx, o.PartId, o.Abbr, null, null, o.Xs, o.Ys, index),
                CurveSetRangeOp o => PlanCurve(ctx, o.PartId, o.Abbr, o.FromTick, o.ToTick, o.Xs, o.Ys, index),
                CurveClearOp o => PlanCurveClear(ctx, o, index),
                ProjectExpressionsOp o => PlanExpressions(ctx, o, index),
                TrackAddOp o => PlanTrackAdd(ctx, o, index),
                TrackMixOp o => PlanTrackMix(ctx, o, index),
                PartAddOp o => PlanPartAdd(ctx, o, index),
                PartDuplicateOp o => PlanPartDuplicate(ctx, o, index),
                PartCommentOp o => PlanPartComment(ctx, o, index),
                TrackUpdateOp o => PlanTrackUpdate(ctx, o, index),
                TrackRemoveOp o => PlanTrackRemove(ctx, o, index),
                PartUpdateOp o => PlanPartUpdate(ctx, o, index),
                PartRemoveOp o => PlanPartRemove(ctx, o, index),
                ProjectUpdateOp o => PlanProjectUpdate(ctx, o, index),
                NoteResetExprsOp o => PlanNoteResetExprs(ctx, o, index),
                NotePhonemizerOp o => PlanNotePhonemizer(ctx, o, index),
                NotePhonemesOp o => PlanNotePhonemes(ctx, o, index),
                PartAddWaveOp o => PlanPartAddWave(ctx, o, index),
                _ => throw new BridgeException(ErrorCodes.CapabilityUnavailable, $"Op type {op.GetType().Name} is not supported."),
            });
        } catch (BridgeException e) {
            e.Details["opIndex"] = index;
            throw;
        }
    }

    // ------------------------------------------------------------------ notes

    static Action PlanNoteUpdate(PlanContext ctx, NoteUpdateOp u, int index) {
        ctx.ValidateRef(u.NoteId, "note", index);
        string at = $"ops[{index}]";
        CheckNoteValues(at, u.Pos, u.Dur, u.Tone, u.Tuning);
        if (u.Lyric is { Length: 0 }) {
            throw BridgeException.InvalidParams("Lyric cannot be empty.", $"{at}.lyric");
        }
        if (u.Lyric is not null) {
            ctx.PhonemesPending.Add(u.NoteId);
        }
        // Deltas are computed at execution time: an earlier op of the same plan may have moved this note.
        return () => {
            var (note, part) = ctx.Note(u.NoteId);
            ctx.Touch(part);
            var doc = PlanContext.Doc;
            if (u.Lyric is { } lyric && lyric != note.lyric) {
                doc.ExecuteCmd(new ChangeNoteLyricCommand(part, note, lyric));
            }
            int deltaPos = u.Pos is int pos ? pos - note.position : 0;
            int deltaTone = u.Tone is int tone ? tone - note.tone : 0;
            if (deltaPos != 0 || deltaTone != 0) {
                doc.ExecuteCmd(new MoveNoteCommand(part, note, deltaPos, deltaTone));
            }
            if (u.Dur is int dur && dur != note.duration) {
                doc.ExecuteCmd(new ResizeNoteCommand(part, note, dur - note.duration));
            }
            if (u.Tuning is int tuning && tuning != note.tuning) {
                doc.ExecuteCmd(new ChangeNoteTuningCommand(part, note, tuning));
            }
        };
    }

    static Action PlanNoteAdd(PlanContext ctx, NoteAddOp a, int index) {
        string at = $"ops[{index}]";
        ctx.ValidateRef(a.PartId, "part", index);
        CheckNoteValues(at, a.Pos, a.Dur, a.Tone, a.Tuning);
        if (string.IsNullOrEmpty(a.Lyric)) {
            throw BridgeException.InvalidParams("Lyric cannot be empty.", $"{at}.lyric");
        }
        var pitch = a.Pitch is null ? null : CheckPitch(a.Pitch, at);
        var exprs = (a.Exprs ?? new Dictionary<string, float>())
            .Select(kv => (abbr: kv.Key, value: CheckNoteExprValue(ctx, kv.Key, kv.Value, index))).ToList();
        ctx.DeclareRef(a.Ref, "note", index);
        ctx.PhonemesPending.Add(a.Ref ?? $"#{index}");

        return () => {
            var project = ctx.Project;
            var part = ctx.Part(a.PartId);
            ctx.Touch(part);
            var doc = PlanContext.Doc;
            int durationBefore = part.Duration;
            if (a.Pos + a.Dur > part.Duration) {
                if (!a.GrowPart) {
                    throw new InvalidOperationException($"the note ends after part {BridgeHost.Ids.IdOf(part)} and growPart is false");
                }
                doc.ExecuteCmd(new ResizeVoicePartCommand(project, part, a.Pos + a.Dur - part.Duration, false));
            }
            var note = project.CreateNote(a.Tone, a.Pos, a.Dur);
            note.lyric = a.Lyric;
            if (a.Tuning is int tuning) {
                note.tuning = tuning;
            }
            if (pitch is not null) {
                note.pitch = pitch.Clone();
            }
            if (a.Vibrato is { } patch) {
                ApplyVibrato(note.vibrato, patch, ctx, at);
            }
            doc.ExecuteCmd(new AddNoteCommand(part, note));
            if (part.Duration > durationBefore) {
                // AddNoteCommand itself also extends the part when a note ends near its end (OpenUtau keeps room after the last note).
                ctx.Warnings.Add(new Warning(WarningCodes.PartGrown,
                    $"Part grew by {part.Duration - durationBefore} ticks to fit a new note.", BridgeHost.Ids.IdOf(part)));
            }
            foreach (var (abbr, value) in exprs) {
                doc.ExecuteCmd(new SetNotesSameExpressionCommand(project, ctx.TrackOf(part), part, [note], abbr, value));
            }
            ctx.Bind(a.Ref, $"#{index}", note, BridgeHost.Ids.IdOf(note, part), part);
        };
    }

    static Action PlanNoteRemove(PlanContext ctx, NoteRemoveOp r, int index) {
        if (r.NoteIds.Count == 0) {
            throw BridgeException.InvalidParams("'noteIds' is empty.", $"ops[{index}].noteIds");
        }
        foreach (var id in r.NoteIds) {
            ctx.ValidateRef(id, "note", index);
        }
        return () => {
            var resolved = r.NoteIds.Select(id => (id, target: ctx.Note(id))).ToList();
            foreach (var group in resolved.GroupBy(x => x.target.part)) {
                ctx.Touch(group.Key);
                PlanContext.Doc.ExecuteCmd(new RemoveNoteCommand(group.Key, group.Select(x => x.target.note).Distinct().ToList()));
            }
            ctx.Removed.AddRange(resolved.Select(x => BridgeHost.Ids.IdOf(x.target.note, x.target.part)));
        };
    }

    static Action PlanNoteVibrato(PlanContext ctx, NoteVibratoOp v, int index) {
        string at = $"ops[{index}]";
        foreach (var id in v.NoteIds) {
            ctx.ValidateRef(id, "note", index);
        }
        if (!v.Reset && v.Vibrato is null) {
            throw BridgeException.InvalidParams("Give 'vibrato' or 'reset: true'.", at);
        }
        return () => {
            foreach (var id in v.NoteIds) {
                var (note, part) = ctx.Note(id);
                ctx.Touch(part);
                var vibrato = v.Reset ? new UVibrato() : note.vibrato.Clone();
                if (!v.Reset) {
                    ApplyVibrato(vibrato, v.Vibrato!, ctx, at);
                }
                PlanContext.Doc.ExecuteCmd(new SetVibratoCommand(part, note, vibrato));
            }
        };
    }

    static Action PlanNotePitch(PlanContext ctx, NotePitchOp n, int index) {
        string at = $"ops[{index}]";
        foreach (var id in n.NoteIds) {
            ctx.ValidateRef(id, "note", index);
        }
        var pitch = n.Pitch is null ? null : CheckPitch(n.Pitch, at);
        return () => {
            foreach (var id in n.NoteIds) {
                var (note, part) = ctx.Note(id);
                ctx.Touch(part);
                PlanContext.Doc.ExecuteCmd(pitch is null
                    ? new ResetPitchPointsCommand(part, note)
                    : new SetPitchPointsCommand(part, note, pitch.Clone()));
            }
        };
    }

    static Action PlanNoteExpr(PlanContext ctx, NoteExprOp e, int index) {
        string at = $"ops[{index}]";
        foreach (var id in e.NoteIds) {
            ctx.ValidateRef(id, "note", index);
        }
        var descriptor = ctx.Descriptor(e.Abbr, index);
        if (descriptor.type is UExpressionType.Curve or UExpressionType.MaskedCurve) {
            throw BridgeException.InvalidParams($"'{e.Abbr}' is a curve; write it with curve ops.", $"{at}.abbr");
        }
        if (e.Values is { } values) {
            if (e.NoteIds.Count != 1) {
                throw BridgeException.InvalidParams("Per-phoneme 'values' need exactly one note.", $"{at}.noteIds");
            }
            if (ctx.PhonemesPending.Contains(e.NoteIds[0]) || ctx.IsRef(e.NoteIds[0])) {
                throw BridgeException.InvalidParams(
                    "Per-phoneme values need the note's phonemes, which do not exist until OpenUtau phonemizes the note " +
                    "created, copied or re-lyricked in this same call. Apply in two calls.", $"{at}.values");
            }
            var (note, part) = ctx.Note(e.NoteIds[0]);
            int phonemes = part.phonemes.Count(ph => ph.Parent == note);
            if (values.Count != phonemes) {
                throw BridgeException.InvalidParams($"The note has {phonemes} phoneme(s) but {values.Count} values were given.", $"{at}.values");
            }
            var clamped = values.Select(v => v is float f ? ClampExpr(ctx, descriptor, f, at) : (float?)null).ToArray();
            return () => {
                var (n, p) = ctx.Note(e.NoteIds[0]);
                ctx.Touch(p);
                PlanContext.Doc.ExecuteCmd(new SetNoteExpressionCommand(ctx.Project, ctx.TrackOf(p), p, n, e.Abbr, clamped));
            };
        }
        float? value = e.Value is float v ? ClampExpr(ctx, descriptor, v, at) : null;
        return () => {
            foreach (var group in e.NoteIds.Select(ctx.Note).GroupBy(x => x.part)) {
                ctx.Touch(group.Key);
                PlanContext.Doc.ExecuteCmd(new SetNotesSameExpressionCommand(ctx.Project, ctx.TrackOf(group.Key), group.Key,
                    group.Select(x => x.note).ToList(), e.Abbr, value));
            }
        };
    }

    static void CheckNoteValues(string at, int? pos, int? dur, int? tone, int? tuning) {
        if (pos is < 0) {
            throw BridgeException.OutOfRange($"{at}.pos", pos.Value, 0, int.MaxValue);
        }
        if (dur is < 1) {
            throw BridgeException.OutOfRange($"{at}.dur", dur.Value, 1, int.MaxValue);
        }
        if (tone is < 0 or > 127) {
            throw BridgeException.OutOfRange($"{at}.tone", tone.Value, 0, 127);
        }
        if (tuning is < -MaxTuningCents or > MaxTuningCents) {
            throw BridgeException.OutOfRange($"{at}.tuning", tuning.Value, -MaxTuningCents, MaxTuningCents);
        }
    }

    /// <summary>Strict pitch points (fix #13): shapes io|l|i|o|sp, finite values, ascending x.</summary>
    static UPitch CheckPitch(PitchSpec spec, string at) {
        if (spec.Points.Count == 0) {
            throw BridgeException.InvalidParams("Pitch needs at least one point (or omit it to reset).", $"{at}.pitch");
        }
        var pitch = new UPitch { snapFirst = spec.SnapFirst };
        float lastX = float.NegativeInfinity;
        for (int i = 0; i < spec.Points.Count; i++) {
            var pt = spec.Points[i];
            if (!pitchShapes.Contains(pt.Shape)) {
                throw BridgeException.InvalidParams($"Unknown pitch shape '{pt.Shape}'; use io, l, i, o or sp.", $"{at}.pitch.points[{i}].shape");
            }
            if (!float.IsFinite(pt.X) || !float.IsFinite(pt.Y) || pt.X < lastX) {
                throw BridgeException.InvalidParams("Pitch points need finite values in ascending x.", $"{at}.pitch.points[{i}]");
            }
            lastX = pt.X;
            pitch.AddPoint(new PitchPoint(pt.X, pt.Y, Enum.Parse<PitchPointShape>(pt.Shape)));
        }
        return pitch;
    }

    /// <summary>Applies a vibrato patch; normalizes in/out so in + out &lt;= 100 before assigning (fix #12).</summary>
    static void ApplyVibrato(UVibrato v, VibratoPatch patch, PlanContext ctx, string at) {
        if (patch.Length is float length) v.length = length;
        if (patch.Period is float period) v.period = period;
        if (patch.Depth is float depth) v.depth = depth;
        if (patch.Shift is float shift) v.shift = shift;
        if (patch.Drift is float drift) v.drift = drift;
        if (patch.VolLink is float volLink) v.volLink = volLink;
        float fadeIn = Math.Clamp(patch.In ?? v.@in, 0, 100);
        float fadeOut = Math.Clamp(patch.Out ?? v.@out, 0, 100);
        if (fadeIn + fadeOut > 100) {
            float scale = 100 / (fadeIn + fadeOut);
            fadeIn *= scale;
            fadeOut *= scale;
            ctx.Warnings.Add(new Warning(WarningCodes.Clamped, $"{at}: vibrato in + out scaled down to 100%."));
        }
        // Assign the smaller one first: OpenUtau clamps each against the other.
        v.@in = 0;
        v.@out = fadeOut;
        v.@in = fadeIn;
    }

    static float CheckNoteExprValue(PlanContext ctx, string abbr, float value, int index) {
        var descriptor = ctx.Descriptor(abbr, index);
        if (descriptor.type is UExpressionType.Curve or UExpressionType.MaskedCurve) {
            throw BridgeException.InvalidParams($"'{abbr}' is a curve; write it with curve ops.", $"ops[{index}].exprs");
        }
        return ClampExpr(ctx, descriptor, value, $"ops[{index}]");
    }

    static float ClampExpr(PlanContext ctx, UExpressionDescriptor d, float value, string at) {
        if (d.type == UExpressionType.Options) {
            // Voice colors (clr) are validated by OpenUtau against the singer; options need a whole index.
            return MathF.Round(Math.Max(0, value));
        }
        float clamped = Math.Clamp(value, d.min, d.max);
        if (clamped != value) {
            ctx.Warnings.Add(new Warning(WarningCodes.Clamped, $"{at}: {d.abbr} clamped to [{d.min}, {d.max}]."));
        }
        return clamped;
    }

    // ------------------------------------------------------------------ curves

    static Action PlanCurve(PlanContext ctx, string partId, string abbr, int? from, int? to,
                            IReadOnlyList<int> xs, IReadOnlyList<int> ys, int index) {
        string at = $"ops[{index}]";
        ctx.ValidateRef(partId, "part", index);
        var descriptor = ctx.Descriptor(abbr, index);
        if (descriptor.type != UExpressionType.Curve) {
            throw BridgeException.InvalidParams($"Expression '{abbr}' is not a curve.", $"{at}.abbr");
        }
        if (xs.Count != ys.Count) {
            throw BridgeException.InvalidParams("'xs' and 'ys' must have the same length.", $"{at}.xs");
        }
        if (xs.Count > ProtocolInfo.MaxCurvePoints) {
            throw new BridgeException(ErrorCodes.LimitExceeded, $"At most {ProtocolInfo.MaxCurvePoints} curve points.");
        }
        if (from is int f && to is int t && (f < 0 || t <= f)) {
            throw BridgeException.InvalidParams("The range needs 0 <= fromTick < toTick.", $"{at}.fromTick");
        }
        var points = CurveMath.Normalize(xs, ys, descriptor, out bool snapped, out bool clamped, at);
        if (from is int lo && to is int hi && points.Keys.Any(x => x < CurveMath.Snap(lo) || x > CurveMath.Snap(hi))) {
            throw BridgeException.InvalidParams("All points must lie inside [fromTick, toTick].", $"{at}.xs");
        }
        if (snapped) {
            ctx.Warnings.Add(new Warning(WarningCodes.SnappedToGrid, $"{abbr}: x positions snapped to the {ProtocolInfo.CurveGridTicks}-tick grid.", partId));
        }
        if (clamped) {
            ctx.Warnings.Add(new Warning(WarningCodes.Clamped, $"{abbr}: values clamped to [{descriptor.min}, {descriptor.max}].", partId));
        }
        return () => {
            var part = ctx.Part(partId);
            ctx.Touch(part);
            var curve = part.curves.FirstOrDefault(c => c.abbr == abbr);
            var (newXs, newYs) = from is int a && to is int b
                ? CurveMath.Splice(curve, CurveMath.Snap(a), CurveMath.Snap(b), points, (int)descriptor.defaultValue)
                : (points.Keys.ToArray(), points.Values.ToArray());
            WriteCurve(ctx.Project, part, abbr, curve, newXs, newYs);
        };
    }

    static Action PlanCurveClear(PlanContext ctx, CurveClearOp c, int index) {
        string at = $"ops[{index}]";
        ctx.ValidateRef(c.PartId, "part", index);
        var descriptor = ctx.Descriptor(c.Abbr, index);
        if ((c.FromTick is null) != (c.ToTick is null) || (c.FromTick is int f && c.ToTick is int t && (f < 0 || t <= f))) {
            throw BridgeException.InvalidParams("Give both fromTick and toTick (0 <= from < to), or neither.", $"{at}.fromTick");
        }
        return () => {
            var part = ctx.Part(c.PartId);
            ctx.Touch(part);
            var curve = part.curves.FirstOrDefault(x => x.abbr == c.Abbr);
            if (curve is null || curve.xs.Count == 0) {
                return;
            }
            if (c.FromTick is int a && c.ToTick is int b) {
                var (xs, ys) = CurveMath.Splice(curve, CurveMath.Snap(a), CurveMath.Snap(b), new SortedDictionary<int, int>(),
                    (int)descriptor.defaultValue);
                WriteCurve(ctx.Project, part, c.Abbr, curve, xs, ys);
            } else {
                PlanContext.Doc.ExecuteCmd(new ClearCurveCommand(part, c.Abbr));
            }
        };
    }

    static void WriteCurve(UProject project, UVoicePart part, string abbr, UCurve? curve, int[] newXs, int[] newYs) {
        int[] oldXs = curve?.xs.ToArray() ?? [];
        int[] oldYs = curve?.ys.ToArray() ?? [];
        PlanContext.Doc.ExecuteCmd(new MergedSetCurveCommand(project, part, abbr, oldXs, oldYs, newXs, newYs));
        var after = part.curves.FirstOrDefault(x => x.abbr == abbr);
        int count = after?.xs.Count ?? 0;
        if (count != newXs.Length) {
            throw new InvalidOperationException($"curve '{abbr}' has {count} points after the write, expected {newXs.Length}");
        }
    }

    // ------------------------------------------------------------------ project / tracks / parts

    static Action PlanExpressions(PlanContext ctx, ProjectExpressionsOp e, int index) {
        var project = ctx.Project;
        var suggested = new Dictionary<string, UExpressionDescriptor>();
        IEnumerable<UTrack> sources = project.tracks;
        if (e.SuggestedFor is { } trackId) {
            ctx.ValidateRef(trackId, "track", index);
            sources = ctx.IsRef(trackId) ? [] : [BridgeHost.Ids.ResolveTrack(project, trackId)];
        }
        foreach (var track in sources) {
            foreach (var d in Catalog.Suggested(track)) {
                suggested.TryAdd(d.abbr, d);
            }
        }
        if (e.SuggestedForSinger is { } singerId) {
            foreach (var d in Catalog.SuggestedForSinger(Catalog.FindSinger(singerId))) {
                suggested.TryAdd(d.abbr, d);
            }
        }
        var toAdd = new List<UExpressionDescriptor>();
        foreach (var abbr in e.Ensure.Distinct()) {
            if (project.expressions.ContainsKey(abbr) || ctx.Ensured.ContainsKey(abbr)) {
                continue;
            }
            if (!suggested.TryGetValue(abbr, out var descriptor)) {
                throw new BridgeException(ErrorCodes.UnknownExpression,
                    $"'{abbr}' is neither registered nor suggested by the tracks' renderers.",
                    new JsonObject { ["abbr"] = abbr, ["suggested"] = new JsonArray(suggested.Keys.Select(k => (JsonNode?)k).ToArray()) });
            }
            ctx.Ensured[abbr] = descriptor;
            toAdd.Add(descriptor.Clone());
        }
        return () => {
            if (toAdd.Count > 0) {
                var all = project.expressions.Values.Concat(toAdd).ToArray();
                PlanContext.Doc.ExecuteCmd(new ConfigureExpressionsCommand(project, all));
            }
        };
    }

    static Action PlanTrackAdd(PlanContext ctx, TrackAddOp t, int index) {
        string at = $"ops[{index}]";
        USinger? singer = null;
        if (t.SingerId is { } singerId) {
            singer = Catalog.FindSinger(singerId);
        }
        if (t.CopyFrom is { } copyFrom) {
            ctx.ValidateRef(copyFrom, "track", index);
            if (singer is not null) {
                throw BridgeException.InvalidParams("Give singerId or copyFrom, not both.", at);
            }
        }
        ctx.DeclareRef(t.Ref, "track", index);
        return () => {
            var project = ctx.Project;
            var doc = PlanContext.Doc;
            var track = new UTrack(project) { TrackNo = project.tracks.Count };
            if (t.Name is { Length: > 0 } name) {
                track.TrackName = name;
            }
            if (t.Color is { Length: > 0 } color) {
                track.TrackColor = color;
            }
            doc.ExecuteCmd(new AddTrackCommand(project, track));
            if (t.CopyFrom is { } source) {
                var from = ctx.Track(source);
                doc.ExecuteCmd(new TrackChangeSingerCommand(project, track, from.Singer));
                if (from.Phonemizer is { } phonemizer && PhonemizerFactory.Get(phonemizer.GetType()) is { } factory) {
                    doc.ExecuteCmd(new TrackChangePhonemizerCommand(project, track, factory.Create()));
                }
                doc.ExecuteCmd(new TrackChangeRenderSettingCommand(project, track, new URenderSettings {
                    renderer = from.RendererSettings.renderer,
                    resampler = from.RendererSettings.resampler,
                    wavtool = from.RendererSettings.wavtool,
                }));
            } else if (singer is not null) {
                ApplySinger(project, track, singer);
            }
            ctx.Bind(t.Ref, $"#{index}", track, BridgeHost.Ids.IdOf(track));
        };
    }

    /// <summary>Same flow as OpenUtau's track header, without the color remapping dialog.</summary>
    static void ApplySinger(UProject project, UTrack track, USinger singer) {
        var doc = PlanContext.Doc;
        if (singer.Found && !singer.Loaded) {
            singer.EnsureLoaded();  // as the UI does before assigning a singer: voice colors and phonemizer need it
        }
        doc.ExecuteCmd(new TrackChangeSingerCommand(project, track, singer));
        string? phonemizerName = !string.IsNullOrEmpty(singer.Id) && Preferences.Default.SingerPhonemizers.TryGetValue(singer.Id, out var preferred)
            ? preferred : singer.DefaultPhonemizer;
        if (!string.IsNullOrEmpty(phonemizerName) && PhonemizerFactory.Get(phonemizerName)?.Create() is { } phonemizer) {
            doc.ExecuteCmd(new TrackChangePhonemizerCommand(project, track, phonemizer));
        }
        doc.ExecuteCmd(new TrackChangeRenderSettingCommand(project, track, new URenderSettings {
            // Empty = let OpenUtau pick the default renderer when it validates the track (same as the UI).
            renderer = singer.Found ? Renderers.GetDefaultRenderer(singer.SingerType) : string.Empty,
        }));
    }

    static Action PlanTrackMix(PlanContext ctx, TrackMixOp m, int index) {
        string at = $"ops[{index}]";
        ctx.ValidateRef(m.TrackId, "track", index);
        if (m.VolumeDb is < -24 or > 12) {
            throw BridgeException.OutOfRange($"{at}.volumeDb", m.VolumeDb.Value, -24, 12);
        }
        if (m.Pan is < -100 or > 100) {
            throw BridgeException.OutOfRange($"{at}.pan", m.Pan.Value, -100, 100);
        }
        const string notUndoable =
            "Mixer changes are not undoable in OpenUtau, and the track header may not show them until the project is reloaded.";
        if (!ctx.IsRef(m.TrackId)) {
            ctx.Warnings.Add(new Warning(WarningCodes.NotUndoable, notUndoable, m.TrackId));
        }
        return () => {
            var project = ctx.Project;
            var track = ctx.Track(m.TrackId);
            if (ctx.IsRef(m.TrackId)) {
                ctx.Warnings.Add(new Warning(WarningCodes.NotUndoable, notUndoable, BridgeHost.Ids.IdOf(track)));
            }
            var (oldVolume, oldPan, oldMute, oldSolo) = (track.Volume, track.Pan, track.Mute, track.Solo);
            ctx.OnRollback(() => {
                (track.Volume, track.Pan, track.Mute, track.Solo) = (oldVolume, oldPan, oldMute, oldSolo);
                RefreshMix(project, track);
            });
            if (m.VolumeDb is double volume) track.Volume = volume;
            if (m.Pan is double pan) track.Pan = pan;
            if (m.Mute is bool mute) track.Mute = mute;
            if (m.Solo is bool solo) track.Solo = solo;
            RefreshMix(project, track);
        };
    }

    /// <summary>Recomputes Muted with the track header's JudgeMuted rule and tells the mixer.</summary>
    static void RefreshMix(UProject project, UTrack track) {
        foreach (var each in project.tracks) {
            each.Muted = !each.Solo && (each.Mute || project.SoloTrackExist);
            PlanContext.Doc.ExecuteCmd(new VolumeChangeNotification(each.TrackNo, each.Muted ? -24 : each.Volume));
        }
        if (project.tracks.Contains(track)) {
            PlanContext.Doc.ExecuteCmd(new PanChangeNotification(track.TrackNo, track.Pan));
        }
    }

    static Action PlanPartAdd(PlanContext ctx, PartAddOp p, int index) {
        string at = $"ops[{index}]";
        ctx.ValidateRef(p.TrackId, "track", index);
        if (p.Pos < 0) {
            throw BridgeException.OutOfRange($"{at}.pos", p.Pos, 0, int.MaxValue);
        }
        if (p.Dur < 1) {
            throw BridgeException.OutOfRange($"{at}.dur", p.Dur, 1, int.MaxValue);
        }
        ctx.DeclareRef(p.Ref, "part", index);
        return () => {
            var track = ctx.Track(p.TrackId);
            var part = new UVoicePart { trackNo = track.TrackNo, position = p.Pos, duration = p.Dur, name = p.Name ?? "New Part" };
            PlanContext.Doc.ExecuteCmd(new AddPartCommand(ctx.Project, part));
            ctx.Touch(part);
            ctx.Bind(p.Ref, $"#{index}", part, BridgeHost.Ids.IdOf(part));
        };
    }

    static Action PlanPartDuplicate(PlanContext ctx, PartDuplicateOp d, int index) {
        string at = $"ops[{index}]";
        ctx.ValidateRef(d.PartId, "part", index);
        ctx.ValidateRef(d.TrackId, "track", index);
        if (d.Pos is < 0) {
            throw BridgeException.OutOfRange($"{at}.pos", d.Pos.Value, 0, int.MaxValue);
        }
        List<string>? noteIds = null;
        List<UNote>? sourceNotes = null;
        if (d.NoteRefPrefix is { } prefix) {
            if (ctx.IsRef(d.PartId) || !ctx.IsRef(prefix) || prefix.Length < 2) {
                throw BridgeException.InvalidParams("noteRefPrefix needs an existing source part and must start with \"@\".", $"{at}.noteRefPrefix");
            }
            var sourcePart = BridgeHost.Ids.ResolveVoicePart(ctx.Project, d.PartId);
            noteIds = sourcePart.notes.Select(n => BridgeHost.Ids.IdOf(n, sourcePart)).ToList();
            sourceNotes = sourcePart.notes.ToList();
            foreach (var id in noteIds) {
                ctx.DeclareRef(prefix + id, "note", index);
            }
        }
        ctx.DeclareRef(d.Ref, "part", index);
        return () => {
            var source = ctx.Part(d.PartId);
            var track = ctx.Track(d.TrackId);
            var copy = (UVoicePart)source.Clone();
            copy.trackNo = track.TrackNo;
            copy.position = d.Pos ?? source.position;
            if (d.Name is { Length: > 0 } name) {
                copy.name = name;
            }
            PlanContext.Doc.ExecuteCmd(new AddPartCommand(ctx.Project, copy));
            ctx.Touch(copy);
            ctx.Bind(d.Ref, $"#{index}", copy, BridgeHost.Ids.IdOf(copy));
            if (noteIds is not null && sourceNotes is not null) {
                // Match by content: notes at the same position sort by hash code, so the clone order may differ there.
                var copies = copy.notes.GroupBy(n => (n.position, n.duration, n.tone, n.lyric))
                    .ToDictionary(g => g.Key, g => new Queue<UNote>(g));
                for (int k = 0; k < noteIds.Count; k++) {
                    var s = sourceNotes[k];
                    if (!copies.TryGetValue((s.position, s.duration, s.tone, s.lyric), out var queue) || !queue.TryDequeue(out var note)) {
                        throw new InvalidOperationException(
                            $"note {noteIds[k]} was removed from the source part by an earlier op of this plan, so its copy has no ref " +
                            $"{d.NoteRefPrefix}{noteIds[k]}; duplicate the part before changing its notes");
                    }
                    ctx.Bind(d.NoteRefPrefix + noteIds[k], $"#{index}", note, BridgeHost.Ids.IdOf(note, copy), copy, report: false);
                }
            }
        };
    }

    static Action PlanPartComment(PlanContext ctx, PartCommentOp c, int index) {
        ctx.ValidateRef(c.PartId, "part", index);
        if (c.Comment.Length > 10000) {
            throw BridgeException.InvalidParams("The comment is longer than 10000 characters.", $"ops[{index}].comment");
        }
        return () => {
            var part = ctx.Part(c.PartId);
            // Direct write: OpenUtau has no command for part comments, so this is not undoable.
            string old = part.comment;
            ctx.OnRollback(() => part.comment = old);
            part.comment = c.Comment;
            ctx.Warnings.Add(new Warning(WarningCodes.NotUndoable, "The part comment is not undoable.", BridgeHost.Ids.IdOf(part)));
        };
    }

    // ------------------------------------------------------------------ checks

    static void CheckOverlaps(PlanContext ctx, bool allow) {
        foreach (var (part, before) in ctx.OverlapsBefore) {
            int after = PlanContext.CountOverlaps(part);
            if (after <= before) {
                continue;
            }
            string id = BridgeHost.Ids.IdOf(part);
            if (!allow) {
                throw new InvalidOperationException(
                    $"the edit makes notes overlap in part {id} ({after - before} new overlap(s)); pass allowOverlap to accept");
            }
            ctx.Warnings.Add(new Warning(WarningCodes.Overlap, $"{after - before} new overlapping note(s) in part {id}.", id));
        }
    }

    static void CheckIfRev(UProject project, IReadOnlyDictionary<string, int>? ifRev) {
        if (ifRev is null) {
            return;
        }
        foreach (var (partId, expected) in ifRev) {
            var part = BridgeHost.Ids.ResolvePart(project, partId);
            int actual = BridgeHost.Revisions.PartRev(part);
            if (actual != expected) {
                throw new BridgeException(ErrorCodes.Conflict,
                    $"Part {partId} changed since it was read (partRev {expected} -> {actual}).",
                    new JsonObject { ["part"] = partId, ["expected"] = expected, ["actual"] = actual });
            }
        }
    }

    /// <summary>
    /// First mutation in a project (per epoch): back it up. Serialized here on
    /// the UI thread; the file is written in the background.
    /// </summary>
    internal static void BackupBeforeFirstEdit(UProject project) {
        int epoch = BridgeHost.Ids.Epoch;
        if (BridgeHost.AutoBackupEpoch == epoch) {
            return;
        }
        var snapshot = Backup.ProjectBackup.Serialize(project, "before-first-edit");
        BridgeHost.AutoBackupEpoch = epoch;
        _ = Task.Run(async () => {
            try {
                await Backup.ProjectBackup.WriteAsync(snapshot).ConfigureAwait(false);
            } catch (BridgeException e) {
                Serilog.Log.Warning($"{BridgeHost.LogPrefix} automatic backup failed: {e.Message}");
            }
        });
    }
}
