using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtauMcp.Contracts.Model;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>playback.control / playback.get (catalog section 7). UI thread only.</summary>
public static class PlaybackAdapter {
    public static PlaybackStateDto Get() {
        var pm = PlaybackManager.Inst;
        return new PlaybackStateDto {
            // OutputActive stays true while the audio device is open; PlayingMaster tracks project playback.
            State = pm.StartingToPlay ? "starting" : pm.PlayingMaster ? "playing" : "stopped",
            Tick = DocManager.Inst.playPosTick,
            Loop = pm.LoopPlayback,
        };
    }

    public static PlaybackStateDto Control(UProject project, PlaybackControlParams p) {
        var pm = PlaybackManager.Inst;
        switch (p.Action) {
            case PlaybackActions.Play:
                int trackNo = -1;
                if (p.TrackId is { } trackId) {
                    trackNo = project.tracks.IndexOf(BridgeHost.Ids.ResolveTrack(project, trackId));
                }
                if (p.Tick is < 0) {
                    throw BridgeException.OutOfRange("tick", p.Tick.Value, 0, int.MaxValue);
                }
                if (p.Loop is bool loop) {
                    pm.LoopPlayback = loop;
                }
                pm.StopPlayback();
                int start = p.Tick ?? DocManager.Inst.playPosTick;
                pm.Play(project, start, p.EndTick ?? -1, trackNo);
                // The cursor only moves once rendering starts the audio; report where playback will begin.
                return Get() with { Tick = start };
            case PlaybackActions.Pause:
                pm.PausePlayback();
                break;
            case PlaybackActions.Stop:
                pm.StopPlayback();
                break;
            case PlaybackActions.Seek:
                if (p.Tick is not int tick || tick < 0) {
                    throw BridgeException.InvalidParams("seek needs a non-negative 'tick'.", "tick");
                }
                DocManager.Inst.ExecuteCmd(new SeekPlayPosTickNotification(tick));
                break;
            default:
                throw BridgeException.InvalidParams($"Unknown playback action '{p.Action}'.", "action");
        }
        return Get();
    }
}
