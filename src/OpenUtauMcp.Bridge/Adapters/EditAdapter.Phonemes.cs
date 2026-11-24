using OpenUtau.Api;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtauMcp.Contracts.Ops;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>Per-note phoneme ops (F1b): reset expressions, phonemizer override, phoneme overrides.</summary>
public static partial class EditAdapter {
    static Action PlanNoteResetExprs(PlanContext ctx, NoteResetExprsOp r, int index) {
        if (r.NoteIds.Count == 0) {
            throw BridgeException.InvalidParams("'noteIds' is empty.", $"ops[{index}].noteIds");
        }
        foreach (var id in r.NoteIds) {
            ctx.ValidateRef(id, "note", index);
        }
        return () => {
            foreach (var (note, part) in r.NoteIds.Select(ctx.Note)) {
                ctx.Touch(part);
                PlanContext.Doc.ExecuteCmd(new ResetExpressionsCommand(part, note));
            }
        };
    }

    static Action PlanNotePhonemizer(PlanContext ctx, NotePhonemizerOp o, int index) {
        if (o.NoteIds.Count == 0) {
            throw BridgeException.InvalidParams("'noteIds' is empty.", $"ops[{index}].noteIds");
        }
        foreach (var id in o.NoteIds) {
            ctx.ValidateRef(id, "note", index);
            ctx.PhonemesPending.Add(id);
        }
        // OpenUtau stores the override by the factory's display name.
        string? name = o.Phonemizer is { } id2 ? FindPhonemizer(id2, $"ops[{index}].phonemizer").name : null;
        return () => {
            foreach (var (note, part) in o.NoteIds.Select(ctx.Note)) {
                ctx.Touch(part);
                if (note.PhonemizerOverride != name) {
                    PlanContext.Doc.ExecuteCmd(new ChangeNotePhonemizerCommand(part, note, name));
                }
            }
        };
    }

    internal static PhonemizerFactory FindPhonemizer(string id, string path) =>
        PhonemizerFactory.Get(id)
        ?? PhonemizerFactory.GetAll().FirstOrDefault(f => f.type.Name == id)
        ?? PhonemizerFactory.GetAll().FirstOrDefault(f => string.Equals(f.tag, id, StringComparison.OrdinalIgnoreCase))
        ?? PhonemizerFactory.GetAll().FirstOrDefault(f => string.Equals(f.name, id, StringComparison.OrdinalIgnoreCase))
        ?? throw BridgeException.InvalidParams($"Unknown phonemizer '{id}'.", path);

    /// <summary>OpenUtau clamps preutter/overlap to what the voicebank allows (as the UI does); say so instead of failing silently.</summary>
    static void WarnIfClamped(PlanContext ctx, UNote note, UVoicePart part, int index, string field, float requested, float applied) {
        if (Math.Abs(requested - applied) > 0.01f) {
            ctx.Warnings.Add(new Contracts.Errors.Warning(Contracts.Errors.WarningCodes.Clamped,
                $"Phoneme {index} {field}: {requested} ms was limited to {applied} ms by the voicebank timing.", BridgeHost.Ids.IdOf(note, part)));
        }
    }

    static Action PlanNotePhonemes(PlanContext ctx, NotePhonemesOp o, int index) {
        string at = $"ops[{index}]";
        ctx.ValidateRef(o.NoteId, "note", index);
        if (ctx.IsRef(o.NoteId) || ctx.PhonemesPending.Contains(o.NoteId)) {
            throw BridgeException.InvalidParams(
                "Phoneme overrides need the note's phonemes, which do not exist until OpenUtau phonemizes the note " +
                "created, re-lyricked or given another phonemizer in this same call. Apply in two calls.", $"{at}.overrides");
        }
        var (planNote, planPart) = ctx.Note(o.NoteId);
        if (o.Overrides is { } overrides) {
            if (!planPart.PhonemesUpToDate) {
                throw new BridgeException(Contracts.Errors.ErrorCodes.OpFailed,
                    "OpenUtau is still phonemizing this part; read the phonemes with awaitMs and retry.");
            }
            int count = planPart.phonemes.Count(ph => ph.Parent == planNote);
            foreach (var ov in overrides) {
                if (ov.Index < 0 || ov.Index >= count) {
                    throw BridgeException.OutOfRange($"{at}.overrides.index", ov.Index, 0, count - 1);
                }
            }
            if (overrides.Select(ov => ov.Index).Distinct().Count() != overrides.Count) {
                throw BridgeException.InvalidParams("Each phoneme index may appear once.", $"{at}.overrides");
            }
        }
        return () => {
            var (note, part) = ctx.Note(o.NoteId);
            ctx.Touch(part);
            var doc = PlanContext.Doc;
            if (o.Overrides is null) {
                doc.ExecuteCmd(new ClearPhonemeTimingCommand(part, note));
                foreach (var ov in note.phonemeOverrides.Where(x => !string.IsNullOrEmpty(x.phoneme)).ToList()) {
                    doc.ExecuteCmd(new ChangePhonemeAliasCommand(part, note, ov.index, null));
                }
                return;
            }
            foreach (var ov in o.Overrides) {
                // Phoneme objects are read before any change: preutter/overlap commands clamp against them.
                var phoneme = part.phonemes.First(ph => ph.Parent == note && ph.index == ov.Index);
                if (ov.Alias is { } alias) {
                    doc.ExecuteCmd(new ChangePhonemeAliasCommand(part, note, ov.Index, alias.Length == 0 ? null : alias));
                }
                if (ov.Offset is int offset) {
                    doc.ExecuteCmd(new PhonemeOffsetCommand(part, note, ov.Index, offset));
                }
                if (ov.PreutterDeltaMs is float pre) {
                    doc.ExecuteCmd(new PhonemePreutterCommand(part, note, ov.Index, phoneme, pre));
                    WarnIfClamped(ctx, note, part, ov.Index, "preutterDeltaMs", pre, note.GetPhonemeOverride(ov.Index).preutterDelta ?? 0);
                }
                if (ov.OverlapDeltaMs is float ovl) {
                    doc.ExecuteCmd(new PhonemeOverlapCommand(part, note, ov.Index, phoneme, ovl));
                    WarnIfClamped(ctx, note, part, ov.Index, "overlapDeltaMs", ovl, note.GetPhonemeOverride(ov.Index).overlapDelta ?? 0);
                }
            }
        };
    }
}
