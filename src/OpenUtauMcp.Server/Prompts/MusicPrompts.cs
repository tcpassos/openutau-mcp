using System.ComponentModel;
using ModelContextProtocol.Server;

namespace OpenUtauMcp.Server.Prompts;

/// <summary>Guided workflows shown as slash commands in Claude Code.</summary>
[McpServerPromptType]
public static class MusicPrompts {
    [McpServerPrompt(Name = "humanize_phrase", Title = "Humanize a phrase")]
    [Description("Analyze a stretch of a part, humanize it, and let the user listen.")]
    public static string HumanizePhrase(
        [Description("Part id or name.")] string part,
        [Description("Bars, e.g. \"17-24\".")] string bars,
        [Description("Optional mood, e.g. \"dark and intimate\", \"powerful chorus\".")] string? mood = null) => $$"""
        Humanize bars {{bars}} of part "{{part}}" in OpenUtau{{(mood is null ? "" : $", aiming for: {mood}")}}.

        1. Call ou_status, then ou_notes_get for the part with range {"bars":"{{bars}}"} to see the phrases.
        2. Call ou_catalog_get kind=singer for the part's track to know which expressions are effective.
        3. Choose the preset (natural for a lead, backing for harmonies, chops for vocal chops) and the section roles
           of these bars; adjust intensity or layers only if the mood calls for it (see openutau://guide/humanization).
        4. Call ou_music_humanize with dryRun:true first and summarize what would change.
        5. Apply it, then play the bars with ou_playback (only that track if it helps) and ask the user how it sounds.
        6. Report the seed so the result can be reproduced; offer ou_history undo if it is worse.
        """;

    [McpServerPrompt(Name = "refine_expressiveness", Title = "Refine expressiveness from feedback")]
    [Description("Turn the user's listening feedback into one concrete humanization or curve change at a time.")]
    public static string RefineExpressiveness(
        [Description("Part id or name.")] string part,
        [Description("What the user said, e.g. \"robotic in the chorus\", \"dry phrase endings\".")] string feedback) => $"""
        The user listened to part "{part}" and said: "{feedback}".

        1. Find where the problem is (ask for bars if unclear) and read the notes and curves there
           (ou_notes_get detail=full fields vibrato,pitch; ou_curves_get per=phrase).
        2. Pick ONE change that addresses the feedback (see the tuning list in openutau://guide/humanization):
           re-run ou_music_humanize on those bars with the same seed and one parameter changed, or a targeted
           ou_notes_edit / ou_curves_edit. Keep what already works with preserve.
        3. Apply it, play those bars with ou_playback, and ask again. Repeat one change at a time.
        4. If a change made it worse, undo it with ou_history before trying the next idea.
        """;

    [McpServerPrompt(Name = "fix_pronunciation", Title = "Fix the pronunciation of words")]
    [Description("Check how the phonemizer reads some words and fix the wrong ones with phonetic hints or phoneme overrides.")]
    public static string FixPronunciation(
        [Description("Part id or name.")] string part,
        [Description("Words that sound wrong, comma separated, e.g. \"porcelain, crown\".")] string words) => $$"""
        Fix how these words are sung in part "{{part}}": {{words}}.

        1. Call ou_notes_get for the part with select.lyric matching the words and detail:"full", fields:["phonemes"]
           to see the phonemes OpenUtau chose for each note.
        2. Call ou_catalog_get kind:"g2p" with the part's track and the words, to see how the phonemizer dictionary reads them
           (null = not in the dictionary, so the phonemizer guessed).
        3. Compare with the intended pronunciation. Prefer, in this order:
           a. a phonetic hint in the lyric, e.g. "[p ao r s ax l ih n]" on the first note of the word (ou_notes_edit lyric),
              using only symbols the dictionary uses (look at other words' phonemes);
           b. phoneme alias or timing overrides on the existing note (ou_notes_edit phonemes, after the lyric is final);
           c. a different phonemizer for just that note (ou_notes_edit phonemizer), only if the language is wrong.
        4. Re-read the phonemes (fields:["phonemes"]) to confirm the change, then play the phrase with ou_playback.
        5. Ask the user whether it sounds right; undo with ou_history if not.
        """;

