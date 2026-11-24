# openutau-mcp

Control **OpenUtau** from Claude (or any MCP client), live, while the program is open.
Everything is C# / .NET 10.

```
Claude ──MCP (stdio)──> openutau-mcp.exe ──named pipe (current user)──> OpenUtauMcp.Bridge.dll (inside OpenUtau)
```

| Project | What it is |
|---|---|
| `src/OpenUtauMcp.Contracts` | Bridge ⇄ server protocol contracts (data only). Also compiled into the bridge. |
| `src/OpenUtauMcp.Bridge` | Plugin loaded by OpenUtau. Starts with OpenUtau, opens the pipe `openutau-mcp-<pid>` and runs everything on the UI thread with OpenUtau's own commands (visible on screen, undoable with Ctrl+Z). |
| `src/OpenUtauMcp.Music` | Musical domain with no OpenUtau dependency: humanizer, song specs, composer, derived voices. |
| `src/OpenUtauMcp.Server` | `openutau-mcp.exe`: MCP server (official `ModelContextProtocol` SDK), musical units, tools. |
| `tests/` | Contract, music and server tests (`dotnet test`); `tests/fixtures/` holds the test song Porcelain Crown. |
| `tools/` | `bridge-call.ps1`: sends raw bridge requests to a running OpenUtau, for ops without an MCP tool. |

## Tools (22)

| Tool | What it does |
|---|---|
| `ou_status` | connection, project, playback, history, jobs, recent errors |
| `ou_project_get` | tempo, time signatures, tracks, parts (ids `p1`…) |
| `ou_project_open` | open, create, reload, or import the tracks of other files (ids stay valid then); automatic backup first; refuses with unsaved changes unless `onUnsaved` says otherwise |
| `ou_project_save` | save, save as, side-effect-free backup, export MIDI, UST or DiffSinger (.ds) |
| `ou_project_set` | BPM, tempo and time signature changes, key (the mode is kept in the comment), register expressions, song sections (in the project comment, not undoable) |
| `ou_tracks_edit` | add a track (singer, phonemizer, renderer), change, mix, remove (`confirm` with the name when it has notes) |
| `ou_parts_edit` | add, move, resize, rename, duplicate (to a new or existing track) and remove parts, add audio parts |
| `ou_catalog_get` | a singer (voice colors, DiffSinger embeds) and expressions: which ones **have an effect** with the track's voice; singers, phonemizers, renderers, batch edits, presets, and how the phonemizer reads words (g2p) |
| `ou_notes_get` | notes as a compact table (`bar:beat`, quarters, note names) plus phrases; `full` adds vibrato, pitch, expressions; `phonemes` shows the phonemes OpenUtau resolved (waits for phonemization) |
| `ou_notes_edit` | add, update (by id or filter) and remove notes: position, length, pitch, lyric, tuning, vibrato, pitch points, per-note/per-phoneme expressions, phonemes (alias, offset, preutter, overlap), per-note phonemizer, expression reset — one Ctrl+Z per call, all or nothing |
| `ou_lyrics_set` | spread a text over notes: hyphens split syllables, `+` = melisma, `[f o n]` = phonemes; a wrong count is an error with the alignment (or `truncate`/`pad`) |
| `ou_curves_get` | curve summaries/points, per note or per phrase; `final_pitch` = the pitch sent to the model (portamento, vibrato, `pitd`) as cents of deviation per note; `real:<abbr>` = the curve the renderer produced |
| `ou_curves_edit` | write curves over a span (keeping the rest) or whole, stepped or linear, clear; shapes (const, ramp, arc), scale/offset, copy spans; `ensure` registers suggested curves (`velc`, `cl01`…) |
| `ou_batch_edit` | OpenUtau's own batch edits (quantize, legato, fix overlaps, transpose, breaths, tails, phonetic hints, resets, renderer pitch, real curves); each is its own undo step; lists created/removed notes |
| `ou_history` | undo/redo |
| `ou_playback` | play a range or one track, pause, stop, seek |
| `ou_render` | export WAV (mixdown or stems) as a job; with `range`/`tracks`/`format`/`channels`/`applyFaders`, the bridge's own exporter: a span, chosen tracks, wav16/24/32f, mono/stereo, cancellation that really stops it |
| `ou_daw` | link OpenUtau to its plugin in Bitwig (or another DAW): status (with the track each instance plays), scan, connect, disconnect, bind an instance to a track, BPM mismatch and duplicate-binding warnings |
| `ou_music_humanize` | humanize (pitch, vibrato, dynamics, breaths, voice color per section), reproducible seed |
| `ou_music_compose` | create a song from a `SongSpec` (sections, lyric/melody lines, derived voices): new project, append to the open one, or write a `.ustx` without OpenUtau |
| `ou_music_analyze` | phrases, range, leaps, "raw" stretches and problems (lint) with suggested fixes |
| `ou_music_variations` | A/B variations on parallel tracks: create (one Ctrl+Z), listen one at a time, promote, remove |

