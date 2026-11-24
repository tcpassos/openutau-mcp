using System.ComponentModel;
using ModelContextProtocol.Server;

namespace OpenUtauMcp.Server.Guides;

/// <summary>Static orientation guides, in English, as MCP resources.</summary>
[McpServerResourceType]
public static class Guides {
    [McpServerResource(UriTemplate = "openutau://guide/units", Name = "guide-units", Title = "Units and positions", MimeType = "text/markdown")]
    [Description("How positions, durations, pitches and curve values are written in this server.")]
    public static string Units() => """
        # Units

        - **Position**: absolute `bar:beat`, 1-based, beats may be fractional: `17:1`, `17:2.5`; `17:3+60` adds ticks.
          A bare integer is an absolute tick. Ranges: `{"bars":"17-24"}` covers bar 17 up to the start of bar 25.
        - **Duration**: quarters `0.5q`, whole-note fractions `1/8`, dotted `1/4.`, or ticks (480 per quarter).
        - **Pitch**: scientific names, C4 = 60 (`A4`, `C#5`, `Bb3`) or MIDI numbers.
        - **Pitch points** (`ou_notes_edit pitchPoints`): `[x ms from note start, y cents, shape]`, shapes io|l|i|o|sp.
          With snapFirst (OpenUtau's default) the first point's y is replaced by the previous note's pitch (0 after a rest).
        - **Curves**: `pitd` cents; `dyn` -240..120 (0 = neutral); `velc` 100 = normal consonant speed; `genc` +darker/-brighter
          (100 = 12 semitones of formant); voice color mixing `cl01`.. 0..100. Points sit on a 5-tick grid.
        - **Ids**: `n12` notes, `p3` parts, `t2` tracks. They stay valid while the project is open; reopening invalidates them.
        """;

    [McpServerResource(UriTemplate = "openutau://guide/bitwig", Name = "guide-bitwig", Title = "Vocals in Bitwig (or another DAW)", MimeType = "text/markdown")]
    [Description("Two ways to get the OpenUtau vocals into Bitwig: the live DAW link (plugin) or exported stems.")]
    public static string Bitwig() => """
        # Vocals in Bitwig

        ## Live: the OpenUtau plugin (no exporting)
        1. Install the plugin once from OpenUtau (DAW integration window; VST3 or CLAP). `ou_daw status` shows whether it is installed.
        2. Save the project (`ou_project_save`): the link needs a project file.
        3. In Bitwig, put the OpenUtau plugin on an audio/instrument track (one instance per OpenUtau track you want to hear).
        4. `ou_daw scan`, then `ou_daw connect` (pass `server` when several instances run).
           `ou_daw status` lists the track each instance plays; `ou_daw bind` (server + track) changes it. An instance follows
           its track when tracks are reordered (DAW protocol 1.3); older ones follow the position.
        5. Match the tempo: `ou_daw status` warns when the DAW BPM differs from the project (`ou_project_set bpm`).
        6. Edit with any tool; the plugin receives the change after rendering. Nothing is exported.

        ## Offline: stems
        - `ou_render export scope:"stems"` writes one file per unmuted track, all starting at 0:00, so they line up when dropped at
          bar 1 in Bitwig at the same tempo.
        - For a section or higher quality: `range:{"bars":"17-24"}`, `format:"wav32f"` (the file then starts at the range start:
          drop it at that bar), `applyFaders:false` to mix in Bitwig instead.
        """;

    [McpServerResource(UriTemplate = "openutau://guide/song-spec", Name = "guide-song-spec", Title = "SongSpec for ou_music_compose", MimeType = "text/markdown")]
    [Description("How to write the SongSpec that ou_music_compose turns into tracks, parts and notes, with an example.")]
    public static string SongSpecGuide() => """
        # SongSpec

        ```json
        { "title": "Porcelain Crown", "bpm": 124, "timeSig": "4/4", "key": "Dm",
          "sections": [ {"name":"verse1","role":"verse","bars":8,"chords":["Dm","Bb","Gm","A","Dm","Bb","Gm","A"]},
                        {"name":"chorus1","role":"chorus","bars":8},
                        {"name":"chorus2","role":"chorus","bars":8} ],
          "voices": [
            {"name":"Lead","singer":"LEONA","humanize":{"preset":"natural","seed":7},"lines":[
              {"section":"verse1","lyrics":"Can-dles burn-ing in the hall of glass","pitches":"A3 A3 D4 D4 E4 F4 E4 D4 D4",
               "rhythm":"1/8*8 3q","restAfter":"1q"},
              {"section":"chorus1","lyrics":"I wear a por-ce-lain crown","pitches":"A4 A4 G4 F4 G4 A4 D5",
               "rhythm":"1/8 1q 1/8 1/8 1/8 1/8 3.5q","restAfter":"1q"},
              {"section":"chorus2","sameAs":"chorus1"} ]},
            {"name":"Harmony","derive":{"from":"Lead","kind":"harmony","interval":"third_below","roles":["chorus"]}} ] }
        ```

        - **sections**: in song order; `role` is intro|verse|pre|chorus|drop|bridge|outro|other (it drives dynamics and
          voice color when humanizing); `bars` per section; `chords` optional, one per bar.
        - **lines**: `lyrics` with hyphens between syllables (a word of N syllables takes N notes: the word, then "+"); a lone
          "+" holds the previous syllable; `pitches` one per syllable (`~` repeats); `rhythm` durations (`1/8`, dotted `1/4.`,
          quarters `3q`, `r:1/8` rest, `*n` repeat); counts must match. `at` = "bar:beat" inside the section; omitted = right
          after the previous line of that section plus its `restAfter`. `sameAs` repeats this voice's lines of another section.
        - **derive**: `harmony` (diatonic `interval`: third_below, third_above, fourth_*, fifth_*, sixth_*), `double`
          (unison, `detuneCents`), `octave` (`direction` up|down); `roles` limits it to those sections.
        - Validate with `dryRun:true`; errors name the field, e.g. `voices[0].lines[3]`, with the counts.
        - Each voice becomes one track and one part; the whole song is one undo step. `target:"file"` writes a .ustx.
        """;

