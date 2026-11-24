using System.Text.Json.Nodes;
using OpenUtauMcp.Server.Tools;

namespace OpenUtauMcp.Server.Safety;

/// <summary>
/// Limits which files the LLM can make OpenUtau read or write.
/// Allowed roots: Documents\OpenUtau, the folder of the open project, and folders given with
/// --allow-dir or the OPENUTAU_MCP_ALLOW_DIRS environment variable (separated by ';').
/// Existing files are never overwritten unless the caller says so.
/// </summary>
public sealed class PathPolicy {
    public const string NotAllowed = "PATH_NOT_ALLOWED";
    public const string AlreadyExists = "FILE_EXISTS";
    public const string EnvironmentVariable = "OPENUTAU_MCP_ALLOW_DIRS";

    readonly List<string> configured;

    public PathPolicy(IEnumerable<string> allowDirs) {
        var dirs = allowDirs.ToList();
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } env) {
            dirs.AddRange(env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OpenUtau"));
        configured = dirs.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> Roots(string? projectFile) {
        var roots = new List<string>(configured);
        if (!string.IsNullOrEmpty(projectFile) && Path.GetDirectoryName(projectFile) is { } dir) {
            roots.Add(Normalize(dir));
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Returns the full path if allowed; throws a tool input error otherwise.</summary>
    public string Check(string path, string? projectFile, IReadOnlyCollection<string> extensions, bool write, bool overwrite = false) {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) {
            throw new ToolInputException(ToolInputException.InvalidParams, $"'{path}' is not an absolute path.");
        }
        string full = Path.GetFullPath(path);
        string ext = Path.GetExtension(full);
        if (!extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) {
            throw new ToolInputException("UNSUPPORTED_FORMAT", $"'{ext}' files are not allowed here; use {string.Join(", ", extensions)}.");
        }
        var roots = Roots(projectFile);
        string dir = Normalize(Path.GetDirectoryName(full)!);
        if (!roots.Any(root => dir.Equals(root, StringComparison.OrdinalIgnoreCase)
                               || dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) {
            throw new ToolInputException(NotAllowed, $"{full} is outside the allowed folders.",
                "Use a path inside one of the allowed folders, or start the server with --allow-dir <folder>.",
                new JsonObject { ["allowed"] = new JsonArray(roots.Select(r => (JsonNode?)r).ToArray()) });
        }
        if (write && !overwrite && File.Exists(full)) {
            throw new ToolInputException(AlreadyExists, $"{full} already exists.", "Pass overwrite:true to replace it.");
        }
        return full;
    }

    static string Normalize(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir)));
}