    [McpServerPrompt(Name = "compose_song", Title = "Compose a song")]
    [Description("Write a SongSpec from a brief, validate it, create it in OpenUtau, humanize and play the chorus.")]
    public static string ComposeSong(
        [Description("What the song is about, style, mood, structure.")] string brief,
        [Description("Singer id or name (default: pick one with ou_catalog_get kind:\"singers\").")] string? singer = null,
        [Description("Tempo in BPM.")] string? bpm = null,
        [Description("Key, e.g. \"Dm\".")] string? key = null) => $$"""
        Compose a song in OpenUtau from this brief: {{brief}}{{(bpm is null ? "" : $"\nTempo: {bpm} BPM.")}}{{(key is null ? "" : $"\nKey: {key}.")}}

        1. Read openutau://guide/song-spec. {{(singer is null ? "Pick a singer with ou_catalog_get kind:\"singers\"." : $"Use the singer \"{singer}\".")}}
        2. Write a SongSpec: sections with roles and chords, a Lead voice with lyric lines (hyphenated syllables, one
           pitch and one duration per syllable, lines that fit their sections, rests between lines), and derived voices
           (harmony in the chorus) if they suit the style. Keep the melody singable for the singer's range.
        3. Call ou_music_compose with dryRun:true and fix every listed issue (the counts must match).
        4. Ask the user before replacing the open project (onUnsaved), then call ou_music_compose for real.
        5. Play the first chorus with ou_playback and ask how it sounds; offer ou_music_variations or ou_music_humanize
           tweaks, and ou_project_save save_as to keep it.
        """;

    [McpServerPrompt(Name = "ab_compare", Title = "A/B compare variations")]
    [Description("Create 2-3 humanization variations of a part, play them one after another, and keep the best.")]
    public static string AbCompare(
        [Description("Part id or name.")] string part,
        [Description("Bars to listen to, e.g. \"21-28\".")] string range,
        [Description("Optional ideas to try, e.g. \"more vibrato\", \"softer verse\".")] string? ideas = null) => $$"""
        Compare variations of part "{{part}}" in bars {{range}}{{(ideas is null ? "" : $", trying: {ideas}")}}.

        1. ou_music_variations action:"create" with 2-3 variants: different seeds, and presets or layer overrides that
           follow the ideas (one clear difference per variant). Labels B, C, D.
        2. For each label (and "base"): ou_music_variations action:"activate", then ou_playback play with
           range {"bars":"{{range}}"}. Tell the user which one is playing.
        3. Ask which they prefer. ou_music_variations action:"promote" with that label (it needs confirm; ask first),
           or "remove" the ones they do not want.
        """;

    [McpServerPrompt(Name = "export_to_bitwig", Title = "Get the vocals into Bitwig")]
    [Description("Connect OpenUtau to the plugin in Bitwig if it is there; otherwise export aligned stems.")]
    public static string ExportToBitwig(
        [Description("plugin (live link) or stems (files); default: plugin when available.")] string? mode = null) => $$"""
        Get the OpenUtau vocals into Bitwig{{(mode is null ? "" : $" using {mode}")}}. Follow openutau://guide/bitwig.

        1. ou_status: the project must be saved for the live link (offer ou_project_save if not).
        2. Unless mode is stems: ou_daw scan. If a plugin is running, ou_daw connect and check the BPM warning (fix the tempo
           in Bitwig or with ou_project_set). Tell the user edits now reach Bitwig without exporting.
        3. If there is no plugin (or mode is stems): ou_render export scope:"stems" to a folder the user chooses, wait for
           the job, and list the files with where to drop them (bar 1, same tempo).
        """;
}
