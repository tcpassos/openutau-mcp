using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Contracts.Ops;
using OpenUtauMcp.Server.Bridge;
using OpenUtauMcp.Server.Safety;
using OpenUtauMcp.Server.Units;

namespace OpenUtauMcp.Server.Tools.Core;

public sealed record PartOpInput(
    [property: Description("add | add_audio | update | duplicate | remove.")] string Op,
    [property: Description("update/duplicate/remove: part id (\"p3\") or exact name.")] string? Part = null,
    [property: Description("add/update: track id, exact name or \"#2\". update: moves the part to that track.")] string? Track = null,
    [property: Description("Start, absolute bar:beat, e.g. \"17:1\".")] string? From = null,
    [property: Description("add/update: length, e.g. \"32q\", \"1/1\" or ticks. Or give 'to'.")] string? Length = null,
    [property: Description("add/update: end position (exclusive) instead of length.")] string? To = null,
    [property: Description("Part name.")] string? Name = null,
    [property: Description("update: when the length changes, move the start instead of the end.")] bool ResizeFromStart = false,
    [property: Description("duplicate: target track; omitted = a new track with the same singer.")] string? ToTrack = null,
    [property: Description("remove: the exact part name, required when the part has more than 50 notes.")] string? Confirm = null,
    [property: Description("add_audio: absolute path of a .wav, .flac, .mp3 or .ogg file.")] string? File = null);

[McpServerToolType]
public static class PartsEditTool {
    const int ConfirmAboveNotes = 50;

    [McpServerTool(Name = "ou_parts_edit", Title = "Add, move, duplicate or remove parts", ReadOnly = false, Idempotent = false, OpenWorld = false, Destructive = true)]
    [Description("Adds, moves, resizes, renames, duplicates and removes parts in one undo step. Ops: add {track, from, length|to}; add_audio {file, track?, from?}; update {part, name, from, track, length|to, resizeFromStart}; duplicate {part, toTrack?, from?} (no toTrack = new track); remove {part, confirm}. Positions are absolute bar:beat. Example: {\"ops\":[{\"op\":\"add\",\"track\":\"t1\",\"from\":\"1:1\",\"to\":\"9:1\",\"name\":\"Verse\"}]}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge, PathPolicy policy,
        [Description("Part operations, applied in order.")] PartOpInput[] ops,
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
                if (ops[i] is { Op: "remove", Part: { } pr } && !dryRun) {
                    var target = ctx.ResolvePart(pr);
                    if (target.NoteCount > ConfirmAboveNotes && ops[i].Confirm != target.Name && await Confirmation.AskAsync(server,
                            $"Remove part '{target.Name}' and its {target.NoteCount} notes? (Undo restores it.)", ct).ConfigureAwait(false)) {
                        confirmed.Add(i);
                    }
                }
            }
            var plan = new List<EditOp>();
            for (int i = 0; i < ops.Length; i++) {
                plan.AddRange(ToOps(ctx, policy, ops[i], i, confirmed.Contains(i)));
            }
            var result = await bridge.EditApplyAsync(new EditApplyParams {
                UndoName = undoName ?? $"MCP: edit {ops.Length} part op{(ops.Length == 1 ? "" : "s")}",
                DryRun = dryRun,
                Ops = plan,
            }, ct).ConfigureAwait(false);

            var data = new JsonObject();
            var created = new JsonArray();
            for (int i = 0; i < ops.Length; i++) {
                if (result.Created?.GetValueOrDefault($"@p{i}") is { } id) {
                    created.Add(id);
                }
                if (result.Created?.GetValueOrDefault($"@t{i}") is { } track) {
                    data["createdTracks"] ??= new JsonArray();
                    data["createdTracks"]!.AsArray().Add(track);
                }
            }
            if (created.Count > 0) {
                data["created"] = created;
            }
            if (result.Removed is { Count: > 0 } removed) {
                data["removed"] = new JsonArray(removed.Select(r => (JsonNode?)r).ToArray());
            }
            if (result.PartRevs.Count > 0) {
                data["partRev"] = new JsonObject(result.PartRevs.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
            }
            string summary = dryRun
                ? $"Dry run: {ops.Length} part op(s) would succeed; nothing changed."
                : $"{ops.Length} part op(s) applied. Undo with ou_history or Ctrl+Z in OpenUtau.";
            return ToolResult.Ok(summary, data, result.Warnings,
                new JsonObject { ["epoch"] = result.Epoch, ["structureRev"] = result.StructureRev });
        });

    static readonly string[] audio = [".wav", ".flac", ".mp3", ".ogg", ".aiff", ".aif"];

