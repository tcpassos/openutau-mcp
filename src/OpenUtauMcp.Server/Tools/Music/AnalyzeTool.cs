using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Music.Theory;
using OpenUtauMcp.Server.Bridge;
using OpenUtauMcp.Server.Tools.Core;
using OpenUtauMcp.Server.Units;

namespace OpenUtauMcp.Server.Tools.Music;

/// <summary>"Seeing" the music: phrases, range, leaps, raw stretches and problems.</summary>
[McpServerToolType]
public static class AnalyzeTool {
    static readonly string[] allReports = ["phrases", "range", "intervals", "expressiveness", "lint"];
    const int LeapSemitones = 5;
    const int ShortNoteTicks = 60;
    const int LongPhraseBeats = 32;

    [McpServerTool(Name = "ou_music_analyze", Title = "Analyze phrases, range and problems", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Analyzes notes of one part. phrases: start/end, note count, peak, rest before, text. range: lowest/highest, tessitura (10th-90th percentile), share of notes in the key. intervals: leaps of 5+ semitones, notes per bar. expressiveness: per phrase, vibrato, edited pitch, dyn and pitd, so raw phrases stand out. lint: overlaps, phoneme errors, empty lyrics, very short notes, long phrases without a breath, curves without effect for the singer, clr out of range. Issues carry a suggested fix. Example: {\"select\":{\"part\":\"Lead\"},\"report\":[\"expressiveness\",\"lint\"]}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge,
        [Description("Notes to analyze (default: the whole part).")] NoteSelector select,
        [Description("Any of phrases, range, intervals, expressiveness, lint (default: all).")] string[]? report = null,
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            var reports = report is { Length: > 0 } ? report : allReports;
            if (reports.Except(allReports).FirstOrDefault() is { } unknown) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"Unknown report '{unknown}'. Use {string.Join(", ", allReports)}.");
            }
            var ctx = await ProjectContext.LoadAsync(bridge, ct, "tempo", "tracks", "parts", "comment").ConfigureAwait(false);
            var part = ctx.ResolvePart(select.Part);
            if (part.Type != "voice") {
                throw new ToolInputException("WRONG_KIND", $"Part {part.Id} is an audio part.");
            }
            bool needDetail = reports.Contains("expressiveness") || reports.Contains("lint");
            var all = (await bridge.AllNotesAsync(part.Id, needDetail ? ["vibrato", "pitch", "exprs", "errors"] : null, ct).ConfigureAwait(false)).Notes;
            var chosen = NotesGetTool.Filter(all, part, ctx.Time, select.Range, select.Ids, select.Phrases, select.Lyric).ToHashSet();
            var (phrases, _) = Phrases.Detect(all);
            var inScope = phrases.Where(ph => ph.Notes.Any(chosen.Contains)).ToList();
            var sung = chosen.Where(n => !Phrases.IsBreath(n)).OrderBy(n => n.Pos).ToList();
            string Pos(int rel) => ctx.Time.FormatPosition(part.Pos + rel);
            var data = new JsonObject { ["part"] = part.Id, ["notes"] = sung.Count, ["phraseCount"] = inScope.Count };
            var issues = new JsonArray();

            if (reports.Contains("phrases")) {
                data["phrases"] = ProjectGetTool.Table(["n", "from", "to", "notes", "peak", "restBeforeBeats", "text"], inScope.Select(ph => {
                    var prev = all.Where(n => !Phrases.IsBreath(n) && n.Pos + n.Dur <= ph.Notes[0].Pos).OrderBy(n => n.Pos + n.Dur).LastOrDefault();
                    double? rest = prev is null ? null : Math.Round((ph.Notes[0].Pos - prev.Pos - prev.Dur) / 480.0, 2);
                    return new JsonNode?[] {
                        ph.Number, Pos(ph.Notes[0].Pos), Pos(ph.Notes[^1].Pos + ph.Notes[^1].Dur), ph.Notes.Count,
                        Pitches.Format(ph.Notes.Max(n => n.Tone)), rest, ph.Text,
                    };
                }));
            }
            if (reports.Contains("range") && sung.Count > 0) {
                var tones = sung.Select(n => n.Tone).Order().ToList();
                var scale = KeyScale(ctx);
                data["range"] = new JsonObject {
                    ["lowest"] = Pitches.Format(tones[0]),
                    ["highest"] = Pitches.Format(tones[^1]),
                    ["tessitura"] = $"{Pitches.Format(tones[(int)(tones.Count * 0.1)])}-{Pitches.Format(tones[Math.Min(tones.Count - 1, (int)(tones.Count * 0.9))])}",
                    ["key"] = scale.name,
                    ["inKeyPercent"] = Math.Round(100.0 * sung.Count(n => scale.scale.Contains(n.Tone)) / sung.Count, 1),
                };
            }
            if (reports.Contains("intervals") && sung.Count > 0) {
                var leaps = new JsonArray();
                foreach (var ph in inScope) {
                    var notes = ph.Notes;
                    for (int k = 1; k < notes.Count; k++) {
                        int jump = notes[k].Tone - notes[k - 1].Tone;
                        if (Math.Abs(jump) >= LeapSemitones && chosen.Contains(notes[k])) {
                            leaps.Add(new JsonArray(notes[k].Id, Pos(notes[k].Pos), $"{Pitches.Format(notes[k - 1].Tone)}->{Pitches.Format(notes[k].Tone)}", jump));
                        }
                    }
                }
                double bars = Math.Max(1, (sung[^1].Pos + sung[^1].Dur - sung[0].Pos) / (double)BarTicks(ctx));
                data["intervals"] = new JsonObject {
                    ["leapCols"] = new JsonArray("id", "at", "move", "semitones"),
                    ["leaps"] = leaps,
                    ["notesPerBar"] = Math.Round(sung.Count / bars, 2),
                };
            }
            if (reports.Contains("expressiveness")) {
                var curves = (await bridge.CurvesQueryAsync(new CurvesQueryParams { PartId = part.Id, Abbrs = ["dyn", "pitd"] }, ct).ConfigureAwait(false))
                    .Curves.ToDictionary(c => c.Abbr);
                var rows = new List<JsonNode?[]>();
                foreach (var ph in inScope) {
                    int from = ph.Notes[0].Pos, to = ph.Notes[^1].Pos + ph.Notes[^1].Dur;
                    int vib = ph.Notes.Count(n => n.Vibrato is { Length: > 0 });
                    int edited = ph.Notes.Count(n => n.Pitch is { } p && !IsDefaultPitch(p));
                    bool dynFlat = !curves.TryGetValue("dyn", out var dyn) || CurveFlat(dyn, from, to);
                    bool pitdEmpty = !curves.TryGetValue("pitd", out var pitd) || !pitd.Xs.Any(x => x >= from && x <= to);
                    bool raw = vib == 0 && edited == 0 && dynFlat && pitdEmpty;
                    rows.Add([ph.Number, Pos(from), vib, edited, dynFlat, pitdEmpty, raw]);
                    if (raw) {
                        issues.Add(Issue("info", ph.Notes.Select(n => n.Id), Pos(from), $"Phrase {ph.Number} ('{ph.Text}') is raw: no vibrato, default pitch, flat dyn.",
                            $"ou_music_humanize {{\"select\":{{\"part\":\"{part.Id}\",\"phrases\":[{ph.Number}]}}}}"));
                    }
                }
                data["expressiveness"] = ProjectGetTool.Table(["n", "from", "vibratoNotes", "editedPitchNotes", "dynFlat", "pitdEmpty", "raw"], rows);
            }
            if (reports.Contains("lint")) {
                await LintAsync(bridge, part, all, chosen, inScope, issues, Pos, ct).ConfigureAwait(false);
            }
            data["issues"] = issues;
            int problems = issues.Count(i => i?["severity"]?.GetValue<string>() is "warning" or "error");
            return ToolResult.Ok($"{part.Id} ({part.Name}): {sung.Count} notes, {inScope.Count} phrases, {issues.Count} issue(s) ({problems} warnings/errors).", data);
        });

    static async Task LintAsync(IBridgeClient bridge, PartDto part, IReadOnlyList<NoteDto> all, HashSet<NoteDto> chosen,
                                IReadOnlyList<Phrases.Phrase> phrases, JsonArray issues, Func<int, string> pos, CancellationToken ct) {
        var sorted = all.OrderBy(n => n.Pos).ToList();
        for (int k = 1; k < sorted.Count; k++) {
            var (a, b) = (sorted[k - 1], sorted[k]);
            if (b.Pos < a.Pos + a.Dur && (chosen.Contains(a) || chosen.Contains(b))) {
                issues.Add(Issue("warning", [a.Id, b.Id], pos(b.Pos), $"{a.Id} and {b.Id} overlap.",
                    $"ou_batch_edit {{\"name\":\"fix_overlap\",\"select\":{{\"part\":\"{part.Id}\",\"ids\":[\"{a.Id}\",\"{b.Id}\"]}}}}"));
            }
        }
        foreach (var n in chosen.OrderBy(n => n.Pos)) {
            if (n.Error == true) {
                issues.Add(Issue("warning", [n.Id], pos(n.Pos), $"{n.Id} ('{n.Lyric}') has a phoneme error.",
                    $"ou_catalog_get {{\"kind\":\"g2p\",\"track\":\"{part.TrackId}\",\"words\":[\"{n.Lyric}\"]}}, then a phonetic hint"));
            }
            if (string.IsNullOrWhiteSpace(n.Lyric)) {
                issues.Add(Issue("warning", [n.Id], pos(n.Pos), $"{n.Id} has no lyric.", "ou_lyrics_set or ou_notes_edit lyric"));
            }
            if (n.Dur < ShortNoteTicks && !Phrases.IsBreath(n)) {
                issues.Add(Issue("info", [n.Id], pos(n.Pos), $"{n.Id} is only {n.Dur} ticks long.", "ou_notes_edit dur, or merge it with its neighbor"));
            }
        }
        foreach (var ph in phrases) {
            double beats = (ph.Notes[^1].Pos + ph.Notes[^1].Dur - ph.Notes[0].Pos) / 480.0;
            if (beats > LongPhraseBeats) {
                issues.Add(Issue("info", [ph.Notes[0].Id], pos(ph.Notes[0].Pos), $"Phrase {ph.Number} lasts {beats:0.#} beats without a breath.",
                    "insert a rest or an AP note, or ou_music_humanize to add breaths"));
            }
        }
        var singer = await bridge.CatalogSingerAsync(new CatalogSingerParams { TrackId = part.TrackId }, ct).ConfigureAwait(false);
        var curves = await bridge.CurvesQueryAsync(new CurvesQueryParams { PartId = part.Id }, ct).ConfigureAwait(false);
        foreach (var c in curves.Curves.Where(c => c.Xs.Count > 0 && singer.Effects.GetValueOrDefault(c.Abbr) == Effects.NoEffect)) {
            issues.Add(Issue("info", [], pos(c.Xs[0]), $"Curve '{c.Abbr}' has data but no effect with {singer.Name}.",
                $"ou_curves_edit {{\"part\":\"{part.Id}\",\"ops\":[{{\"op\":\"clear\",\"curve\":\"{c.Abbr}\"}}]}}"));
        }
        int colors = singer.Colors?.Count ?? 0;
        foreach (var n in chosen) {
            if (n.Exprs?.FirstOrDefault(e => e.Abbr == "clr") is { } clr && colors > 0 && (clr.Value < 0 || clr.Value >= colors)) {
                issues.Add(Issue("warning", [n.Id], pos(n.Pos), $"{n.Id} uses voice color {clr.Value}; {singer.Name} has {colors}.",
                    $"ou_notes_edit {{\"part\":\"{part.Id}\",\"update\":[{{\"id\":\"{n.Id}\",\"expr\":{{\"clr\":null}}}}]}}"));
            }
        }
    }

    static JsonObject Issue(string severity, IEnumerable<string> ids, string at, string message, string fix) => new() {
        ["severity"] = severity,
        ["where"] = new JsonObject { ["ids"] = new JsonArray(ids.Select(i => (JsonNode?)i).ToArray()), ["at"] = at },
        ["message"] = message,
        ["fix"] = fix,
    };

    static bool IsDefaultPitch(PitchDto p) => p.Points.Count == 2 && p.Points.All(pt => Math.Abs(pt.Y) < 1e-3);

    static bool CurveFlat(CurveDto curve, int from, int to) {
        var values = Enumerable.Range(0, Math.Max(1, (to - from) / 60)).Select(k => CurvesGetTool.Sample(curve, from + k * 60)).ToList();
        return values.Max() - values.Min() == 0;
    }

    static int BarTicks(ProjectContext ctx) {
        var sig = ctx.Project.TimeSignatures?.FirstOrDefault();
        return sig is null ? 1920 : sig.BeatPerBar * 1920 / sig.BeatUnit;
    }

    /// <summary>The project's key, with the mode stored by ou_project_set / ou_music_compose (default major).</summary>
    static (Scale scale, string name) KeyScale(ProjectContext ctx) {
        int tonic = ((ctx.Project.Key % 12) + 12) % 12;
        string? mode = SongComments.Read(ctx.Project.Comment)?["mode"]?.GetValue<string>();
        var m = mode switch { "minor" => Mode.Minor, "harmonicminor" => Mode.HarmonicMinor, "dorian" => Mode.Dorian, "mixolydian" => Mode.Mixolydian, _ => Mode.Major };
        return (new Scale(tonic, m), $"{PitchName.Format(tonic + 12)[..^1]} {m.ToString().ToLowerInvariant()}");
    }
}
