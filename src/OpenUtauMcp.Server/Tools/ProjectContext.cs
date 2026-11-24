using System.Text.Json.Nodes;
using OpenUtauMcp.Contracts.Errors;
using OpenUtauMcp.Contracts.Model;
using OpenUtauMcp.Server.Bridge;
using OpenUtauMcp.Server.Units;

namespace OpenUtauMcp.Server.Tools;

/// <summary>A fresh snapshot of the project structure plus unit conversion and reference resolution.</summary>
public sealed class ProjectContext {
    public ProjectDto Project { get; }
    public TimeMap Time { get; }

    ProjectContext(ProjectDto project) {
        Project = project;
        Time = new TimeMap(project.TimeSignatures);
    }

    public static async Task<ProjectContext> LoadAsync(IBridgeClient bridge, CancellationToken ct, params string[] include) {
        var project = await bridge.ProjectGetAsync(new ProjectGetParams {
            Include = include.Length > 0 ? include : ["tempo", "tracks", "parts"],
        }, ct).ConfigureAwait(false);
        return new ProjectContext(project);
    }

    /// <summary>Resolves a PartRef: id ("p3") or exact name. Ambiguous names list the candidates.</summary>
    public PartDto ResolvePart(string reference) {
        var parts = Project.Parts ?? [];
        var byId = parts.FirstOrDefault(p => p.Id == reference);
        if (byId is not null) {
            return byId;
        }
        var byName = parts.Where(p => string.Equals(p.Name, reference, StringComparison.Ordinal)).ToList();
        if (byName.Count == 0) {
            byName = parts.Where(p => string.Equals(p.Name, reference, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (byName.Count == 1) {
            return byName[0];
        }
        if (byName.Count > 1) {
            throw new ToolInputException(ToolInputException.AmbiguousRef, $"'{reference}' matches {byName.Count} parts.",
                "Use one of the part ids from the candidates.",
                new JsonObject { ["candidates"] = Candidates(byName) });
        }
        throw new ToolInputException(ErrorCodes.NotFound, $"No part with id or name '{reference}'.",
            "List parts with ou_project_get.",
            new JsonObject { ["parts"] = Candidates(parts) });
    }

    /// <summary>Resolves a TrackRef: id ("t2"), exact name, or 1-based position as "#2".</summary>
    public TrackDto ResolveTrack(string reference) {
        var tracks = Project.Tracks ?? [];
        var found = tracks.FirstOrDefault(t => t.Id == reference);
        if (found is null && reference.StartsWith('#') && int.TryParse(reference.AsSpan(1), out int position)) {
            found = tracks.FirstOrDefault(t => t.Index == position - 1);
        }
        if (found is not null) {
            return found;
        }
        var byName = tracks.Where(t => string.Equals(t.Name, reference, StringComparison.Ordinal)).ToList();
        if (byName.Count == 0) {
            byName = tracks.Where(t => string.Equals(t.Name, reference, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        var candidates = new JsonArray((byName.Count > 1 ? byName : tracks).Take(20)
            .Select(t => (JsonNode?)new JsonObject { ["id"] = t.Id, ["name"] = t.Name }).ToArray());
        return byName.Count switch {
            1 => byName[0],
            > 1 => throw new ToolInputException(ToolInputException.AmbiguousRef, $"'{reference}' matches {byName.Count} tracks.",
                "Use one of the track ids from the candidates.", new JsonObject { ["candidates"] = candidates }),
            _ => throw new ToolInputException(ErrorCodes.NotFound, $"No track with id or name '{reference}'.",
                "List tracks with ou_project_get.", new JsonObject { ["tracks"] = candidates }),
        };
    }

    public string TrackName(string trackId) => Project.Tracks?.FirstOrDefault(t => t.Id == trackId)?.Name ?? trackId;

    static JsonArray Candidates(IEnumerable<PartDto> parts) =>
        new(parts.Take(20).Select(p => (JsonNode?)new JsonObject { ["id"] = p.Id, ["name"] = p.Name }).ToArray());
}