    static IEnumerable<EditOp> ToOps(ProjectContext ctx, PathPolicy policy, PartOpInput o, int i, bool confirmed = false) {
        string at = $"ops[{i}]";
        void Only(params string[] allowed) {
            var given = new (string name, bool set)[] {
                ("part", o.Part is not null), ("track", o.Track is not null), ("from", o.From is not null),
                ("length", o.Length is not null), ("to", o.To is not null), ("name", o.Name is not null),
                ("resizeFromStart", o.ResizeFromStart), ("toTrack", o.ToTrack is not null), ("confirm", o.Confirm is not null), ("file", o.File is not null),
            };
            if (given.FirstOrDefault(g => g.set && !allowed.Contains(g.name)) is { name: { } bad }) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: '{bad}' is not used by op '{o.Op}'.");
            }
        }
        PartDto Part() => o.Part is { } p ? ctx.ResolvePart(p)
            : throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: 'part' is required for op '{o.Op}'.");
        int? Length(int start) {
            if (o.Length is not null && o.To is not null) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: give 'length' or 'to', not both.");
            }
            int? length = o.Length is { } l ? Durations.Parse(l) : o.To is { } to ? ctx.Time.ParsePosition(to) - start : null;
            if (length is <= 0) {
                throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: the part must end after it starts.");
            }
            return length;
        }

        switch (o.Op) {
            case "add": {
                Only("track", "from", "length", "to", "name");
                if (o.Track is null || o.From is null) {
                    throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: add needs 'track' and 'from'.");
                }
                var track = ctx.ResolveTrack(o.Track);
                int pos = ctx.Time.ParsePosition(o.From);
                int dur = Length(pos) ?? throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: add needs 'length' or 'to'.");
                yield return new PartAddOp { TrackId = track.Id, Pos = pos, Dur = dur, Name = o.Name, Ref = $"@p{i}" };
                break;
            }
            case "add_audio": {
                Only("file", "track", "from", "name");
                if (o.File is null) {
                    throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: add_audio needs 'file'.");
                }
                string file = policy.Check(o.File, ctx.Project.FilePath, audio, write: false);
                string trackId;
                if (o.Track is { } t) {
                    if (o.Name is not null) {
                        throw new ToolInputException(ToolInputException.InvalidParams,
                            $"{at}: 'name' names the new track that add_audio creates; with 'track' there is none. Rename the part with update.");
                    }
                    trackId = ctx.ResolveTrack(t).Id;
                } else {
                    trackId = $"@t{i}";
                    yield return new TrackAddOp { Name = o.Name ?? System.IO.Path.GetFileNameWithoutExtension(file), Ref = trackId };
                }
                yield return new PartAddWaveOp {
                    TrackId = trackId, File = file, Pos = o.From is { } f ? ctx.Time.ParsePosition(f) : 0, Ref = $"@p{i}",
                };
                break;
            }
            case "update": {
                Only("part", "track", "from", "length", "to", "name", "resizeFromStart");
                var part = Part();
                int? pos = o.From is { } f ? ctx.Time.ParsePosition(f) : null;
                int? dur;
                if (o.ResizeFromStart) {
                    // The end stays where it is: from is the new start, or length is measured back from the end.
                    if ((pos is null) == (o.Length is null) || o.To is not null) {
                        throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: with resizeFromStart give either from or length.");
                    }
                    dur = pos is int start ? part.Pos + part.Dur - start : Durations.Parse(o.Length!);
                    if (dur <= 0) {
                        throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: the new start must be before the end of the part.");
                    }
                    if (dur > part.Pos + part.Dur) {
                        throw new ToolInputException(ToolInputException.InvalidParams,
                            $"{at}: the part ends at {ctx.Time.FormatPosition(part.Pos + part.Dur)}, so with resizeFromStart the length can be at most {Durations.Format(part.Pos + part.Dur)} (the start cannot go before bar 1).");
                    }
                    pos = null;
                } else {
                    dur = Length(pos ?? part.Pos);
                }
                yield return new PartUpdateOp {
                    PartId = part.Id, Name = o.Name, Pos = pos, Dur = dur, ResizeFromStart = o.ResizeFromStart,
                    TrackId = o.Track is { } t ? ctx.ResolveTrack(t).Id : null,
                };
                break;
            }
            case "duplicate": {
                Only("part", "from", "name", "toTrack");
                var part = Part();
                string trackId;
                if (o.ToTrack is { } target) {
                    trackId = ctx.ResolveTrack(target).Id;
                } else {
                    trackId = $"@t{i}";
                    yield return new TrackAddOp { CopyFrom = part.TrackId, Ref = trackId };
                }
                yield return new PartDuplicateOp {
                    PartId = part.Id, TrackId = trackId, Name = o.Name, Ref = $"@p{i}",
                    Pos = o.From is { } f ? ctx.Time.ParsePosition(f) : null,
                };
                break;
            }
            case "remove": {
                Only("part", "confirm");
                var part = Part();
                if (part.NoteCount > ConfirmAboveNotes && o.Confirm != part.Name && !confirmed) {
                    throw new ToolInputException(ToolInputException.ConfirmationRequired,
                        $"{at}: part {part.Id} ('{part.Name}') has {part.NoteCount} notes.",
                        $"Ask the user, then pass confirm:\"{part.Name}\" to remove it.",
                        new JsonObject { ["part"] = part.Id, ["notes"] = part.NoteCount });
                }
                yield return new PartRemoveOp { PartId = part.Id };
                break;
            }
            default:
                throw new ToolInputException(ToolInputException.InvalidParams, $"{at}: unknown op '{o.Op}'. Use add, add_audio, update, duplicate or remove.");
        }
    }
}
