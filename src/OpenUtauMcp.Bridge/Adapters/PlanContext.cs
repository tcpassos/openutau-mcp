using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtauMcp.Contracts.Errors;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>
/// State shared by the ops of one edit.apply plan. Planning (validation, before anything runs) records
/// symbolic refs, ensured expressions and notes whose phonemes will change; execution records created and
/// removed objects and the parts that were touched.
/// </summary>
public sealed class PlanContext(UProject project) {
    public UProject Project { get; } = project;
    public List<Warning> Warnings { get; } = new();

    // Planning
    readonly Dictionary<string, string> refKinds = new();
    public Dictionary<string, UExpressionDescriptor> Ensured { get; } = new();
    /// <summary>Notes (id or ref) created or re-lyricked in this plan: their phonemes are not known yet.</summary>
    public HashSet<string> PhonemesPending { get; } = new();

    // Execution
    readonly Dictionary<string, object> refObjects = new();
    readonly Dictionary<UNote, UVoicePart> refNoteParts = new();
    public Dictionary<string, string> Created { get; } = new();
    public List<string> Removed { get; } = new();
    public Dictionary<UVoicePart, int> OverlapsBefore { get; } = new();

    readonly List<Action> restores = new();

    /// <summary>
    /// Registers how to put back a direct write (mixer, comments: OpenUtau has no command for them, so rolling back the
    /// undo group does not revert them). Called right before the write.
    /// </summary>
    public void OnRollback(Action restore) => restores.Add(restore);

    /// <summary>Reverts the direct writes of a failed plan, newest first. Errors are logged, not thrown.</summary>
    public void RestoreDirectWrites() {
        for (int i = restores.Count - 1; i >= 0; i--) {
            try {
                restores[i]();
            } catch (Exception e) {
                Serilog.Log.Warning(e, $"{BridgeHost.LogPrefix} could not revert a direct write after a failed plan");
            }
        }
        restores.Clear();
    }

    public bool IsRef(string value) => value.StartsWith('@');

    public void DeclareRef(string? reference, string kind, int opIndex) {
        if (reference is null) {
            return;
        }
        if (!IsRef(reference) || reference.Length < 2) {
            throw BridgeException.InvalidParams($"ops[{opIndex}].ref must look like \"@name\".", $"ops[{opIndex}].ref");
        }
        if (!refKinds.TryAdd(reference, kind)) {
            throw BridgeException.InvalidParams($"Ref {reference} is declared twice.", $"ops[{opIndex}].ref");
        }
    }

    /// <summary>Planning-time check that an id or ref of the given kind can be resolved.</summary>
    public void ValidateRef(string value, string kind, int opIndex) {
        if (IsRef(value)) {
            if (!refKinds.TryGetValue(value, out var declared) || declared != kind && !(kind == "anyPart" && declared is "part" or "wavePart")) {
                throw BridgeException.InvalidParams(
                    $"{value} is not a {kind} created by an earlier op of this plan.", $"ops[{opIndex}]");
            }
            return;
        }
        switch (kind) {
            case "note": BridgeHost.Ids.ResolveNote(Project, value); break;
            case "part": BridgeHost.Ids.ResolveVoicePart(Project, value); break;
            case "anyPart": BridgeHost.Ids.ResolvePart(Project, value); break;
            case "track": BridgeHost.Ids.ResolveTrack(Project, value); break;
        }
    }

    /// <summary>Expression descriptor registered in the project or ensured earlier in the plan.</summary>
    public UExpressionDescriptor Descriptor(string abbr, int opIndex) {
        if (Project.expressions.TryGetValue(abbr, out var descriptor) || Ensured.TryGetValue(abbr, out descriptor)) {
            return descriptor;
        }
        throw new BridgeException(ErrorCodes.UnknownExpression,
            $"Expression '{abbr}' is not registered in the project.",
            new() {
                ["abbr"] = abbr,
                ["opIndex"] = opIndex,
                ["known"] = new System.Text.Json.Nodes.JsonArray(Project.expressions.Keys.Select(k => (System.Text.Json.Nodes.JsonNode?)k).ToArray()),
            });
    }

    // Execution-time resolution (refs exist only after their op ran).

    public void Bind(string? reference, string opKey, object target, string id, UVoicePart? notePart = null, bool report = true) {
        if (report) {
            Created[reference ?? opKey] = id;
        }
        if (reference is not null) {
            refObjects[reference] = target;
        }
        if (target is UNote note && notePart is not null) {
            refNoteParts[note] = notePart;
        }
    }

    public (UNote note, UVoicePart part) Note(string value) {
        if (IsRef(value)) {
            var note = (UNote)refObjects[value];
            return (note, refNoteParts[note]);
        }
        return BridgeHost.Ids.ResolveNote(Project, value);
    }

    public UVoicePart Part(string value) =>
        IsRef(value) ? (UVoicePart)refObjects[value] : BridgeHost.Ids.ResolveVoicePart(Project, value);

    public UPart AnyPart(string value) =>
        IsRef(value) ? (UPart)refObjects[value] : BridgeHost.Ids.ResolvePart(Project, value);

    public UTrack Track(string value) =>
        IsRef(value) ? (UTrack)refObjects[value] : BridgeHost.Ids.ResolveTrack(Project, value);

    /// <summary>Call before an op modifies a part: remembers its overlap count for the final check.</summary>
    public void Touch(UVoicePart part) {
        if (!OverlapsBefore.ContainsKey(part)) {
            OverlapsBefore[part] = CountOverlaps(part);
        }
    }

    public static int CountOverlaps(UVoicePart part) {
        int count = 0, end = int.MinValue;
        foreach (var note in part.notes) {
            if (note.position < end) {
                count++;
            }
            end = Math.Max(end, note.End);
        }
        return count;
    }

    public UTrack TrackOf(UVoicePart part) => Project.tracks[part.trackNo];

    public static DocManager Doc => DocManager.Inst;
}
