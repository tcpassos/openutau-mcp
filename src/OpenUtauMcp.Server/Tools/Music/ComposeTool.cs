using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Music.Humanize;
using OpenUtauMcp.Music.Randomness;
using OpenUtauMcp.Music.Song;
using OpenUtauMcp.Server.Bridge;
using OpenUtauMcp.Server.Files;
using OpenUtauMcp.Server.Safety;

namespace OpenUtauMcp.Server.Tools.Music;

[McpServerToolType]
public static class ComposeTool {
    public const string SpecInvalid = "SONG_SPEC_INVALID";
    static readonly string[] json = [".json"];
    static readonly string[] ustx = [".ustx"];

    [McpServerTool(Name = "ou_music_compose", Title = "Create tracks, parts and notes from a song spec", ReadOnly = false, Idempotent = false, OpenWorld = false, Destructive = true)]
    [Description("Creates a song from a SongSpec: sections (name, role, bars, chords), voices with lines {section, at?, lyrics with hyphenated syllables, pitches, rhythm \"1/8*8 3q r:1/8\", restAfter} or sameAs, and derived voices {derive:{from, kind: harmony|double|octave, interval, roles}}. One track and part per voice, optionally humanized; one undo step. target new_project (default), append (add to the open project at 'at') or file (write a .ustx; OpenUtau may be closed). Errors point at the line with its syllable/pitch/duration counts; dryRun only validates. SongSpec format and example: openutau://guide/song-spec.")]
    public static Task<CallToolResult> Run(IBridgeClient bridge, PathPolicy policy,
        [Description("The SongSpec object (or use songFile).")] JsonElement? song = null,
        [Description("Absolute path of a SongSpec .json file.")] string? songFile = null,
        [Description("Singer per voice, e.g. {\"Lead\":\"LEONA\"} (default: the voice's singer).")] Dictionary<string, string>? tracks = null,
        [Description("Humanize every voice with its preset and seed (default true).")] bool humanize = true,
        [Description("new_project | append | file.")] string target = "new_project",
        [Description("file: absolute .ustx path.")] string? path = null,
        [Description("append: absolute bar:beat where the song starts (default 1:1).")] string? at = null,
        [Description("new_project with unsaved changes: fail (default) | save | backup.")] string onUnsaved = "fail",
        [Description("file: replace an existing file.")] bool overwrite = false,
        [Description("file: phonemizer for every track (default: each singer's, which needs OpenUtau running).")] string? phonemizer = null,
        [Description("Only validate; change nothing.")] bool dryRun = false,
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            if (target is not ("new_project" or "append" or "file")) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"Unknown target '{target}'. Use new_project, append or file.");
            }
            StatusResult? status = null;
            try {
                status = await bridge.StatusAsync(ct).ConfigureAwait(false);
            } catch (BridgeCallException e) when (e.Code == BridgeCallException.Unavailable && target == "file") {
                // Writing a file does not need OpenUtau.
            }
            var spec = LoadSpec(song, songFile, policy, status?.Project.FilePath);
            ComposedSong composed;
            try {
                composed = Composer.Compose(spec);
            } catch (SongSpecException e) {
                throw new ToolInputException(SpecInvalid, e.Message,
                    "Fix the listed fields: each line needs as many syllables (hyphens split words) as pitches and non-rest durations.",
                    new JsonObject { ["issues"] = new JsonArray(e.Issues.Select(i => (JsonNode?)new JsonObject { ["path"] = i.Path, ["message"] = i.Message }).ToArray()) });
            }
            var warnings = composed.Warnings.Select(w => new Warning("SONG_SPEC", w)).ToList();
            var summary = Summary(composed);
            if (dryRun) {
                return ToolResult.Ok($"Dry run: '{composed.Title}' is valid: {composed.Sections.Count} sections, " +
                                     $"{string.Join(", ", composed.Voices.Select(v => $"{v.Name} {v.Notes.Count} notes"))}; nothing changed.", summary, warnings);
            }

            // Singers: the call's map, then the voice's own.
            var singers = new Dictionary<string, SingerInfo?>();
            foreach (var voice in composed.Voices) {
                string? id = tracks?.GetValueOrDefault(voice.Name) ?? voice.Singer;
                if (id is null) {
                    throw new ToolInputException(ToolInputException.InvalidParams, $"No singer for voice '{voice.Name}'.",
                        "Pass tracks:{\"" + voice.Name + "\":\"<singer>\"} (ou_catalog_get kind:\"singers\" lists them).");
                }
                singers[voice.Name] = status is null ? null
                    : await bridge.CatalogSingerAsync(new CatalogSingerParams { Id = id }, ct).ConfigureAwait(false);
                if (status is null) {
                    singers[voice.Name] = new SingerInfo { Id = id, Name = id, Type = "unknown", Found = false, Effects = new Dictionary<string, string>() };
                }
            }

            return target switch {
                "file" => await WriteFileAsync(policy, composed, singers, humanize, path, overwrite, phonemizer, status, warnings, summary, ct).ConfigureAwait(false),
                "append" => await AppendAsync(bridge, composed, spec, singers, humanize, at, warnings, summary, ct).ConfigureAwait(false),
                _ => await NewProjectAsync(bridge, composed, singers, humanize, onUnsaved, warnings, summary, ct).ConfigureAwait(false),
            };
        });

    static SongSpec LoadSpec(JsonElement? song, string? songFile, PathPolicy policy, string? projectFile) {
        if ((song is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined }) == (songFile is not null)) {
            throw new ToolInputException(ToolInputException.InvalidParams, "Give either 'song' (the SongSpec) or 'songFile'.");
        }
        try {
            if (songFile is not null) {
                string file = policy.Check(songFile, projectFile, json, write: false);
                return SongSpec.Parse(File.ReadAllText(file));
            }
            return song!.Value.Deserialize<SongSpec>(SongSpec.JsonOptions) ?? throw new JsonException("The song spec is empty.");
        } catch (JsonException e) {
            throw new ToolInputException(SpecInvalid, $"The song spec is not valid JSON for a SongSpec: {e.Message}",
                "Fields: title, bpm, timeSig, key, sections[{name, role, bars, chords}], voices[{name, singer, lines|derive, humanize}].");
        } catch (IOException e) {
            throw new ToolInputException(ErrorCodes.IoError, $"Cannot read {songFile}: {e.Message}");
        }
    }

    static JsonObject Summary(ComposedSong s) => new() {
        ["title"] = s.Title,
        ["bpm"] = s.Bpm,
        ["key"] = s.Key,
        ["bars"] = s.EndTick / (Composer.Ppq * 4 * s.BeatPerBar / s.BeatUnit),
        ["sections"] = new JsonArray(s.Sections.Select(x => (JsonNode?)new JsonArray(x.Name, x.Role, x.From, x.To)).ToArray()),
        ["voices"] = new JsonArray(s.Voices.Select(v => (JsonNode?)new JsonObject {
            ["name"] = v.Name, ["kind"] = v.Kind, ["notes"] = v.Notes.Count, ["derivedFrom"] = v.DerivedFrom,
        }).ToArray()),
    };

    /// <summary>[openutau-mcp] block of the project comment: mode and sections.</summary>
    static JsonObject SongBlock(ComposedSong s, int offset, Units.TimeMap time) => new() {
        ["v"] = 1,
        ["title"] = s.Title,
        ["mode"] = s.Scale.Mode.ToString().ToLowerInvariant(),
        ["sections"] = new JsonArray(s.Sections.Select(x => {
            var node = new JsonObject {
                ["name"] = x.Name, ["role"] = x.Role, ["from"] = time.FormatPosition(offset + x.From), ["to"] = time.FormatPosition(offset + x.To),
            };
            if (x.Chords is { } chords) {
                node["chords"] = new JsonArray(chords.Select(c => (JsonNode?)c).ToArray());
            }
            return (JsonNode?)node;
        }).ToArray()),
    };

    /// <summary>
    /// Tracks, parts, notes and (optionally) humanization of every voice, as one plan with refs: voice i has track
    /// "@t{i}", part "@p{i}" and notes "@n{i}.{k}".
    /// </summary>
    static List<EditOp> VoiceOps(ComposedSong song, IReadOnlyList<ComposedVoice> voices, Dictionary<string, SingerInfo?> singers, bool humanize,
                                 int offset, IReadOnlyCollection<string> registered, List<Warning> warnings, Func<SingerInfo, (string? phonemizer, string? renderer)>? trackSettings) {
        var ops = new List<EditOp>();
        var ensured = new HashSet<string>(registered);
        for (int i = 0; i < voices.Count; i++) {
            var voice = voices[i];
            var singer = singers[voice.Name]!;
            string t = $"@t{i}", p = $"@p{i}";
            ops.Add(new TrackAddOp { Name = voice.Name, SingerId = singer.Id, Ref = t });
            if (trackSettings is not null) {
                var (phonemizer, renderer) = trackSettings(singer);
                if (phonemizer is not null || renderer is not null) {
                    ops.Add(new TrackUpdateOp { TrackId = t, Phonemizer = phonemizer, Renderer = renderer });
                }
            }
            ops.Add(new PartAddOp { TrackId = t, Pos = offset, Dur = Math.Max(song.EndTick, 1920), Name = voice.Name, Ref = p });
            var notes = voice.Notes.Select((n, k) => (n, id: $"@n{i}.{k}")).ToList();
            ops.AddRange(notes.Select(x => new NoteAddOp {
                PartId = p, Pos = x.n.Pos, Dur = x.n.Dur, Tone = x.n.Tone, Lyric = x.n.Lyric, Tuning = x.n.Tuning == 0 ? null : x.n.Tuning,
                Ref = x.id, GrowPart = true,
            }));
            if (!humanize || notes.Count == 0) {
                continue;
            }
            var parameters = HumanizeParams.FromPreset(voice.HumanizePreset);
            int seed = voice.HumanizeSeed ?? Random.Shared.Next(1, int.MaxValue);
            var voiceWarnings = new List<Warning>();
            var colors = HumanizeCore.ColorMap(singer, null);
            parameters = HumanizeCore.DisableIneffective(parameters, singer, colors, voiceWarnings, voice.Name);
            warnings.AddRange(voiceWarnings.Select(w => w with { Ref = voice.Name }));
            var input = new HumanizeInput {
                Notes = notes.Select(x => new HNote(x.id, x.n.Pos, x.n.Dur, x.n.Tone, x.n.Lyric)).ToList(),
                PartPos = offset,
                Bpm = song.Bpm,
                Sections = song.Sections.Select(s => new Section(s.Name, s.Role, offset + s.From, offset + s.To)).ToList(),
                Colors = colors,
            };
            var plan = Humanizer.Plan(input, parameters, new SubstreamRandom(seed));
            warnings.AddRange(plan.Warnings.Select(w => new Warning(HumanizeCore.WarningCode(w), w, voice.Name)));
            var current = notes.Select(x => new NoteDto { Id = x.id, Pos = x.n.Pos, Dur = x.n.Dur, Tone = x.n.Tone, Lyric = x.n.Lyric }).ToList();
            var built = HumanizeCore.BuildOps(plan, current, new Dictionary<string, CurveDto>(), Math.Max(song.EndTick, 1920), wholePart: true,
                new HumanizeCore.Target { PartId = p, RegisteredExpressions = ensured, TrackIdForEnsure = t, SingerIdForEnsure = singer.Id });
            foreach (var op in built.Ops.OfType<ProjectExpressionsOp>()) {
                ensured.UnionWith(op.Ensure);
            }
            ops.AddRange(built.Ops);
            ops.Add(new PartCommentOp { PartId = p, Comment = SongComments.Write("", HumanizeCore.History(parameters, seed)) });
        }
        return ops;
    }

    static async Task<CallToolResult> NewProjectAsync(IBridgeClient bridge, ComposedSong song, Dictionary<string, SingerInfo?> singers, bool humanize,
                                                      string onUnsaved, List<Warning> warnings, JsonObject summary, CancellationToken ct) {
        if (onUnsaved is not (OnUnsaved.Fail or OnUnsaved.Save or OnUnsaved.Backup)) {
            throw new ToolInputException(ToolInputException.InvalidParams, $"Unknown onUnsaved '{onUnsaved}'. Use fail, save or backup.");
        }
        // Validate the voices against the open project first (dry run): a bad singer or value must fail before the
        // open project is replaced by a new one.
        var before = await ProjectContext.LoadAsync(bridge, ct, "tempo", "expressions").ConfigureAwait(false);
        var probe = VoiceOps(song, song.Voices, singers, humanize, 0,
            (before.Project.Expressions ?? []).Select(e => e.Abbr).ToHashSet(), new List<Warning>(), null);
        await bridge.EditApplyAsync(new EditApplyParams { DryRun = true, Ops = probe }, ct).ConfigureAwait(false);
        var opened = await bridge.ProjectNewAsync(onUnsaved, ct).ConfigureAwait(false);
        var ctx = await ProjectContext.LoadAsync(bridge, ct, "tempo", "tracks", "parts", "expressions", "comment").ConfigureAwait(false);
        var time = new Units.TimeMap([new TimeSignatureDto(0, song.BeatPerBar, song.BeatUnit)]);
        var ops = new List<EditOp> {
            new ProjectUpdateOp {
                Bpm = song.Bpm,
                TimeSig = new TimeSignatureDto(0, song.BeatPerBar, song.BeatUnit),
                Key = song.Scale.Tonic,
                Comment = SongComments.Write(ctx.Project.Comment, SongBlock(song, 0, time)),
            },
        };
        // The new project's empty default track(s) would only be in the way.
        var used = (ctx.Project.Parts ?? []).Select(x => x.TrackId).ToHashSet();
        var voiceOps = VoiceOps(song, song.Voices, singers, humanize, 0,
            (ctx.Project.Expressions ?? []).Select(e => e.Abbr).ToHashSet(), warnings, null);
        ops.AddRange(voiceOps);
        ops.AddRange((ctx.Project.Tracks ?? []).Where(t => !used.Contains(t.Id)).Select(t => new TrackRemoveOp { TrackId = t.Id }));
        var result = await bridge.EditApplyAsync(new EditApplyParams { UndoName = $"MCP: compose {song.Title}", Ops = ops }, ct).ConfigureAwait(false);
        warnings.AddRange((result.Warnings ?? []).Where(w => w.Code is not (WarningCodes.NotUndoable or WarningCodes.DialogMayOpen)));
        summary["created"] = Created(result, song.Voices.Count);
        summary["backup"] = opened.BackupPath;
        return ToolResult.Ok($"Composed '{song.Title}' in a new project: {Voices(song)}{(humanize ? ", humanized" : "")}. One undo step " +
                             $"(the project settings and sections are not undoable). Save it with ou_project_save save_as.",
            summary, warnings, new JsonObject { ["epoch"] = result.Epoch });
    }

    static async Task<CallToolResult> AppendAsync(IBridgeClient bridge, ComposedSong song, SongSpec spec, Dictionary<string, SingerInfo?> singers,
                                                  bool humanize, string? at, List<Warning> warnings, JsonObject summary, CancellationToken ct) {
        var ctx = await ProjectContext.LoadAsync(bridge, ct, "tempo", "tracks", "parts", "expressions").ConfigureAwait(false);
        int offset = at is null ? 0 : ctx.Time.ParsePosition(at);
        if (ctx.Project.Tempos?.FirstOrDefault()?.Bpm is double bpm && Math.Abs(bpm - song.Bpm) > 0.01) {
            warnings.Add(new Warning("BPM_DIFFERS", $"The project is at {bpm} BPM; the spec says {song.Bpm}. Notes are placed in ticks (bars), not seconds."));
        }
        // Voices derived from an existing part: read it and derive now.
        var voices = new List<ComposedVoice>();
        foreach (var voice in song.Voices) {
            if (voice.Notes.Count > 0 || voice.DerivedFrom is null || song.Voices.Any(v => v.Name == voice.DerivedFrom)) {
                voices.Add(voice);
                continue;
            }
            var part = ctx.ResolvePart(voice.DerivedFrom);
            var derive = spec.Voices.Single(v => v.Name == voice.Name).Derive!;
            var source = (await bridge.AllNotesAsync(part.Id, null, ct).ConfigureAwait(false)).Notes
                .Select(n => new ComposedNote(part.Pos + n.Pos - offset, n.Dur, n.Tone, n.Lyric)).ToList();
            List<ComposedNote> notes;
            try {
                notes = Arranger.Derive(source, derive, song.Scale, song.Sections);
            } catch (ArgumentException e) {
                throw new ToolInputException(SpecInvalid, $"voices '{voice.Name}': {e.Message}");
            }
            voices.Add(voice with { Notes = notes.Where(n => n.Pos >= 0).ToList() });
        }
        var ops = VoiceOps(song, voices, singers, humanize, offset, (ctx.Project.Expressions ?? []).Select(e => e.Abbr).ToHashSet(), warnings, null);
        var result = await bridge.EditApplyAsync(new EditApplyParams { UndoName = $"MCP: add {song.Title}", Ops = ops }, ct).ConfigureAwait(false);
        warnings.AddRange((result.Warnings ?? []).Where(w => w.Code is not (WarningCodes.NotUndoable or WarningCodes.DialogMayOpen)));
        summary["created"] = Created(result, voices.Count);
        summary["voices"] = new JsonArray(voices.Select(v => (JsonNode?)new JsonObject {
            ["name"] = v.Name, ["kind"] = v.Kind, ["notes"] = v.Notes.Count, ["derivedFrom"] = v.DerivedFrom,
        }).ToArray());
        return ToolResult.Ok($"Added {voices.Count} voice track(s) of '{song.Title}' at {ctx.Time.FormatPosition(offset)}: {string.Join(", ", voices.Select(v => $"{v.Name} ({v.Notes.Count} notes)"))}. One undo step.",
            summary, warnings, new JsonObject { ["epoch"] = result.Epoch });
    }

    static async Task<CallToolResult> WriteFileAsync(PathPolicy policy, ComposedSong song,
                                                     Dictionary<string, SingerInfo?> singers, bool humanize, string? path, bool overwrite, string? phonemizer,
                                                     StatusResult? status, List<Warning> warnings, JsonObject summary, CancellationToken ct) {
        if (path is null) {
            throw new ToolInputException(ToolInputException.InvalidParams, "target file needs 'path' (an absolute .ustx path).");
        }
        if (song.Voices.Any(v => v.Notes.Count == 0 && v.DerivedFrom is not null && song.Voices.All(x => x.Name != v.DerivedFrom))) {
            throw new ToolInputException(ToolInputException.InvalidParams, "derive.fromPart needs the open project: use target append.");
        }
        string file = policy.Check(path, status?.Project.FilePath, ustx, write: true, overwrite);
        if (status is null) {
            warnings.Add(new Warning("OFFLINE", "OpenUtau is not running: singers were not checked and humanization skips voice color, consonants and formant. " +
                (phonemizer is null
                    ? "Tracks have no phonemizer, so OpenUtau will use its DefaultPhonemizer: pass phonemizer, or fix it after opening with ou_tracks_edit update."
                    : "Every track uses the given phonemizer.")));
        }
        var time = new Units.TimeMap([new TimeSignatureDto(0, song.BeatPerBar, song.BeatUnit)]);
        var ops = new List<EditOp> {
            new ProjectUpdateOp {
                Bpm = song.Bpm, TimeSig = new TimeSignatureDto(0, song.BeatPerBar, song.BeatUnit), Key = song.Scale.Tonic,
                Comment = SongComments.Write("", SongBlock(song, 0, time)),
            },
        };
        ops.AddRange(VoiceOps(song, song.Voices, singers, humanize, 0, UstxWriter.DefaultExpressions, warnings,
            s => (phonemizer ?? s.DefaultPhonemizer, s.Renderers?.FirstOrDefault())));
        // Expressions beyond OpenUtau's defaults (velc, speaker curves) use a built-in descriptor table.
        var project = new InMemoryProject(UstxWriter.BuiltInDescriptor) { Name = song.Title };
        try {
            project.Apply(ops);
        } catch (Exception e) when (e is InvalidOperationException or NotSupportedException) {
            throw new ToolInputException(ToolInputException.InvalidParams, $"Cannot write this song to a file: {e.Message}");
        }
        string text = UstxWriter.Write(project);
        byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(text);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
        summary["file"] = file;
        summary["bytes"] = bytes.Length;
        return ToolResult.Ok($"Wrote '{song.Title}' to {file}: {Voices(song)}{(humanize ? ", humanized" : "")}. Open it with ou_project_open.",
            summary, warnings);
    }

    static JsonObject Created(EditApplyResult result, int voices) {
        var created = new JsonObject();
        for (int i = 0; i < voices; i++) {
            if (result.Created?.GetValueOrDefault($"@t{i}") is { } t) {
                created[t] = result.Created?.GetValueOrDefault($"@p{i}");
            }
        }
        return created;
    }

    static string Voices(ComposedSong song) => string.Join(", ", song.Voices.Select(v => $"{v.Name} ({v.Notes.Count} notes)"));
}
