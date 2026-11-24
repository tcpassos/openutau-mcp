using OpenUtau.Core;
using OpenUtau.Core.Editing;
using OpenUtau.Core.Ustx;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>
/// OpenUtau's built-in batch edits. They open their own undo group, so each run
/// is a separate history item (SEPARATE_UNDO_STEP) and never part of an edit.apply. Some recreate notes: created and
/// removed note ids are found by comparing the part's notes before and after.
/// </summary>
public static class BatchAdapter {
    const int DefaultQuantizeGrid = 120;

    /// <summary>The OpenUtau batch edit for an MCP name (sync and async ones).</summary>
    public static BatchEdit Create(string name, int? grid, int? semitones, string? lyric) => name switch {
        "quantize" => new QuantizeNotes(grid is > 0 ? grid.Value
            : grid is null ? DefaultQuantizeGrid : throw BridgeException.InvalidParams("'grid' must be positive.", "grid")),
        "auto_legato" => new AutoLegato(),
        "fix_overlap" => new FixOverlap(),
        "transpose" => new Transpose(semitones is int s and not 0 and >= -48 and <= 48 ? s
            : throw BridgeException.InvalidParams("'semitones' must be between -48 and 48 and not 0.", "semitones"), "MCP transpose"),
        "add_breath" => new AddBreathNote(lyric ?? "AP"),
        "add_tail" => new AddTailNote(lyric ?? "-", "pianoroll.menu.notes.addtaildash"),
        "remove_tail" => new RemoveTailNote(lyric ?? "-", "pianoroll.menu.notes.removetaildash"),
        "add_phonetic_hints" => new AddPhoneticHints(),
        "remove_phonetic_hints" => new RemovePhoneticHint(),
        "bake_pitch" => new BakePitch(),
        "reset_pitch" => new ResetPitchBends(),
        "reset_expressions" => new ResetAllExpressions(),
        "reset_vibratos" => new ResetVibratos(),
        "clear_vibratos" => new ClearVibratos(),
        "clear_timings" => new ClearTimings(),
        "reset_aliases" => new ResetAliases(),
        "reset_all" => new ResetAll(),
        BatchNames.LoadRenderedPitch => new LoadRenderedPitch(),
        BatchNames.RefreshRealCurves => new RefreshRealCurves(),
        _ => throw BridgeException.InvalidParams(
            $"Unknown batch edit '{name}'. Use {string.Join(", ", BatchNames.Sync.Concat(BatchNames.Async))}.", "name"),
    };

    public static List<UNote> Notes(UProject project, UVoicePart part, IReadOnlyList<string>? noteIds) {
        var notes = new List<UNote>();
        foreach (var id in noteIds ?? []) {
            var (note, owner) = BridgeHost.Ids.ResolveNote(project, id);
            if (!ReferenceEquals(owner, part)) {
                throw BridgeException.InvalidParams($"Note {id} is not in part {BridgeHost.Ids.IdOf(part)}.", "noteIds");
            }
            notes.Add(note);
        }
        return notes;
    }

    /// <summary>Synchronous batch edits, on the UI thread.</summary>
    public static BatchRunResult Run(UProject project, BatchRunParams p) {
        if (BatchNames.Async.Contains(p.Name)) {
            throw BridgeException.InvalidParams($"'{p.Name}' runs as a job: use jobs.start.", "name");
        }
        UndoScope.EnsureNoUserEdit();
        BridgeHost.Jobs.EnsureIdle();
        var ids = BridgeHost.Ids;
        var part = ids.ResolveVoicePart(project, p.PartId);
        var edit = Create(p.Name, p.Grid, p.Semitones, p.Lyric);
        var selected = Notes(project, part, p.NoteIds);

        var before = part.notes.Select(n => ids.IdOf(n, part)).ToHashSet();
        int revBefore = BridgeHost.Revisions.PartRev(part);
        long errorSeq = BridgeHost.Events.ErrorSeq;
        EditAdapter.BackupBeforeFirstEdit(project);
        try {
            UndoScope.RunOwnGroup(() => edit.Run(project, part, selected, DocManager.Inst));
        } catch (Exception e) {
            // A group the batch left open was rolled back; one it had already closed stays as its own history item.
            throw new BridgeException(ErrorCodes.BatchFailed, $"The batch edit failed: {e.Message}",
                new() { ["name"] = p.Name });
        }
        var errors = BridgeHost.Events.ErrorsSince(errorSeq);
        if (errors.Count > 0) {
            throw new BridgeException(ErrorCodes.BatchFailed, string.Join(" | ", errors), new() { ["name"] = p.Name });
        }
        var after = part.notes.Select(n => ids.IdOf(n, part)).ToHashSet();
        int revAfter = BridgeHost.Revisions.PartRev(part);
        bool changed = revAfter != revBefore;
        return new BatchRunResult {
            Epoch = ids.Epoch,
            PartId = ids.IdOf(part),
            PartRev = revAfter,
            Changed = changed,
            Created = after.Except(before).ToList(),
            Removed = before.Except(after).ToList(),
            Warnings = changed
                ? [new Warning(WarningCodes.SeparateUndoStep,
                    "OpenUtau records each batch edit as its own history item: one more Ctrl+Z (or ou_history undo) to revert it.",
                    ids.IdOf(part))]
                : null,
        };
    }
}
