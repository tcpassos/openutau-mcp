using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;

namespace OpenUtauMcp.Server.Files;

/// <summary>
/// A project built in memory by applying creation ops, for ou_music_compose
/// target "file": the same plan that would go to edit.apply is applied here and written with <see cref="UstxWriter"/>.
/// No undo, no phonemization, no OpenUtau. Refs ("@...") resolve to the objects created by earlier ops.
/// </summary>
public sealed class InMemoryProject {
    public sealed class Track {
        public string Name { get; set; } = "Track";
        public string? Singer { get; set; }
        public string? Phonemizer { get; set; }
        public string? Renderer { get; set; }
        public string Color { get; set; } = "Blue";
        public bool Mute { get; set; }
        public bool Solo { get; set; }
        public double Volume { get; set; }
        public double Pan { get; set; }
    }

    public sealed class Note {
        public int Pos { get; set; }
        public int Dur { get; set; }
        public int Tone { get; set; }
        public string Lyric { get; set; } = "a";
        public int Tuning { get; set; }
        public List<PitchPointDto> Pitch { get; set; } = [new(-40, 0, "io"), new(40, 0, "io")];
        public bool SnapFirst { get; set; } = true;
        public VibratoDto Vibrato { get; set; } = new() { Length = 0, Period = 175, Depth = 25, In = 10, Out = 10 };
        public Dictionary<string, float> Exprs { get; } = new();
    }

    public sealed class Part {
        public required Track Track { get; set; }
        public string Name { get; set; } = "New Part";
        public string Comment { get; set; } = "";
        public int Pos { get; set; }
        public int Dur { get; set; }
        public List<Note> Notes { get; } = [];
        public SortedDictionary<string, SortedDictionary<int, int>> Curves { get; } = new(StringComparer.Ordinal);
    }

    public string Name { get; set; } = "New Project";
    public string Comment { get; set; } = "";
    public double Bpm { get; set; } = 120;
    public int BeatPerBar { get; set; } = 4;
    public int BeatUnit { get; set; } = 4;
    public int Key { get; set; }
    public List<Track> Tracks { get; } = [];
    public List<Part> Parts { get; } = [];
    /// <summary>Expressions beyond OpenUtau's defaults, registered by project.expressions.</summary>
    public Dictionary<string, ExpressionDescriptorDto> Expressions { get; } = new(StringComparer.Ordinal);

    readonly Dictionary<string, object> refs = new(StringComparer.Ordinal);
    readonly Func<string, ExpressionDescriptorDto?> describe;

    /// <param name="describe">Descriptor of an expression to register (from the bridge catalog, or a built-in table).</param>
    public InMemoryProject(Func<string, ExpressionDescriptorDto?> describe) => this.describe = describe;

    public void Apply(IEnumerable<EditOp> ops) {
        foreach (var op in ops) {
            Apply(op);
        }
    }

