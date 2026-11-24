using OpenUtau.Core.Ustx;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Protocol;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>notes.query. UI thread only.</summary>
public static class NotesAdapter {
    public static NotesQueryResult Query(UProject project, NotesQueryParams p) {
        var ids = BridgeHost.Ids;
        var part = ids.ResolveVoicePart(project, p.PartId);
        int limit = p.Limit == 0 ? 500 : p.Limit;  // 0 = omitted by a raw client
        if (limit is < 1 || limit > ProtocolInfo.MaxNotesPerQuery) {
            throw BridgeException.OutOfRange("limit", limit, 1, ProtocolInfo.MaxNotesPerQuery);
        }
        if (p.Offset < 0) {
            throw BridgeException.OutOfRange("offset", p.Offset, 0, int.MaxValue);
        }
        var fields = new HashSet<string>(p.Fields ?? [], StringComparer.OrdinalIgnoreCase);

        IEnumerable<UNote> selected = part.notes;
        if (p.Range is { } range) {
            selected = selected.Where(n => n.position + part.position >= range.FromTick && n.position + part.position < range.ToTick);
        }
        if (p.Ids is { Count: > 0 }) {
            var wanted = p.Ids.Select(id => ids.ResolveNote(project, id))
                .Where(r => ReferenceEquals(r.part, part)).Select(r => r.note).ToHashSet();
            selected = selected.Where(wanted.Contains);
        }
        var all = selected.ToList();
        var page = all.Skip(p.Offset).Take(limit).Select(n => ToDto(n, part, fields)).ToList();
        int next = p.Offset + page.Count;
        return new NotesQueryResult {
            PartId = ids.IdOf(part),
            PartPos = part.position,
            PartRev = BridgeHost.Revisions.PartRev(part),
            Epoch = ids.Epoch,
            Total = all.Count,
            Notes = page,
            NextOffset = next < all.Count ? next : null,
        };
    }

    static NoteDto ToDto(UNote n, UVoicePart part, HashSet<string> fields) => new() {
        Id = BridgeHost.Ids.IdOf(n, part),
        Pos = n.position,
        Dur = n.duration,
        Tone = n.tone,
        Lyric = n.lyric,
        Tuning = n.tuning,
        Vibrato = fields.Contains("vibrato") ? new VibratoDto {
            Length = n.vibrato.length, Period = n.vibrato.period, Depth = n.vibrato.depth, In = n.vibrato.@in,
            Out = n.vibrato.@out, Shift = n.vibrato.shift, Drift = n.vibrato.drift, VolLink = n.vibrato.volLink,
        } : null,
        Pitch = fields.Contains("pitch") ? new PitchDto {
            SnapFirst = n.pitch.snapFirst,
            Points = n.pitch.data.Select(pt => new PitchPointDto(pt.X, pt.Y, pt.shape.ToString())).ToList(),
        } : null,
        Exprs = fields.Contains("exprs")
            ? n.phonemeExpressions.Select(e => new NoteExpressionDto(e.abbr, e.index, e.value)).ToList()
            : null,
        Error = fields.Contains("errors") ? n.Error : null,
        OverlapError = fields.Contains("errors") ? n.OverlapError : null,
        PosMs = fields.Contains("ms") ? Math.Round(n.PositionMs, 1) : null,
        EndMs = fields.Contains("ms") ? Math.Round(n.EndMs, 1) : null,
        Phonemes = fields.Contains("phonemes") ? Phonemes(n, part) : null,
        Phonemizer = fields.Contains("phonemes") ? n.PhonemizerOverride : null,
    };

    static List<PhonemeDto> Phonemes(UNote n, UVoicePart part) => part.phonemes
        .Where(ph => ph.Parent == n)
        .Select(ph => {
            var o = n.phonemeOverrides.FirstOrDefault(x => x.index == ph.index);
            return new PhonemeDto {
                Index = ph.index,
                Phoneme = ph.phoneme,
                Pos = ph.position - n.position,
                Dur = ph.Duration,
                PosMs = Math.Round(ph.PositionMs - n.PositionMs, 1),
                DurMs = Math.Round(ph.DurationMs, 1),
                Alias = string.IsNullOrEmpty(o?.phoneme) ? null : o.phoneme,
                Offset = o?.offset,
                PreutterDeltaMs = o?.preutterDelta,
                OverlapDeltaMs = o?.overlapDelta,
                Error = ph.Error,
            };
        }).ToList();
}
