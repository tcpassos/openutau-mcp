using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Server.Bridge;
using OpenUtauMcp.Server.Units;

namespace OpenUtauMcp.Server.Tools.Core;

public sealed record RangeInput(
    [property: Description("Start, bar:beat (inclusive).")] string? From = null,
    [property: Description("End, bar:beat (exclusive; default: part end).")] string? To = null,
    [property: Description("Or bars, e.g. \"17-24\".")] string? Bars = null);

public sealed record NoteSelector(
    [property: Description("Part id or exact name.")] string Part,
    [property: Description("Only notes starting in this range.")] RangeInput? Range = null,
    [property: Description("Only these note ids.")] string[]? Ids = null,
    [property: Description("Phrase numbers (from 1).")] int[]? Phrases = null,
    [property: Description("Only notes whose lyric matches this regex.")] string? Lyric = null);

[McpServerToolType]
public static class NotesGetTool {
    static readonly string[] fullFields = ["vibrato", "pitch", "exprs", "errors", "ms"];
    static readonly string[] derivedFields = ["phonemes", "phonemizer"];

    [McpServerTool(Name = "ou_notes_get", Title = "Read notes", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Reads notes of one part as a compact table (id, position bar:beat, duration in quarters, pitch name, lyric) " +
                 "plus the list of phrases. Note ids stay valid while the project is open, even after edits. " +
                 "detail=summary gives counts and phrases only; detail=full adds the requested fields. " +
                 "Example: {\"select\":{\"part\":\"Lead D\",\"range\":{\"bars\":\"9-16\"}}}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge,
        [Description("Which notes to read.")] NoteSelector select,
        [Description("summary | compact (default) | full")] string detail = "compact",
        [Description("With detail=full: any of vibrato, pitch, exprs, errors, ms (default: all of these), phonemes, phonemizer. " +
                     "phonemes = [phoneme, start ms from the note, length ms, overrides?]; waits for phonemization.")] string[]? fields = null,
        [Description("With phonemes: how long to wait for OpenUtau to phonemize, in ms (default 5000).")] int awaitMs = 5000,
        [Description("musical (default: bar:beat, quarters, note names) | raw (ticks, MIDI)")] string units = "musical",
        [Description("Max notes per page (1-500).")] int limit = 200,
        [Description("Cursor from a previous page (page.next).")] string? cursor = null,
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            if (detail is not ("summary" or "compact" or "full")) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"Unknown detail '{detail}'. Use summary, compact or full.");
            }
            if (units is not ("musical" or "raw")) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"Unknown units '{units}'. Use musical or raw.");
            }
            limit = Math.Clamp(limit, 1, 500);
            int offset = ParseCursor(cursor);
            var ctx = await ProjectContext.LoadAsync(bridge, ct).ConfigureAwait(false);
            var part = ctx.ResolvePart(select.Part);
            if (part.Type != "voice") {
                throw new ToolInputException("WRONG_KIND", $"Part {part.Id} is an audio part and has no notes.");
            }
            var requested = detail == "full" ? (fields is { Length: > 0 } ? fields : fullFields) : null;
            if (requested is not null && requested.Except(fullFields.Concat(derivedFields)).FirstOrDefault() is { } unknown) {
                throw new ToolInputException(ToolInputException.InvalidParams,
                    $"Unknown field '{unknown}'. Use {string.Join(", ", fullFields.Concat(derivedFields))}.");
            }
            bool derived = requested?.Intersect(derivedFields).Any() == true;
            var bridgeFields = requested?.Select(f => f == "phonemizer" ? "phonemes" : f).Distinct().ToList();
            var result = await bridge.AllNotesAsync(part.Id, bridgeFields, ct, derived ? Math.Clamp(awaitMs, 0, 15000) : null).ConfigureAwait(false);
            var notes = result.Notes;
            var (phrases, _) = Phrases.Detect(notes);
            var chosen = Filter(notes, part, ctx.Time, select.Range, select.Ids, select.Phrases, select.Lyric);
            var chosenPhrases = phrases.Where(ph => ph.Notes.Any(n => chosen.Contains(n))).ToList();
            bool musical = units == "musical";
            string Pos(int rel) => musical ? ctx.Time.FormatPosition(part.Pos + rel) : (part.Pos + rel).ToString(CultureInfo.InvariantCulture);

            var data = new JsonObject {
                ["part"] = part.Id,
                ["partName"] = part.Name,
                ["partRev"] = result.PartRev,
                ["epoch"] = result.Epoch,
            };
            var phraseList = new JsonArray(chosenPhrases.Select(ph => (JsonNode?)new JsonObject {
                ["n"] = ph.Number, ["firstId"] = ph.Notes[0].Id, ["at"] = Pos(ph.Notes[0].Pos), ["text"] = ph.Text,
            }).ToArray());

            if (detail == "summary") {
                var sung = chosen.Where(n => !Phrases.IsBreath(n)).ToList();
                data["notes"] = chosen.Count;
                if (sung.Count > 0) {
                    data["from"] = Pos(chosen[0].Pos);
                    data["to"] = Pos(chosen[^1].Pos + chosen[^1].Dur);
                    data["lowest"] = Pitches.Format(sung.Min(n => n.Tone));
                    data["highest"] = Pitches.Format(sung.Max(n => n.Tone));
                }
                data["phrases"] = phraseList;
                return ToolResult.Ok($"{chosen.Count} notes, {chosenPhrases.Count} phrases in {part.Id} ({part.Name}).", data);
            }

            var page = chosen.Skip(offset).Take(limit).ToList();
            var cols = new List<string> { "id", "pos", "dur", "pitch", "lyric" };
            if (requested is not null) {
                cols.AddRange(requested);
            }
            var rows = page.Select(n => {
                var row = new List<JsonNode?> {
                    n.Id, Pos(n.Pos),
                    musical ? Durations.Format(n.Dur) : n.Dur,
                    musical ? Pitches.Format(n.Tone) + (n.Tuning != 0 ? $"{n.Tuning:+0;-0}c" : "") : n.Tone,
                    n.Lyric,
                };
                if (requested is not null) {
                    row.AddRange(requested.Select(f => FullField(n, f)));
                }
                return row.ToArray();
            });
            data["cols"] = new JsonArray(cols.Select(c => (JsonNode?)c).ToArray());
            data["rows"] = new JsonArray(rows.Select(r => (JsonNode?)new JsonArray(r)).ToArray());
            data["phrases"] = phraseList;
            int next = offset + page.Count;
            var pageInfo = new JsonObject { ["total"] = chosen.Count };
            if (next < chosen.Count) {
                pageInfo["next"] = $"o:{next}";
            }
            data["page"] = pageInfo;
            string more = next < chosen.Count ? $" More with cursor \"o:{next}\"." : "";
            return ToolResult.Ok($"{page.Count} of {chosen.Count} notes from {part.Id} ({part.Name}), partRev {result.PartRev}.{more}", data, result.Warnings);
        });

    /// <summary>Notes of one part matching every given criterion (range, ids, phrases, lyric regex).</summary>
    internal static List<NoteDto> Filter(IReadOnlyList<NoteDto> notes, PartDto part, TimeMap time,
                                         RangeInput? range, string[]? ids, int[]? phrases, string? lyric) {
        IEnumerable<NoteDto> selected = notes;
        if (range is not null) {
            var (from, to) = ResolveRange(time, range, part);
            selected = selected.Where(n => part.Pos + n.Pos >= from && part.Pos + n.Pos < to);
        }
        if (ids is { Length: > 0 }) {
            var set = ids.ToHashSet();
            selected = selected.Where(n => set.Contains(n.Id));
        }
        if (phrases is { Length: > 0 }) {
            var (_, phraseOf) = Phrases.Detect(notes);
            var set = phrases.ToHashSet();
            selected = selected.Where(n => phraseOf.TryGetValue(n.Id, out int ph) && set.Contains(ph));
        }
        if (lyric is not null) {
            Regex regex;
            try {
                regex = new Regex(lyric, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            } catch (ArgumentException e) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"Invalid lyric regex: {e.Message}");
            }
            selected = selected.Where(n => regex.IsMatch(n.Lyric));
        }
        return selected.ToList();
    }

    internal static (int from, int to) ResolveRange(TimeMap time, RangeInput range, PartDto part) {
        if (range.Bars is { } bars) {
            return time.ParseBars(bars);
        }
        int from = range.From is { } f ? time.ParsePosition(f) : part.Pos;
        int to = range.To is { } t ? time.ParsePosition(t) : part.Pos + part.Dur;
        return (from, to);
    }

    static int ParseCursor(string? cursor) {
        if (cursor is null) {
            return 0;
        }
        if (cursor.StartsWith("o:", StringComparison.Ordinal) && int.TryParse(cursor.AsSpan(2), out int offset) && offset >= 0) {
            return offset;
        }
        throw new ToolInputException(ToolInputException.InvalidParams, $"Invalid cursor '{cursor}'. Pass page.next from a previous call.");
    }

    static JsonArray PhonemeCell(PhonemeDto p) {
        var cell = new JsonArray(p.Phoneme, p.PosMs, p.DurMs);
        var o = new JsonObject();
        if (p.Alias is not null) o["alias"] = p.Alias;
        if (p.Offset is int off) o["offset"] = off;
        if (p.PreutterDeltaMs is float pre) o["preutterDeltaMs"] = pre;
        if (p.OverlapDeltaMs is float ovl) o["overlapDeltaMs"] = ovl;
        if (p.Error) o["error"] = true;
        if (o.Count > 0) cell.Add(o);
        return cell;
    }

    static JsonNode? FullField(NoteDto n, string field) => field switch {
        "vibrato" => n.Vibrato is { Length: > 0 } v ? new JsonObject {
            ["length"] = v.Length, ["period"] = v.Period, ["depth"] = v.Depth, ["in"] = v.In, ["out"] = v.Out,
            ["shift"] = v.Shift, ["drift"] = v.Drift, ["volLink"] = v.VolLink,
        } : null,
        // Pitch points are shown as [x ms, y cents, shape]; OpenUtau stores y in tenths of a semitone.
        "pitch" => n.Pitch is { } p ? new JsonArray(p.Points.Select(pt => (JsonNode?)new JsonArray(pt.X, pt.Y * 10, pt.Shape)).ToArray()) : null,
        "exprs" => n.Exprs is { Count: > 0 } e ? new JsonArray(e.Select(x => (JsonNode?)new JsonObject {
            ["abbr"] = x.Abbr, ["index"] = x.Index, ["value"] = x.Value,
        }).ToArray()) : null,
        "errors" => n.Error == true || n.OverlapError == true ? new JsonObject { ["error"] = n.Error, ["overlap"] = n.OverlapError } : null,
        "ms" => new JsonArray(n.PosMs, n.EndMs),
        "phonemes" => n.Phonemes is { } ph ? new JsonArray(ph.Select(x => (JsonNode?)PhonemeCell(x)).ToArray()) : null,
        "phonemizer" => n.Phonemizer,
        _ => null,
    };
}
