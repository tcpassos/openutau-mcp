using System.Runtime.CompilerServices;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;

namespace OpenUtauMcp.Bridge.Revisions;

/// <summary>
/// Bridge-owned revision counters. DocManager.Revision is not used because it
/// also moves on phonemization. Every non-notification command bumps the revision of the part it touches,
/// on execute, undo, redo and rollback, whoever issued it. Track, part and project commands bump
/// <see cref="StructureRev"/>. OnNext runs on the UI thread under DocManager's lock: keep it trivial.
/// </summary>
public sealed class RevisionTracker : ICmdSubscriber {
    readonly ConditionalWeakTable<UPart, StrongBox<int>> partRevs = new();
    int structureRev;

    public int StructureRev => Volatile.Read(ref structureRev);

    public int PartRev(UPart part) => partRevs.TryGetValue(part, out var box) ? box.Value : 0;

    public void OnNext(UCommand cmd, bool isUndo) {
        switch (cmd) {
            case LoadProjectNotification:
                Interlocked.Increment(ref structureRev);
                return;
            case UNotification:
                return;
            case NoteCommand note:
                Bump(note.Part);
                return;
            case ExpCommand exp:
                Bump(exp.Part);
                return;
            case PartCommand part:
                Bump(part.part);
                Interlocked.Increment(ref structureRev);
                return;
            default:
                // Track and project commands (tempo, time signature, key, expressions...).
                Interlocked.Increment(ref structureRev);
                return;
        }
    }

    void Bump(UPart? part) {
        if (part != null) {
            partRevs.GetOrCreateValue(part).Value++;
        }
    }
}
