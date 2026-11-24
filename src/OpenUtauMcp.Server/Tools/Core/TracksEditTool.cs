using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Server.Bridge;

namespace OpenUtauMcp.Server.Tools.Core;

public sealed record TrackOpInput(
    [property: Description("add | update | mix | remove.")] string Op,
    [property: Description("Track id (\"t2\"), exact name or \"#2\" (1-based position). Required except for add.")] string? Track = null,
    [property: Description("add/update: track name.")] string? Name = null,
    [property: Description("add/update: singer id or name (see ou_catalog_get).")] string? Singer = null,
    [property: Description("add/update: phonemizer class name or tag, e.g. \"OpenUtau.Core.DiffSinger.DiffSingerEnglishPhonemizer\".")] string? Phonemizer = null,
    [property: Description("add/update: renderer name, e.g. \"DIFFSINGER\".")] string? Renderer = null,
    [property: Description("add/update: track color name, e.g. \"Blue\".")] string? Color = null,
    [property: Description("update: new 1-based position in the track list.")] int? Position = null,
    [property: Description("add/mix: volume in dB (-24..12).")] double? VolumeDb = null,
    [property: Description("mix: pan (-100..100).")] double? Pan = null,
    [property: Description("add/mix: mute.")] bool? Mute = null,
    [property: Description("mix: solo.")] bool? Solo = null,
    [property: Description("remove: the exact track name, required when the track has notes.")] string? Confirm = null);

[McpServerToolType]
public static class TracksEditTool {
    [McpServerTool(Name = "ou_tracks_edit", Title = "Add, change, mix or remove tracks", ReadOnly = false, Idempotent = false, OpenWorld = false, Destructive = true)]
    [Description("Adds, changes, mixes and removes tracks in one undo step (mix is not undoable in OpenUtau). Ops: add, update, mix, remove (confirm = the track name when it has notes). Example: {\"ops\":[{\"op\":\"add\",\"name\":\"Lead\",\"singer\":\"LEONA\"},{\"op\":\"mix\",\"track\":\"t1\",\"volumeDb\":-3}]}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge,
        [Description("Track operations, applied in order.")] TrackOpInput[] ops,
        [Description("Only validate; change nothing.")] bool dryRun = false,
        [Description("Undo step name (English).")] string? undoName = null,
        McpServer? server = null,
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            if (ops is not { Length: > 0 }) {
                throw new ToolInputException(ToolInputException.InvalidParams, "Nothing to do: give at least one op.");
            }
            var ctx = await ProjectContext.LoadAsync(bridge, ct).ConfigureAwait(false);
            var confirmed = new HashSet<int>();
            for (int i = 0; i < ops.Length; i++) {
                if (ops[i] is { Op: "remove", Track: { } t } && !dryRun) {
                    var track = ctx.ResolveTrack(t);
                    int notes = (ctx.Project.Parts ?? []).Where(x => x.TrackId == track.Id).Sum(x => x.NoteCount);
                    if (notes > 0 && ops[i].Confirm != track.Name && await Confirmation.AskAsync(server,
                            $"Remove track '{track.Name}' and its {notes} notes? (Undo restores it.)", ct).ConfigureAwait(false)) {
                        confirmed.Add(i);
                    }
                }
            }
            var plan = new List<EditOp>();
            var before = new JsonObject();
            for (int i = 0; i < ops.Length; i++) {
                plan.AddRange(ToOps(ctx, ops[i], i, before, confirmed.Contains(i)));
            }
            var result = await bridge.EditApplyAsync(new EditApplyParams {
                UndoName = undoName ?? $"MCP: edit {ops.Length} track op{(ops.Length == 1 ? "" : "s")}",
                DryRun = dryRun,
                Ops = plan,
            }, ct).ConfigureAwait(false);

            var data = new JsonObject();
            var created = new JsonArray();
            for (int i = 0; i < ops.Length; i++) {
                if (result.Created?.GetValueOrDefault($"@t{i}") is { } id) {
                    created.Add(id);
                }
            }
            if (created.Count > 0) {
                data["created"] = created;
            }
            if (result.Removed is { Count: > 0 } removed) {
                data["removed"] = new JsonArray(removed.Select(r => (JsonNode?)r).ToArray());
            }
            if (before.Count > 0) {
                data["mixBefore"] = before;
            }
            string summary = dryRun
                ? $"Dry run: {ops.Length} track op(s) would succeed; nothing changed."
                : $"{ops.Length} track op(s) applied. Undo with ou_history or Ctrl+Z in OpenUtau.";
            return ToolResult.Ok(summary, data, result.Warnings,
                new JsonObject { ["epoch"] = result.Epoch, ["structureRev"] = result.StructureRev });
        });

