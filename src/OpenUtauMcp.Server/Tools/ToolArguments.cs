using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OpenUtauMcp.Server.Tools;

/// <summary>
/// Checks tool arguments against the tool's input schema and parameter types before the SDK binds them. The SDK
/// reports a binding failure (wrong JSON shape, missing required argument) only as "An error occurred invoking
/// 'tool'", and silently drops unknown arguments; here they become INVALID_PARAMS naming the argument, the JSON path
/// and the expected schema, so the client can fix the call.
/// </summary>
public static partial class ToolArguments {
    const int MaxSchemaChars = 700;

    static readonly Lazy<Dictionary<string, Dictionary<string, Type>>> parameterTypes = new(BuildParameterTypes);

    [GeneratedRegex(@"\.(?<prop>[^.\[]+)|\[(?<index>\d+)\]")]
    private static partial Regex PathSegment();

    /// <summary>Request filter for McpServerOptions.Filters.Request.CallToolFilters.</summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, ct) => {
            if (context.MatchedPrimitive is McpServerTool tool
                && Validate(tool.ProtocolTool.Name, context.Params?.Arguments, tool.ProtocolTool.InputSchema) is { } error) {
                return error;
            }
            return await next(context, ct).ConfigureAwait(false);
        };

    /// <summary>Returns an INVALID_PARAMS result for the first problem found, or null when the arguments bind.</summary>
    public static CallToolResult? Validate(string toolName, IDictionary<string, JsonElement>? arguments, JsonElement inputSchema) {
        arguments ??= new Dictionary<string, JsonElement>();
        var properties = inputSchema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object
            ? p.EnumerateObject().ToDictionary(x => x.Name, x => x.Value)
            : new Dictionary<string, JsonElement>();
        var required = inputSchema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => x.GetString()!).ToList()
            : [];

        foreach (var name in arguments.Keys) {
            if (!properties.ContainsKey(name)) {
                string? close = Closest(name, properties.Keys);
                return Error(close is null
                        ? $"{toolName} has no argument '{name}'."
                        : $"{toolName} has no argument '{name}'; did you mean '{close}'?",
                    $"Arguments: {string.Join(", ", properties.Keys)}.",
                    new JsonObject { ["argument"] = name });
            }
        }
        foreach (var name in required) {
            if (!arguments.TryGetValue(name, out var value) || value.ValueKind is JsonValueKind.Undefined) {
                return Error($"{toolName} needs the argument '{name}'.", Expected(properties[name]),
                    new JsonObject { ["argument"] = name, ["expected"] = SchemaNode(properties[name]) });
            }
        }
        var types = parameterTypes.Value.GetValueOrDefault(toolName);
        foreach (var (name, value) in arguments) {
            if (types?.GetValueOrDefault(name) is not { } type || type == typeof(JsonElement) || type == typeof(JsonElement?)) {
                continue;
            }
            try {
                JsonSerializer.Deserialize(value, type, McpJsonUtilities.DefaultOptions);
            } catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException) {
                string path = e is JsonException { Path: { } jsonPath } ? jsonPath : "$";
                string where = name + path.TrimStart('$');
                var schema = SchemaAt(properties[name], path) ?? properties[name];
                return Error($"'{where}' has the wrong shape for {toolName}: got {Describe(At(value, path))}.",
                    Expected(schema),
                    new JsonObject { ["argument"] = name, ["path"] = where, ["expected"] = SchemaNode(schema) });
            }
        }
        return null;
    }

    static CallToolResult Error(string message, string hint, JsonObject details) =>
        ToolResult.Error(ToolInputException.InvalidParams, message, hint, details);

    static readonly JsonSerializerOptions readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string Expected(JsonElement schema) {
        string text = JsonNode.Parse(schema.GetRawText())?.ToJsonString(readable) ?? schema.GetRawText();
        return "Expected: " + (text.Length > MaxSchemaChars ? text[..MaxSchemaChars] + "..." : text);
    }

    static JsonNode? SchemaNode(JsonElement schema) =>
        schema.GetRawText().Length > MaxSchemaChars * 2 ? JsonValue.Create("(see the tool's input schema)") : JsonNode.Parse(schema.GetRawText());

    /// <summary>The value at a System.Text.Json error path ("$.add[0].bpm"), or the whole value when it is not there.</summary>
    static JsonElement At(JsonElement value, string path) {
        var current = value;
        foreach (Match m in PathSegment().Matches(path)) {
            if (m.Groups["prop"].Success && current.ValueKind == JsonValueKind.Object
                && current.TryGetProperty(m.Groups["prop"].Value, out var next)) {
                current = next;
            } else if (m.Groups["index"].Success && current.ValueKind == JsonValueKind.Array
                       && int.Parse(m.Groups["index"].Value) is int i && i < current.GetArrayLength()) {
                current = current[i];
            } else {
                break;
            }
        }
        return current;
    }

    /// <summary>The sub-schema at an error path, following properties and items (and nullable anyOf branches).</summary>
    static JsonElement? SchemaAt(JsonElement schema, string path) {
        JsonElement current = schema;
        foreach (Match m in PathSegment().Matches(path)) {
            current = Concrete(current);
            if (m.Groups["prop"].Success) {
                if (!current.TryGetProperty("properties", out var props)) {
                    return null;
                }
                var prop = props.EnumerateObject()
                    .FirstOrDefault(x => x.Name.Equals(m.Groups["prop"].Value, StringComparison.OrdinalIgnoreCase));
                if (prop.Value.ValueKind == JsonValueKind.Undefined) {
                    return null;
                }
                current = prop.Value;
            } else if (current.TryGetProperty("items", out var items)) {
                current = items;
            } else {
                return null;
            }
        }
        return current;
    }

    static JsonElement Concrete(JsonElement schema) {
        foreach (string key in new[] { "anyOf", "oneOf" }) {
            if (schema.TryGetProperty(key, out var options) && options.ValueKind == JsonValueKind.Array) {
                foreach (var option in options.EnumerateArray()) {
                    if (!(option.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "null")) {
                        return option;
                    }
                }
            }
        }
        return schema;
    }

    /// <summary>The known name closest to a misspelled one (case, or at most 2 edits), if any.</summary>
    static string? Closest(string name, IEnumerable<string> known) {
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (var candidate in known) {
            int d = candidate.Equals(name, StringComparison.OrdinalIgnoreCase) ? 0
                : Distance(name.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (d < bestDistance) {
                (best, bestDistance) = (candidate, d);
            }
        }
        return bestDistance <= 2 ? best : null;
    }

    static int Distance(string a, string b) {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++) {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++) {
                int above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }

    static string Describe(JsonElement value) => value.ValueKind switch {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "a list",
        JsonValueKind.String => $"the string {Truncate(value.GetRawText())}",
        JsonValueKind.Number => $"the number {value.GetRawText()}",
        JsonValueKind.True or JsonValueKind.False => $"the boolean {value.GetRawText()}",
        JsonValueKind.Null => "null",
        _ => "nothing",
    };

    static string Truncate(string text) => text.Length > 40 ? text[..40] + "...\"" : text;

    /// <summary>Tool name -> parameter name -> CLR type, from the [McpServerTool] methods of this assembly.</summary>
    static Dictionary<string, Dictionary<string, Type>> BuildParameterTypes() {
        var result = new Dictionary<string, Dictionary<string, Type>>();
        foreach (var type in typeof(ToolArguments).Assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)) {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)) {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is not { } attribute) {
                    continue;
                }
                result[attribute.Name ?? method.Name] = method.GetParameters()
                    .Where(x => x.Name is not null)
                    .ToDictionary(x => x.Name!, x => x.ParameterType);
            }
        }
        return result;
    }
}
