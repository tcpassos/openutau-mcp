using System.Runtime.CompilerServices;
using OpenUtau.Core.Ustx;
using OpenUtauMcp.Contracts.Errors;

namespace OpenUtauMcp.Bridge.Identity;

/// <summary>
/// Stable ids for tracks (t), parts (p) and notes (n), assigned by object identity.
/// Counters never reset, so an id is unique across epochs; ids of previous epochs answer STALE_EPOCH.
/// A live object only resolves while it is a member of the current project (removed objects are kept
/// alive by the undo queue and answer NOT_FOUND reason "removed"; undoing the removal revives the id).
/// All members must be called on the OpenUtau UI thread.
/// </summary>
public sealed class IdRegistry {
    ConditionalWeakTable<object, string> idOf = new();
    Dictionary<string, WeakReference<object>> byId = new();
    ConditionalWeakTable<UNote, UVoicePart> lastPartOf = new();
    readonly HashSet<string> staleIds = new();
    WeakReference<UProject>? project;
    long nextTrack = 1, nextPart = 1, nextNote = 1;

    public int Epoch { get; private set; }

    /// <summary>Starts a new epoch when the project object changed (open / new / reload).</summary>
    public UProject Sync(UProject current) {
        if (project != null && project.TryGetTarget(out var known) && ReferenceEquals(known, current)) {
            return current;
        }
        if (project != null) {
            staleIds.UnionWith(byId.Keys);
            byId = new();
            idOf = new();
            lastPartOf = new();
        }
        project = new WeakReference<UProject>(current);
        Epoch++;
        return current;
    }

    public string IdOf(UTrack track) => GetOrCreate(track, "t", ref nextTrack);
    public string IdOf(UPart part) => GetOrCreate(part, "p", ref nextPart);

    public string IdOf(UNote note, UVoicePart part) {
        lastPartOf.AddOrUpdate(note, part);
        return GetOrCreate(note, "n", ref nextNote);
    }

    public UTrack ResolveTrack(UProject project, string id) {
        var track = Resolve<UTrack>(id, "track");
        return project.tracks.Contains(track) ? track : throw BridgeException.NotFound("track", id, "removed");
    }

    public UPart ResolvePart(UProject project, string id) {
        var part = Resolve<UPart>(id, "part");
        return project.parts.Contains(part) ? part : throw BridgeException.NotFound("part", id, "removed");
    }

    public UVoicePart ResolveVoicePart(UProject project, string id) =>
        ResolvePart(project, id) as UVoicePart
        ?? throw new BridgeException(ErrorCodes.WrongKind, $"Part {id} is an audio part, not a voice part.");

    public (UNote note, UVoicePart part) ResolveNote(UProject project, string id) {
        var note = Resolve<UNote>(id, "note");
        if (lastPartOf.TryGetValue(note, out var last) && IsMember(project, last, note)) {
            return (note, last);
        }
        foreach (var part in project.parts.OfType<UVoicePart>()) {
            if (IsMember(project, part, note)) {
                lastPartOf.AddOrUpdate(note, part);
                return (note, part);
            }
        }
        throw BridgeException.NotFound("note", id, "removed");
    }

    static bool IsMember(UProject project, UVoicePart part, UNote note) =>
        project.parts.Contains(part)
        && part.notes.TryGetValue(note, out var actual)
        && ReferenceEquals(actual, note);

    T Resolve<T>(string id, string kind) where T : class {
        if (staleIds.Contains(id)) {
            throw new BridgeException(ErrorCodes.StaleEpoch,
                $"The {kind} id '{id}' belongs to a project that is no longer open.",
                new() { ["kind"] = kind, ["ref"] = id, ["epoch"] = Epoch });
        }
        if (byId.TryGetValue(id, out var weak) && weak.TryGetTarget(out var target) && target is T typed) {
            return typed;
        }
        throw BridgeException.NotFound(kind, id, "unknown");
    }

    string GetOrCreate(object target, string prefix, ref long counter) {
        if (idOf.TryGetValue(target, out var id)) {
            return id;
        }
        id = prefix + counter++;
        idOf.Add(target, id);
        byId[id] = new WeakReference<object>(target);
        return id;
    }
}
