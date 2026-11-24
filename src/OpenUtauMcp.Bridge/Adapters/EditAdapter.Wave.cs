using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Ops;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>part.addWave: an audio part, as OpenUtau's "Import audio" does (UWavePart + Load).</summary>
public static partial class EditAdapter {
    static readonly HashSet<string> audioExtensions = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".flac", ".mp3", ".ogg", ".aiff", ".aif" };

    static Action PlanPartAddWave(PlanContext ctx, PartAddWaveOp w, int index) {
        string at = $"ops[{index}]";
        ctx.ValidateRef(w.TrackId, "track", index);
        if (w.Pos < 0) {
            throw BridgeException.OutOfRange($"{at}.pos", w.Pos, 0, int.MaxValue);
        }
        string file = Path.GetFullPath(w.File);
        if (!audioExtensions.Contains(Path.GetExtension(file))) {
            throw new BridgeException(ErrorCodes.UnsupportedFormat, $"Audio parts take {string.Join(", ", audioExtensions)} files.",
                new() { ["path"] = file });
        }
        if (!File.Exists(file)) {
            throw new BridgeException(ErrorCodes.IoError, $"File not found: {file}", new() { ["path"] = file });
        }
        ctx.DeclareRef(w.Ref, "wavePart", index);
        return () => {
            var track = ctx.Track(w.TrackId);
            var part = new UWavePart { FilePath = file, trackNo = track.TrackNo, position = w.Pos, name = Path.GetFileName(file) };
            part.Load(ctx.Project);
            if (part.Duration <= 0) {
                throw new InvalidOperationException($"OpenUtau could not read the audio file {file}");
            }
            PlanContext.Doc.ExecuteCmd(new AddPartCommand(ctx.Project, part));
            ctx.Bind(w.Ref, $"#{index}", part, BridgeHost.Ids.IdOf(part));
        };
    }
}
