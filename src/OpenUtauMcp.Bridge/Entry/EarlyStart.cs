using OpenUtau.Api;
using OpenUtau.Core.Ustx;

namespace OpenUtauMcp.Bridge.Entry;

/// <summary>
/// Early start. At startup OpenUtau scans plugin DLLs for Phonemizer subclasses and reads
/// their <see cref="PhonemizerAttribute"/> (PhonemizerFactory.Get). Constructing this derived attribute
/// starts the bridge. Its name and tag are empty, so PhonemizerFactory skips the type and nothing is
/// added to the phonemizer list.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class McpBridgeStartAttribute : PhonemizerAttribute {
    public McpBridgeStartAttribute() : base(name: "", tag: "") {
        BridgeHost.Start(BridgeHost.StartedByEarlyStart);
    }
}

/// <summary>Carrier type for <see cref="McpBridgeStartAttribute"/>. Never registered nor instantiated.</summary>
[McpBridgeStart]
public sealed class McpBridgeStartup : Phonemizer {
    public override void SetSinger(USinger singer) { }

    public override Result Process(Note[] notes, Note? prev, Note? next, Note? prevNeighbour, Note? nextNeighbour, Note[] prevs) =>
        new() { phonemes = [] };
}