    static IEnumerable<EditOp> ToOps(ProjectContext ctx, TrackOpInput o, int i, JsonObject mixBefore, bool confirmed = false) {
        string at = $"ops[{i}]";
        void Only(params string[] allowed) {
            var given = new (string name, bool set)[] {
                ("track", o.Track is not null), ("name", o.Name is not null), ("singer", o.Singer is not null),
                ("phonemizer", o.Phonemizer is not null), ("renderer", o.Renderer is not null), ("color", o.Color is not null),
                ("position", o.Position is not null), ("volumeDb", o.VolumeDb is not null), ("pan", o.Pan is not null),
                ("mute", o.Mute is not null), ("solo", o.Solo is not null), ("confirm", o.Confirm is not null),
            };
            if (given.FirstOrDefault(g => g.set && !allowed.Contains(g.name)) is { name: { } bad }) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: '{bad}' is not used by op '{o.Op}'.");
            }
        }
        TrackDto Track() => o.Track is { } t ? ctx.ResolveTrack(t)
            : throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: 'track' is required for op '{o.Op}'.");

        switch (o.Op) {
            case "add": {
                Only("name", "singer", "phonemizer", "renderer", "color", "volumeDb", "mute");
                string reference = $"@t{i}";
                yield return new TrackAddOp { Name = o.Name, SingerId = o.Singer, Color = o.Color, Ref = reference };
                if (o.Phonemizer is not null || o.Renderer is not null) {
                    yield return new TrackUpdateOp { TrackId = reference, Phonemizer = o.Phonemizer, Renderer = o.Renderer };
                }
                if (o.VolumeDb is not null || o.Mute is not null) {
                    yield return new TrackMixOp { TrackId = reference, VolumeDb = o.VolumeDb, Mute = o.Mute };
                }
                break;
            }
            case "update": {
                Only("track", "name", "singer", "phonemizer", "renderer", "color", "position");
                var track = Track();
                if (o.Position is < 1) {
                    throw new ToolInputException(ToolInputException.InvalidParams, $"{at}.position is 1-based.");
                }
                yield return new TrackUpdateOp {
                    TrackId = track.Id, Name = o.Name, Color = o.Color, SingerId = o.Singer,
                    Phonemizer = o.Phonemizer, Renderer = o.Renderer, Position = o.Position - 1,
                };
                break;
            }
            case "mix": {
                Only("track", "volumeDb", "pan", "mute", "solo");
                var track = Track();
                mixBefore[track.Id] = new JsonObject {
                    ["volumeDb"] = track.VolumeDb, ["pan"] = track.Pan, ["mute"] = track.Mute, ["solo"] = track.Solo,
                };
                yield return new TrackMixOp { TrackId = track.Id, VolumeDb = o.VolumeDb, Pan = o.Pan, Mute = o.Mute, Solo = o.Solo };
                break;
            }
            case "remove": {
                Only("track", "confirm");
                var track = Track();
                int notes = (ctx.Project.Parts ?? []).Where(p => p.TrackId == track.Id).Sum(p => p.NoteCount);
                if (notes > 0 && o.Confirm != track.Name && !confirmed) {
                    throw new ToolInputException(ToolInputException.ConfirmationRequired,
                        $"{at}: track {track.Id} ('{track.Name}') has {notes} notes.",
                        $"Ask the user, then pass confirm:\"{track.Name}\" to remove it.",
                        new JsonObject { ["track"] = track.Id, ["notes"] = notes });
                }
                yield return new TrackRemoveOp { TrackId = track.Id };
                break;
            }
            default:
                throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: unknown op '{o.Op}'. Use add, update, mix or remove.");
        }
    }
}