Backups go to `%LOCALAPPDATA%\OpenUtauMcp\backups` (before opening another project and before the first edit of each project).

Files can only be read/written in `Documents\OpenUtau`, the open project's folder, and folders allowed with
`--allow-dir <folder>` (in the `args` of `.mcp.json`) or the `OPENUTAU_MCP_ALLOW_DIRS` variable (separated by `;`).

Note ids (`n12`) stay valid while the project is open, even after edits.
Expressions with no effect for the track's voice (e.g. `tenc` with LEONA) are accepted but come back with the warning `EXPRESSION_NO_EFFECT`.

Prompts (`/` commands in Claude Code): `humanize_phrase`, `refine_expressiveness`, `fix_pronunciation`, `compose_song`, `ab_compare`, `export_to_bitwig`.
`SongSpec` example: [`tests/fixtures/porcelain-crown/porcelain_crown.song.json`](tests/fixtures/porcelain-crown/porcelain_crown.song.json)
(reproduces the Python prototype's notes exactly); the format is in the resource `openutau://guide/song-spec`. No workflow needs Python.
Large removals (a track with notes, a part with more than 50 notes, `reset_all`) ask for confirmation: through the `confirm`
parameter or, if the client supports it, a yes/no question.

## Installation

Requires the **.NET 10 SDK** and OpenUtau in `C:\Program Files\OpenUtau` (another location: `-p:OpenUtauDir=...`).

```powershell
dotnet build src\OpenUtauMcp.Bridge -c Release -t:Install   # bridge -> Documents\OpenUtau\Plugins\McpBridge
dotnet build src\OpenUtauMcp.Server -c Release              # MCP server
dotnet test OpenUtauMcp.slnx
```

Restart OpenUtau after installing the bridge. Register the server in the `.mcp.json` of the folder where you make music
(not in this repository, so working on the code does not load it):

```json
{ "mcpServers": { "openutau": { "type": "stdio",
    "command": "<repository>\\src\\OpenUtauMcp.Server\\bin\\Release\\net10.0\\openutau-mcp.exe",
    "args": ["serve"] } } }
```

The running server locks `bin\Release`: close Claude Code sessions that use it before rebuilding.

Manual test without an MCP client:

```powershell
src\OpenUtauMcp.Server\bin\Release\net10.0\openutau-mcp.exe call ou_status
```

Reinstall the bridge after updating OpenUtau (with OpenUtau closed):

```powershell
src\OpenUtauMcp.Server\bin\Release\net10.0\openutau-mcp.exe install-bridge
```

Check the installation (OpenUtau, installed bridge, pipe, handshake, Core version, folders):

```powershell
src\OpenUtauMcp.Server\bin\Release\net10.0\openutau-mcp.exe doctor
```

## Security

Named pipe with `PipeOptions.CurrentUserOnly`: only your Windows user can connect; there is no network port and no secret on disk.

## Compatibility

The bridge is compiled against the installed `OpenUtau.Core.dll` and checks its SHA-256 when connecting.
**After updating OpenUtau, rebuild and reinstall the bridge.** Tested with OpenUtau 0.1.577.11.

`ou_daw` reports and changes which OpenUtau track each DAW plugin instance plays only with an OpenUtau and an
OpenUtau Bridge plugin that speak DAW protocol 1.3 ([openutau/OpenUtau#2645](https://github.com/openutau/OpenUtau/pull/2645),
[KakaruHayate/openutau-vst-bridge#20](https://github.com/KakaruHayate/openutau-vst-bridge/pull/20)); with older
versions it links, scans and reports BPM as before.