    void Apply(EditOp op) {
        switch (op) {
            case ProjectUpdateOp u:
                Bpm = u.Bpm ?? Bpm;
                if (u.TimeSig is { } sig) {
                    (BeatPerBar, BeatUnit) = (sig.BeatPerBar, sig.BeatUnit);
                }
                Key = u.Key ?? Key;
                Comment = u.Comment ?? Comment;
                if (u.TempoAdd is { Count: > 0 } || u.TimeSigAdd is { Count: > 0 }) {
                    throw new NotSupportedException("Files written without OpenUtau support one tempo and one time signature.");
                }
                break;
            case ProjectExpressionsOp e:
                foreach (var abbr in e.Ensure.Where(a => !UstxWriter.DefaultExpressions.Contains(a) && !Expressions.ContainsKey(a))) {
                    Expressions[abbr] = describe(abbr) ?? throw new InvalidOperationException($"Unknown expression '{abbr}'.");
                }
                break;
            case TrackAddOp t: {
                var track = t.CopyFrom is { } from ? Copy(Resolve<Track>(from)) : new Track();
                track.Name = t.Name ?? track.Name;
                track.Singer = t.SingerId ?? track.Singer;
                track.Color = t.Color ?? track.Color;
                Tracks.Add(track);
                Bind(t.Ref, track);
                break;
            }
            case TrackUpdateOp t: {
                var track = Resolve<Track>(t.TrackId);
                track.Name = t.Name ?? track.Name;
                track.Color = t.Color ?? track.Color;
                track.Singer = t.SingerId ?? track.Singer;
                track.Phonemizer = t.Phonemizer ?? track.Phonemizer;
                track.Renderer = t.Renderer ?? track.Renderer;
                break;
            }
            case TrackMixOp m: {
                var track = Resolve<Track>(m.TrackId);
                track.Volume = m.VolumeDb ?? track.Volume;
                track.Pan = m.Pan ?? track.Pan;
                track.Mute = m.Mute ?? track.Mute;
                track.Solo = m.Solo ?? track.Solo;
                break;
            }
            case PartAddOp p: {
                var part = new Part { Track = Resolve<Track>(p.TrackId), Pos = p.Pos, Dur = p.Dur, Name = p.Name ?? "New Part" };
                Parts.Add(part);
                Bind(p.Ref, part);
                break;
            }
            case PartCommentOp c:
                Resolve<Part>(c.PartId).Comment = c.Comment;
                break;
            case NoteAddOp a: {
                var part = Resolve<Part>(a.PartId);
                var note = new Note { Pos = a.Pos, Dur = a.Dur, Tone = a.Tone, Lyric = a.Lyric, Tuning = a.Tuning ?? 0 };
                if (a.Pitch is { } pitch) {
                    note.Pitch = pitch.Points.ToList();
                    note.SnapFirst = pitch.SnapFirst;
                }
                if (a.Vibrato is { } v) {
                    note.Vibrato = Patch(note.Vibrato, v);
                }
                foreach (var (abbr, value) in a.Exprs ?? new Dictionary<string, float>()) {
                    note.Exprs[abbr] = value;
                }
                if (a.Pos + a.Dur > part.Dur) {
                    part.Dur = a.Pos + a.Dur;
                }
                part.Notes.Add(note);
                Bind(a.Ref, note);
                break;
            }
            case NotePitchOp p:
                foreach (var note in p.NoteIds.Select(Resolve<Note>)) {
                    note.Pitch = p.Pitch?.Points.ToList() ?? [new(-40, 0, "io"), new(40, 0, "io")];
                    note.SnapFirst = p.Pitch?.SnapFirst ?? true;
                }
                break;
            case NoteVibratoOp v:
                foreach (var note in v.NoteIds.Select(Resolve<Note>)) {
                    note.Vibrato = v.Reset || v.Vibrato is null ? new Note().Vibrato : Patch(note.Vibrato, v.Vibrato);
                }
                break;
            case NoteExprOp e:
                if (e.Values is not null) {
                    throw new NotSupportedException("Per-phoneme expressions need OpenUtau's phonemizer.");
                }
                foreach (var note in e.NoteIds.Select(Resolve<Note>)) {
                    if (e.Value is float value) {
                        note.Exprs[e.Abbr] = value;
                    } else {
                        note.Exprs.Remove(e.Abbr);
                    }
                }
                break;
            case NoteRemoveOp r:
                foreach (var id in r.NoteIds) {
                    var note = Resolve<Note>(id);
                    foreach (var part in Parts) {
                        part.Notes.Remove(note);
                    }
                }
                break;
            case CurveReplaceOp c:
                Resolve<Part>(c.PartId).Curves[c.Abbr] = new SortedDictionary<int, int>(c.Xs.Zip(c.Ys).ToDictionary(p => p.First, p => p.Second));
                break;
            case CurveSetRangeOp c: {
                var part = Resolve<Part>(c.PartId);
                var curve = part.Curves.TryGetValue(c.Abbr, out var existing) ? existing : part.Curves[c.Abbr] = new SortedDictionary<int, int>();
                foreach (var x in curve.Keys.Where(x => x >= c.FromTick && x <= c.ToTick).ToList()) {
                    curve.Remove(x);
                }
                foreach (var (x, y) in c.Xs.Zip(c.Ys)) {
                    curve[x] = y;
                }
                break;
            }
            case CurveClearOp c: {
                var part = Resolve<Part>(c.PartId);
                if (c.FromTick is null && c.ToTick is null) {
                    part.Curves.Remove(c.Abbr);
                } else if (part.Curves.TryGetValue(c.Abbr, out var curve)) {
                    foreach (var x in curve.Keys.Where(x => x >= (c.FromTick ?? 0) && x <= (c.ToTick ?? int.MaxValue)).ToList()) {
                        curve.Remove(x);
                    }
                }
                break;
            }
            default:
                throw new NotSupportedException($"{op.GetType().Name} is not supported when writing a file.");
        }
    }

    static Track Copy(Track t) => new() {
        Name = t.Name, Singer = t.Singer, Phonemizer = t.Phonemizer, Renderer = t.Renderer, Color = t.Color, Volume = t.Volume, Pan = t.Pan,
    };

    static VibratoDto Patch(VibratoDto v, VibratoPatch p) => v with {
        Length = p.Length ?? v.Length, Period = p.Period ?? v.Period, Depth = p.Depth ?? v.Depth, In = p.In ?? v.In,
        Out = p.Out ?? v.Out, Shift = p.Shift ?? v.Shift, Drift = p.Drift ?? v.Drift, VolLink = p.VolLink ?? v.VolLink,
    };

    void Bind(string? reference, object target) {
        if (reference is not null) {
            refs[reference] = target;
        }
    }

    T Resolve<T>(string id) where T : class =>
        refs.TryGetValue(id, out var target) && target is T typed ? typed
        : throw new InvalidOperationException($"'{id}' is not a {typeof(T).Name.ToLowerInvariant()} created earlier in this plan.");
}
