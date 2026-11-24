using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUtauMcp.Music.Humanize;
using OpenUtauMcp.Server.Units;

namespace OpenUtauMcp.Server.Tools.Music;

/// <summary>
/// The one-line "[openutau-mcp] {json}" block kept in project and part comments.
/// Only that line is replaced; the rest of the user's comment is preserved.
/// </summary>
public static class SongComments {
    public const string Prefix = "[openutau-mcp] ";

    public static JsonObject? Read(string? comment) {
        var line = (comment ?? "").Split('\n').LastOrDefault(l => l.StartsWith(Prefix, StringComparison.Ordinal));
        if (line is null) {
            return null;
        }
        try {
            return JsonNode.Parse(line[Prefix.Length..]) as JsonObject;
        } catch (JsonException) {
            return null;
        }
    }

    public static string Write(string? comment, JsonObject block) {
        var lines = (comment ?? "").Split('\n').Where(l => !l.StartsWith(Prefix, StringComparison.Ordinal) && l.Length > 0).ToList();
        lines.Add(Prefix + block.ToJsonString());
        return string.Join('\n', lines);
    }

    /// <summary>Sections stored in the project comment, converted to absolute ticks.</summary>
    public static IReadOnlyList<Section> Sections(string? projectComment, TimeMap time) {
        if (Read(projectComment)?["sections"] is not JsonArray array) {
            return [];
        }
        var list = new List<Section>();
        foreach (var node in array.OfType<JsonObject>()) {
            string? name = node["name"]?.GetValue<string>();
            string role = node["role"]?.GetValue<string>() ?? Roles.Other;
            string? from = node["from"]?.GetValue<string>(), to = node["to"]?.GetValue<string>();
            if (name is null || from is null || to is null) {
                continue;
            }
            try {
                list.Add(new Section(name, role, time.ParsePosition(from), time.ParsePosition(to)));
            } catch (UnitException) {
                // An unreadable section is skipped; the rest still applies.
            }
        }
        return list;
    }
}
