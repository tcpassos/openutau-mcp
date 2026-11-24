using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace OpenUtauMcp.Server;

/// <summary>
/// Shrinks the generated tool input schemas (the tools/list token budget): removes "default": null, turns
/// ["string","null"] into "string" (every optional field is nullable; the server validates the values itself), and
/// restores [Description] texts the schema generator drops (it emits a bare {} for JsonElement properties).
/// Applied once at startup; arguments are still parsed by System.Text.Json, so nothing about validation changes.
/// </summary>
public static class SchemaCompactor {
    public static void Compact(IEnumerable<McpServerTool> tools) {
        var methods = typeof(SchemaCompactor).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => (method: m, attr: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.attr?.Name is not null)
            .ToDictionary(x => x.attr!.Name!, x => x.method);
        foreach (var tool in tools) {
            var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
            methods.TryGetValue(tool.ProtocolTool.Name, out var method);
            var parameters = method?.GetParameters().Where(p => p.Name is not null).ToDictionary(p => p.Name!, StringComparer.Ordinal);
            Clean(schema);
            if (schema["properties"] is JsonObject props && parameters is not null) {
                foreach (var (name, child) in props) {
                    if (child is JsonObject node && parameters.TryGetValue(name, out var parameter)) {
                        if (node["description"] is null && parameter.GetCustomAttribute<DescriptionAttribute>() is { } d) {
                            node["description"] = d.Description;
                        }
                        Walk(node, ElementType(parameter.ParameterType));
                    }
                }
            }
            tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(schema);
        }
    }

    static void Walk(JsonObject node, Type? type) {
        Clean(node);
        if (node["items"] is JsonObject items) {
            Walk(items, type);
            return;
        }
        if (type is null || node["properties"] is not JsonObject props) {
            return;
        }
        foreach (var (name, child) in props) {
            if (child is not JsonObject childNode) {
                continue;
            }
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (childNode["description"] is null && property?.GetCustomAttribute<DescriptionAttribute>() is { } d) {
                childNode["description"] = d.Description;
            }
            Walk(childNode, property is null ? null : ElementType(property.PropertyType));
        }
    }

    /// <summary>Removes "default": null and collapses nullable type arrays in this node only.</summary>
    static void Clean(JsonObject node) {
        if (node.TryGetPropertyValue("default", out var def) && def is null) {
            node.Remove("default");
        }
        if (node["type"] is JsonArray types) {
            var kept = types.Select(t => t?.GetValue<string>()).Where(t => t is not null and not "null").ToList();
            node["type"] = kept.Count == 1 ? kept[0] : new JsonArray(kept.Select(t => (JsonNode?)t).ToArray());
        }
    }

    /// <summary>The record type whose properties describe this schema node (unwraps nullable, arrays and lists).</summary>
    static Type? ElementType(Type type) {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsArray) {
            return ElementType(type.GetElementType()!);
        }
        if (type.IsGenericType && type.GetGenericArguments().Length == 1 && typeof(System.Collections.IEnumerable).IsAssignableFrom(type)) {
            return ElementType(type.GetGenericArguments()[0]);
        }
        return type.IsPrimitive || type == typeof(string) || type == typeof(JsonElement) ? null : type;
    }
}
