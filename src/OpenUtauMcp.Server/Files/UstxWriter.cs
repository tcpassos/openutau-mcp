using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUtauMcp.Contracts.Model;

namespace OpenUtauMcp.Server.Files;

/// <summary>
/// Writes an <see cref="InMemoryProject"/> as .ustx: top-level YAML keys whose
/// values are JSON (a YAML flow subset), like the prototype that OpenUtau opened. Write-only; no YAML library.
/// The first line must be "ustx_version" for OpenUtau to recognize the format.
/// </summary>
public static class UstxWriter {
    public const string Version = "0.10";

    /// <summary>Expressions every OpenUtau project has; only others are written to "expressions".</summary>
    public static readonly HashSet<string> DefaultExpressions = new(StringComparer.Ordinal) {
        "vel", "vol", "atk", "dec", "gen", "genc", "bre", "brec", "lpf", "mod", "alt", "dir", "shft", "shfc", "tenc", "voic",
        "dyn", "pitd", "clr", "eng", "norm", "mrt", "tens", "rgn",
    };

    /// <summary>Descriptors used when OpenUtau is not running to describe an expression.</summary>
    public static ExpressionDescriptorDto? BuiltInDescriptor(string abbr) => abbr switch {
        "velc" => new ExpressionDescriptorDto { Abbr = "velc", Name = "velocity (curve)", Type = "curve", Min = 0, Max = 200, DefaultValue = 100 },
        "enec" or "ene" => new ExpressionDescriptorDto { Abbr = abbr, Name = "energy (curve)", Type = "curve", Min = -100, Max = 100, DefaultValue = 0 },
        _ when abbr.Length == 4 && abbr.StartsWith("cl", StringComparison.Ordinal) && char.IsDigit(abbr[2]) && char.IsDigit(abbr[3]) =>
            new ExpressionDescriptorDto { Abbr = abbr, Name = $"speaker {abbr[2..]} (curve)", Type = "curve", Min = 0, Max = 100, DefaultValue = 0 },
        _ => null,
    };

    static readonly JsonSerializerOptions json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = false };

    public static string Write(InMemoryProject p) {
        var sb = new StringBuilder();
        void Line(string key, JsonNode? value) => sb.Append(key).Append(": ").Append(value?.ToJsonString(json) ?? "null").Append('\n');

        Line("ustx_version", Version);
        Line("name", p.Name);
        Line("comment", p.Comment);
        Line("output_dir", "Vocal");
        Line("cache_dir", "UCache");
        Line("key", p.Key);
        Line("expressions", new JsonObject(p.Expressions.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)Descriptor(kv.Value)))));
        Line("time_signatures", new JsonArray(new JsonObject { ["bar_position"] = 0, ["beat_per_bar"] = p.BeatPerBar, ["beat_unit"] = p.BeatUnit }));
        Line("tempos", new JsonArray(new JsonObject { ["position"] = 0, ["bpm"] = p.Bpm }));
        Line("tracks", new JsonArray(p.Tracks.Select(t => (JsonNode?)new JsonObject {
            ["singer"] = t.Singer ?? "",
            ["phonemizer"] = t.Phonemizer ?? "",
            ["renderer_settings"] = new JsonObject { ["renderer"] = t.Renderer ?? "" },
            ["track_name"] = t.Name,
            ["track_color"] = t.Color,
            ["mute"] = t.Mute,
            ["solo"] = t.Solo,
            ["volume"] = t.Volume,
            ["pan"] = t.Pan,
        }).ToArray()));
        Line("voice_parts", new JsonArray(p.Parts.Select(part => (JsonNode?)Part(p, part)).ToArray()));
        Line("wave_parts", new JsonArray());
        return sb.ToString();
    }

    static JsonObject Descriptor(ExpressionDescriptorDto d) => new() {
        ["name"] = d.Name,
        ["abbr"] = d.Abbr,
        // OpenUtau's UExpressionType: 0 numerical, 1 options, 2 curve.
        ["type"] = d.Type switch { "options" => 1, "curve" => 2, "maskedCurve" => 3, _ => 0 },
        ["min"] = d.Min,
        ["max"] = d.Max,
        ["default_value"] = d.DefaultValue,
        ["is_flag"] = false,
        ["flag"] = "",
    };

    static JsonObject Part(InMemoryProject p, InMemoryProject.Part part) => new() {
        ["name"] = part.Name,
        ["comment"] = part.Comment,
        ["track_no"] = p.Tracks.IndexOf(part.Track),
        ["position"] = part.Pos,
        ["duration"] = part.Dur,
        ["notes"] = new JsonArray(part.Notes.OrderBy(n => n.Pos).Select(n => (JsonNode?)Note(n)).ToArray()),
        ["curves"] = new JsonArray(part.Curves.Where(c => c.Value.Count > 0).Select(c => (JsonNode?)new JsonObject {
            ["xs"] = new JsonArray(c.Value.Keys.Select(x => (JsonNode?)x).ToArray()),
            ["ys"] = new JsonArray(c.Value.Values.Select(y => (JsonNode?)y).ToArray()),
            ["abbr"] = c.Key,
        }).ToArray()),
    };

    static JsonObject Note(InMemoryProject.Note n) => new() {
        ["position"] = n.Pos,
        ["duration"] = n.Dur,
        ["tone"] = n.Tone,
        ["lyric"] = n.Lyric,
        ["tuning"] = n.Tuning,
        ["pitch"] = new JsonObject {
            ["data"] = new JsonArray(n.Pitch.Select(pt => (JsonNode?)new JsonObject {
                ["x"] = Round(pt.X), ["y"] = Round(pt.Y), ["shape"] = pt.Shape,
            }).ToArray()),
            ["snap_first"] = n.SnapFirst,
        },
        ["vibrato"] = new JsonObject {
            ["length"] = Round(n.Vibrato.Length), ["period"] = Round(n.Vibrato.Period), ["depth"] = Round(n.Vibrato.Depth),
            ["in"] = Round(n.Vibrato.In), ["out"] = Round(n.Vibrato.Out), ["shift"] = Round(n.Vibrato.Shift),
            ["drift"] = Round(n.Vibrato.Drift), ["vol_link"] = Round(n.Vibrato.VolLink),
        },
        // Same value for every phoneme, as the prototype wrote it (OpenUtau keeps the indexes that exist).
        ["phoneme_expressions"] = new JsonArray(n.Exprs.SelectMany(e => Enumerable.Range(0, 5).Select(i => (JsonNode?)new JsonObject {
            ["index"] = i, ["abbr"] = e.Key, ["value"] = e.Value,
        })).ToArray()),
        ["phoneme_overrides"] = new JsonArray(),
    };

    static JsonNode Round(float v) => Math.Abs(v - MathF.Round(v)) < 1e-4
        ? JsonValue.Create((int)MathF.Round(v))
        : JsonValue.Create(Math.Round((double)v, 3));
}
