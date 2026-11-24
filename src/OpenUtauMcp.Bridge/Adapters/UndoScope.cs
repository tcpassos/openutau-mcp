using OpenUtau.Core;
using OpenUtauMcp.Contracts.Errors;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>
/// The only place that opens undo groups. One request = one undo group.
/// On failure the group is rolled back AND closed: RollBackUndoGroup unexecutes the commands but leaves
/// the group open, so EndUndoGroup is still required (with no commands it adds nothing to the history).
/// Must run on the UI thread.
/// </summary>
public static class UndoScope {
    public static void EnsureNoUserEdit() {
        if (DocManager.Inst.HasOpenUndoGroup) {
            throw new BridgeException(ErrorCodes.BusyUserEditing,
                "The user is in the middle of an edit in OpenUtau (e.g. dragging a note).");
        }
    }

    public static void Run(string name, bool deferValidate, Action body) {
        var doc = DocManager.Inst;
        EnsureNoUserEdit();
        doc.StartUndoGroup(name, deferValidate);
        bool committed = false;
        BridgeHost.ApplyingFromBridge = true;
        try {
            body();
            committed = true;
        } finally {
            if (!committed) {
                doc.RollBackUndoGroup();
            }
            doc.EndUndoGroup();
            BridgeHost.ApplyingFromBridge = false;
        }
    }

    /// <summary>
    /// Runs OpenUtau code that manages its own undo group (batch edits, import). If it throws with its group still
    /// open, the partial changes are rolled back instead of committed; a group it left open on success is closed.
    /// </summary>
    public static void RunOwnGroup(Action body) {
        var doc = DocManager.Inst;
        EnsureNoUserEdit();
        BridgeHost.ApplyingFromBridge = true;
        bool ok = false;
        try {
            body();
            ok = true;
        } finally {
            if (doc.HasOpenUndoGroup) {
                if (!ok) {
                    doc.RollBackUndoGroup();
                }
                doc.EndUndoGroup();
            }
            BridgeHost.ApplyingFromBridge = false;
        }
    }
}
