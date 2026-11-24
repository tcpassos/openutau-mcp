using OpenUtau.Core;
using OpenUtau.Core.Editing;
using OpenUtau.Core.Ustx;

namespace OpenUtauMcp.Bridge.Entry;

/// <summary>
/// Fallback start path and manual restart: OpenUtau instantiates external BatchEdits when the piano roll
/// is initialized, and lists them in the piano roll's batch edit menu.
/// </summary>
public sealed class RestartBatchEdit : BatchEdit {
    public string Name => "MCP Bridge: restart connection";

    public RestartBatchEdit() {
        BridgeHost.Start(BridgeHost.StartedByBatchEdit);
    }

    public void Run(UProject project, UVoicePart part, List<UNote> selectedNotes, DocManager docManager) {
        BridgeHost.Restart();
    }
}