    [McpServerResource(UriTemplate = "openutau://guide/workflow", Name = "guide-workflow", Title = "Editing workflow", MimeType = "text/markdown")]
    [Description("Recommended order of tool calls for editing a song safely.")]
    public static string Workflow() => """
        # Workflow

        1. `ou_status` (is OpenUtau connected? unsaved changes?), then `ou_project_get` for part and track ids.
        2. Read before writing: `ou_notes_get` (compact) and, for expression work, `ou_curves_get` and `ou_catalog_get`.
        3. Edit with one call per idea (`ou_notes_edit`, `ou_curves_edit`, `ou_music_humanize`). Each call is ONE undo step;
           invalid input changes nothing. Pass `dryRun` to preview and `ifRev` (the partRev you read) to avoid overwriting
           the user's own edits.
        4. Let the user listen: `ou_playback` with a bar range and, if useful, a single track.
        5. Undo with `ou_history` when a change does not sound better. Save with `ou_project_save`.
        6. Export with `ou_render` (mixdown or stems) when the user wants audio files.

        The user may be editing in OpenUtau at the same time. BUSY_USER_EDITING and CONFLICT are normal: re-read and retry.
        """;

    [McpServerResource(UriTemplate = "openutau://guide/humanization", Name = "guide-humanization", Title = "Humanization", MimeType = "text/markdown")]
    [Description("What each humanization layer does and how to tune it.")]
    public static string Humanization() => """
        # Humanization (`ou_music_humanize`)

        Computed over whole phrases, applied only to the selection, one undo step; same seed = same result.

        | Layer | Effect | Useful parameters |
        |---|---|---|
        | scoop | attack from below at phrase starts | depthCents 50, jitterCents 30, riseMs 85 |
        | portamento | glide between notes, longer for bigger leaps | baseMs 45, perSemitoneMs 9, variation 0.15 |
        | overshoot | slight overshoot on upward leaps >= 3 semitones | amountCents 12, settleMs 70 |
        | drift | slow random pitch wander inside notes | sigmaCents 4, rho 0.6, stepMs 110 |
        | fall | pitch fall at phrase ends | cents -80, fallMs 90 |
        | vibrato | per-note vibrato on notes >= 1 beat, stronger in chorus/drop | depthMin/Max 20-30, periodMin/Max 150-185 ms, maxLength 80 |
        | dynamics | phrase arc up to the highest note, swell on long notes, release at the end | rise 12, release 14, swell 5 |
        | consonants | consonant speed (velc): faster on short notes, slower on long ones | fast 112, normal 100, slow 90 |
        | formant | brighter on high notes, darker on low ones (genc) | base -6, bright -10, dark 4 |
        | breaths | AP breath before phrases after a rest >= 0.75 beat | minGapBeats 0.75, maxBeats 0.75 |
        | color | voice color by section role: verse normal, pre/chorus power, outro soft | colorBySection |

        Presets: `natural` (lead), `backing` (softer, intensity 0.7, no breaths), `chops` (power color, big falls).
        Sections drive dynamics base, color and vibrato strength: pass `sections` or store them in the project.

        Tuning from feedback, one change at a time, then listen:
        - "robotic / too straight" -> intensity 1.2-1.4, or vibrato depthMin/Max up, drift sigmaCents 6.
        - "too wobbly / drunk" -> drift sigmaCents 2, vibrato depth down, portamento variation 0.05.
        - "endings sound dry" -> fall cents -120; "endings droop too much" -> fall cents -40 or fall false.
        - "flat dynamics" -> dynamics rise 18, release 20. "attacks sound out of tune" -> scoop false.
        - Keep what already sounds right with `preserve` (e.g. ["pitch"] to change only dynamics/color).
        """;

    [McpServerResource(UriTemplate = "openutau://guide/diffsinger-expressions", Name = "guide-diffsinger-expressions",
        Title = "DiffSinger expressions", MimeType = "text/markdown")]
    [Description("Which expressions change a DiffSinger voice and why.")]
    public static string DiffSingerExpressions() => """
        # DiffSinger expressions

        Whether an expression changes the sound depends on the voicebank. Always check `ou_catalog_get kind=singer` or
        `kind=expressions` for the track: each expression is effective, noEffect or unknown.

        - `pitd` (pitch deviation) and `dyn` (dynamics) always work.
        - `genc` (formant/gender) needs the key-shift embed; `velc` (consonant speed) needs the speed embed.
        - `ene`, `brec`, `voic`, `tenc` (energy, breathiness, voicing, tension) need both the matching embed AND a variance
          model; many voicebanks (e.g. LEONA) have neither, so these do nothing.
        - `clr` picks one voice color per note; `cl01`, `cl02`.. are curves that MIX colors continuously (0..100 each).
          They are suggested by the renderer and must be registered first (`ensure:true` in `ou_curves_edit`).
        - `pexp` scales the pitch model's expressiveness; it only matters when the model's pitch is loaded.
        Writing an expression marked noEffect is allowed but returns EXPRESSION_NO_EFFECT.
        """;
}
