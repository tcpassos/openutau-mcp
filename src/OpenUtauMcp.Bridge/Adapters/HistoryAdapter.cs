using OpenUtau.Core;
using OpenUtauMcp.Contracts.Model;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>Undo/redo (catalog section 2, DocManager.Undo/Redo). UI thread only.</summary>
public static class HistoryAdapter {
    public static HistoryState Get() {
        var doc = DocManager.Inst;
        bool canUndo = doc.GetUndoState(out var undoName);
        bool canRedo = doc.GetRedoState(out var redoName);
        return new HistoryState {
            CanUndo = canUndo,
            UndoName = undoName,
            CanRedo = canRedo,
            RedoName = redoName,
            ChangesSaved = doc.ChangesSaved,
        };
    }

    public static HistoryStepResult Step(bool undo, HistoryStepParams p) {
        UndoScope.EnsureNoUserEdit();
        if (p.Count is < 1 or > 20) {
            throw BridgeException.OutOfRange("count", p.Count, 1, 20);
        }
        var doc = DocManager.Inst;
        var steps = new List<string>();
        for (int i = 0; i < p.Count; i++) {
            string? name;
            bool available = undo ? doc.GetUndoState(out name) : doc.GetRedoState(out name);
            if (!available) {
                break;
            }
            if (undo) {
                doc.Undo();
            } else {
                doc.Redo();
            }
            steps.Add(name ?? "(unnamed)");
        }
        return new HistoryStepResult { Steps = steps, History = Get() };
    }
}
