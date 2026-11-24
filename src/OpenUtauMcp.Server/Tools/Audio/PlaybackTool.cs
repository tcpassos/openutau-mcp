using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Server.Bridge;
using OpenUtauMcp.Server.Tools.Core;

namespace OpenUtauMcp.Server.Tools.Audio;

[McpServerToolType]
public static class PlaybackTool {
    [McpServerTool(Name = "ou_playback", Title = "Play, pause, stop or seek", ReadOnly = false, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Controls playback in OpenUtau so the user can listen. play starts at 'from' (bar:beat, default: cursor) and stops at 'to'; " +
                 "'range' with bars is a shortcut; 'track' plays one track only. Rendering may take a moment before sound starts. " +
                 "Example: {\"action\":\"play\",\"range\":{\"bars\":\"17-24\"},\"track\":\"Lead D\"}")]
    public static Task<CallToolResult> Run(IBridgeClient bridge,
        [Description("play | pause | stop | seek | status")] string action,
        [Description("Start position, e.g. \"17:1\" (play, seek).")] string? from = null,
        [Description("End position (play).")] string? to = null,
        [Description("Bar range instead of from/to, e.g. \"17-24\".")] RangeInput? range = null,
        [Description("Track id or exact name to play alone.")] string? track = null,
        [Description("Loop the range.")] bool? loop = null,
        CancellationToken ct = default) => ToolResult.Guard(async () => {
            var ctx = await ProjectContext.LoadAsync(bridge, ct, "tempo", "tracks").ConfigureAwait(false);
            PlaybackStateDto state;
            if (action == "status") {
                state = await bridge.PlaybackGetAsync(ct).ConfigureAwait(false);
            } else {
                if (action is not (PlaybackActions.Play or PlaybackActions.Pause or PlaybackActions.Stop or PlaybackActions.Seek)) {
                    throw new ToolInputException(ToolInputException.InvalidParams, $"Unknown action '{action}'.");
                }
                int? start = null, end = null;
                if (range?.Bars is { } bars) {
                    (start, end) = ctx.Time.ParseBars(bars);
                } else {
                    start = (range?.From ?? from) is { } f ? ctx.Time.ParsePosition(f) : null;
                    end = (range?.To ?? to) is { } t ? ctx.Time.ParsePosition(t) : null;
                }
                if (action == PlaybackActions.Seek && start is null) {
                    throw new ToolInputException(ToolInputException.InvalidParams, "seek needs 'from'.");
                }
                string? trackId = track is null ? null : ResolveTrack(ctx, track);
                state = await bridge.PlaybackControlAsync(new PlaybackControlParams {
                    Action = action, Tick = start, EndTick = end, TrackId = trackId, Loop = loop,
                }, ct).ConfigureAwait(false);
            }
            string at = ctx.Time.FormatPosition(state.Tick);
            return ToolResult.Ok($"Playback {state.State} at {at}{(state.Loop ? " (loop)" : "")}.",
                new JsonObject { ["state"] = state.State, ["position"] = at, ["loop"] = state.Loop });
        });

    static string ResolveTrack(ProjectContext ctx, string reference) {
        var tracks = ctx.Project.Tracks ?? [];
        var match = tracks.FirstOrDefault(t => t.Id == reference)
                    ?? tracks.SingleOrDefault(t => string.Equals(t.Name, reference, StringComparison.OrdinalIgnoreCase));
        return match?.Id ?? throw new ToolInputException("NOT_FOUND", $"No track with id or name '{reference}'.",
            "List tracks with ou_project_get.");
    }
}
