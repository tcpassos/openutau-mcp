using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtauMcp.Contracts.Model;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>Project structure and status (catalog section 1, section 7). UI thread only.</summary>
public static class ProjectAdapter {
    static readonly string[] defaultInclude = ["tempo", "tracks", "parts"];

    public static StatusResult Status(UProject project) {
        var doc = DocManager.Inst;
        return new StatusResult {
            Project = new StatusProject {
                Name = project.name,
                FilePath = string.IsNullOrEmpty(project.FilePath) ? null : project.FilePath,
                ChangesSaved = doc.ChangesSaved,
                Epoch = BridgeHost.Ids.Epoch,
                StructureRev = BridgeHost.Revisions.StructureRev,
                Tracks = project.tracks.Count,
                Parts = project.parts.Count,
                Bpm = project.tempos.FirstOrDefault()?.bpm ?? 0,
            },
            Playback = PlaybackAdapter.Get() is var playback ? new StatusPlayback(playback.State, playback.Tick) : null!,
            History = HistoryAdapter.Get(),
            RecentErrors = BridgeHost.Events.RecentErrors(5) is { Count: > 0 } errors ? errors : null,
            Jobs = BridgeHost.Jobs.Recent() is { Count: > 0 } jobs ? jobs : null,
        };
    }

    public static ProjectDto Get(UProject project, ProjectGetParams p) {
        var include = new HashSet<string>(p.Include ?? defaultInclude, StringComparer.OrdinalIgnoreCase);
        var ids = BridgeHost.Ids;
        return new ProjectDto {
            Epoch = ids.Epoch,
            StructureRev = BridgeHost.Revisions.StructureRev,
            Name = project.name,
            FilePath = string.IsNullOrEmpty(project.FilePath) ? null : project.FilePath,
            Resolution = project.resolution,
            Key = project.key,
            Tempos = include.Contains("tempo") ? project.tempos.Select(t => new TempoDto(t.position, t.bpm)).ToList() : null,
            TimeSignatures = include.Contains("tempo")
                ? project.timeSignatures.Select(s => new TimeSignatureDto(s.barPosition, s.beatPerBar, s.beatUnit)).ToList()
                : null,
            Tracks = include.Contains("tracks") ? project.tracks.Select((t, i) => Track(t, i)).ToList() : null,
            Parts = include.Contains("parts") ? project.parts.Select(part => Part(project, part)).ToList() : null,
            Expressions = include.Contains("expressions") ? project.expressions.Values.Select(Descriptor).ToList() : null,
            Comment = include.Contains("comment") ? project.comment : null,
        };
    }

    static TrackDto Track(UTrack track, int index) => new() {
        Id = BridgeHost.Ids.IdOf(track),
        Index = index,
        Name = track.TrackName,
        Singer = track.Singer?.Id ?? track.singer,
        SingerFound = track.Singer?.Found ?? false,
        Phonemizer = track.Phonemizer?.GetType().FullName,
        Renderer = track.RendererSettings?.renderer,
        VolumeDb = track.Volume,
        Pan = track.Pan,
        Mute = track.Mute,
        Solo = track.Solo,
        VoiceColors = track.VoiceColorNames is { Length: > 0 } colors && !(colors.Length == 1 && colors[0] == "") ? colors : null,
    };

    static PartDto Part(UProject project, UPart part) => new() {
        Id = BridgeHost.Ids.IdOf(part),
        Name = part.name,
        TrackId = part.trackNo >= 0 && part.trackNo < project.tracks.Count ? BridgeHost.Ids.IdOf(project.tracks[part.trackNo]) : "",
        Type = part is UVoicePart ? "voice" : "wave",
        Pos = part.position,
        Dur = part.Duration,
        NoteCount = part is UVoicePart voice ? voice.notes.Count : 0,
        PartRev = BridgeHost.Revisions.PartRev(part),
        Comment = string.IsNullOrEmpty(part.comment) ? null : part.comment,
    };

    static ExpressionDescriptorDto Descriptor(UExpressionDescriptor d) => new() {
        Abbr = d.abbr,
        Name = d.name,
        Type = d.type switch {
            UExpressionType.Numerical => "numerical",
            UExpressionType.Options => "options",
            UExpressionType.Curve => "curve",
            _ => d.type.ToString().ToLowerInvariant(),
        },
        Min = d.min,
        Max = d.max,
        DefaultValue = d.defaultValue,
        Options = d.options is { Length: > 0 } ? d.options : null,
    };
}
