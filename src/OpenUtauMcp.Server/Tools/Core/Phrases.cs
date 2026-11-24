using OpenUtauMcp.Contracts.Model;

namespace OpenUtauMcp.Server.Tools.Core;

/// <summary>
/// Phrase detection for reading notes: a phrase is a run of notes without gaps between them.
/// Breath notes (AP, SP, br) are not part of phrases and act as separators. Numbered from 1.
/// </summary>
public static class Phrases {
    static readonly HashSet<string> breaths = new(StringComparer.OrdinalIgnoreCase) { "AP", "SP", "br", "breath" };

    public sealed record Phrase(int Number, IReadOnlyList<NoteDto> Notes) {
        public string Text => string.Join(' ', Notes.Select(n => n.Lyric).Where(l => !l.StartsWith('+') && l != "-"));
    }

    public static bool IsBreath(NoteDto note) => breaths.Contains(note.Lyric);

    /// <summary>Notes must be sorted by position. Returns the phrases and the phrase number of each note id.</summary>
    public static (IReadOnlyList<Phrase> phrases, IReadOnlyDictionary<string, int> phraseOf) Detect(IReadOnlyList<NoteDto> notes) {
        var phrases = new List<Phrase>();
        var phraseOf = new Dictionary<string, int>();
        List<NoteDto>? current = null;
        int lastEnd = int.MinValue;
        foreach (var note in notes) {
            if (IsBreath(note)) {
                Close();
                lastEnd = int.MinValue;
                continue;
            }
            if (current is null || note.Pos > lastEnd) {
                Close();
                current = [];
            }
            current.Add(note);
            phraseOf[note.Id] = phrases.Count + 1;
            lastEnd = Math.Max(lastEnd, note.Pos + note.Dur);
        }
        Close();
        return (phrases, phraseOf);

        void Close() {
            if (current is { Count: > 0 }) {
                phrases.Add(new Phrase(phrases.Count + 1, current));
            }
            current = null;
        }
    }
}
