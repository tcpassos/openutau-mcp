using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenUtauMcp.Music.Humanize;

// Layer parameters. Defaults are the prototype's "Lead D" values.
// Pitch values are in cents here; the humanizer converts to OpenUtau's tenths of a semitone.

public sealed record ScoopParams(bool Enabled = true, double DepthCents = 50, double JitterCents = 30, double RiseMs = 85,
                                 double MaxNoteFraction = 0.4);

public sealed record PortamentoParams(bool Enabled = true, double BaseMs = 45, double PerSemitoneMs = 9, int CapSemitones = 7,
                                      double Variation = 0.15, double Split = 0.55, double MaxNoteFraction = 0.4);

public sealed record OvershootParams(bool Enabled = true, int MinLeapSemitones = 3, double MinNoteMs = 250, double AmountCents = 12,
                                     double SettleMs = 70);

public sealed record FallParams(bool Enabled = true, double Cents = -80, double FallMs = 90, double MinBeats = 1);

public sealed record DriftParams(bool Enabled = true, double Rho = 0.6, double SigmaCents = 4, double StepMs = 110, double StartMs = 90,
                                 double EndMarginMs = 20);

public sealed record VibratoParams(bool Enabled = true, double MinBeats = 1, double MaxLength = 80, double LengthBase = 25,
                                   double LengthPerBeat = 14, double PeriodMin = 150, double PeriodMax = 185, double DepthMin = 20,
                                   double DepthMax = 30, double StrongFactor = 1.15, double WeakFactor = 0.9, double InMin = 30,
                                   double InMax = 50, double OutMin = 10, double OutMax = 20, double ShiftMax = 40);

public sealed record DynamicsParams(bool Enabled = true, double Rise = 12, double FallTo = 10, double AttackDip = 4, double SwellBeats = 1.5,
                                    double Swell = 5, double Release = 14);

public sealed record ConsonantsParams(bool Enabled = true, double FastMaxBeats = 0.5, double SlowMinBeats = 1.5, int Fast = 112,
                                      int Normal = 100, int Slow = 90);

public sealed record FormantParams(bool Enabled = true, int Base = -6, int HighPitch = 69, int Bright = -10, int LowPitch = 62, int Dark = 4);

public sealed record BreathsParams(bool Enabled = true, double MinGapBeats = 0.75, double MinBeats = 0.25, double MaxBeats = 0.75,
                                   double GapFactor = 0.5, int Dip = 12, string Lyric = "AP");

public sealed record ColorParams(bool Enabled = true);

/// <summary>Per-role style.</summary>
public sealed record RoleStyle(int Dynamics, string? Color, bool Strong);

public sealed record HumanizeParams {
    public string Preset { get; init; } = "natural";
    public double Intensity { get; init; } = 1.0;
    public ScoopParams Scoop { get; init; } = new();
    public PortamentoParams Portamento { get; init; } = new();
    public OvershootParams Overshoot { get; init; } = new();
    public FallParams Fall { get; init; } = new();
    public DriftParams Drift { get; init; } = new();
    public VibratoParams Vibrato { get; init; } = new();
    public DynamicsParams Dynamics { get; init; } = new();
    public ConsonantsParams Consonants { get; init; } = new();
    public FormantParams Formant { get; init; } = new();
    public BreathsParams Breaths { get; init; } = new();
    public ColorParams Color { get; init; } = new();
    public IReadOnlyDictionary<string, RoleStyle> RoleStyles { get; init; } = DefaultRoles;

    public static readonly IReadOnlyDictionary<string, RoleStyle> DefaultRoles = new Dictionary<string, RoleStyle> {
        [Roles.Intro] = new(0, ColorRoles.Normal, false),
        [Roles.Verse] = new(-25, ColorRoles.Normal, false),
        [Roles.Pre] = new(-5, ColorRoles.Power, false),
        [Roles.Chorus] = new(15, ColorRoles.Power, true),
        [Roles.Drop] = new(0, ColorRoles.Normal, true),
        [Roles.Bridge] = new(-35, ColorRoles.Normal, false),
        [Roles.Outro] = new(-40, ColorRoles.Soft, false),
        [Roles.Other] = new(0, null, false),
    };

    public static readonly string[] LayerNames = [
        "breaths", "color", "scoop", "portamento", "overshoot", "vibrato", "drift", "fall", "dynamics", "consonants", "formant",
    ];

    /// <summary>"pitch" in preserve means every layer that writes pitch.</summary>
    public static readonly string[] PitchLayers = ["scoop", "portamento", "overshoot", "vibrato", "drift", "fall"];

    public static IReadOnlyList<string> PresetNames => ["natural", "backing", "chops"];

    /// <summary>Presets grounded in the prototype.</summary>
    public static HumanizeParams FromPreset(string preset) => preset switch {
        "natural" => new HumanizeParams(),
        "backing" => new HumanizeParams {
            Preset = "backing",
            Intensity = 0.7,
            Breaths = new BreathsParams(Enabled: false),
            Formant = new FormantParams(Base: 10),
            Fall = new FallParams(Cents: -60, FallMs: 80),
            RoleStyles = DefaultRoles.ToDictionary(kv => kv.Key, kv => kv.Value with { Dynamics = -10 }),
        },
        "chops" => new HumanizeParams {
            Preset = "chops",
            Breaths = new BreathsParams(Enabled: false),
            Formant = new FormantParams(Base: -10),
            Fall = new FallParams(Cents: -250, FallMs: 120),
            RoleStyles = DefaultRoles.ToDictionary(kv => kv.Key, kv => kv.Value with { Dynamics = 5, Color = ColorRoles.Power }),
        },
        _ => throw new ArgumentException($"Unknown preset '{preset}'. Use {string.Join(", ", PresetNames)}."),
    };

    static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Applies {layer: false | {param: value}} overrides and a preserve list (layer names, "pitch").
    /// Unknown layers or parameters throw <see cref="ArgumentException"/>.
    /// </summary>
    public HumanizeParams With(JsonObject? layers, IEnumerable<string>? preserve) {
        var result = this;
        foreach (var (name, value) in layers ?? new JsonObject()) {
            result = result.Override(name, value);
        }
        foreach (var name in preserve ?? []) {
            if (name == "pitch") {
                foreach (var layer in PitchLayers) {
                    result = result.Override(layer, JsonValue.Create(false));
                }
            } else if (LayerNames.Contains(name)) {
                result = result.Override(name, JsonValue.Create(false));
            } else {
                throw new ArgumentException($"Unknown layer in preserve: '{name}'. Use {string.Join(", ", LayerNames)} or pitch.");
            }
        }
        return result;
    }

    HumanizeParams Override(string layer, JsonNode? value) {
        if (value is JsonValue v && v.TryGetValue(out bool enabled)) {
            value = new JsonObject { ["enabled"] = enabled };
        }
        if (value is not JsonObject patch) {
            throw new ArgumentException($"layers.{layer} must be false, true or an object of parameters.");
        }
        return layer switch {
            "scoop" => this with { Scoop = Merge(Scoop, patch, layer) },
            "portamento" => this with { Portamento = Merge(Portamento, patch, layer) },
            "overshoot" => this with { Overshoot = Merge(Overshoot, patch, layer) },
            "fall" => this with { Fall = Merge(Fall, patch, layer) },
            "drift" => this with { Drift = Merge(Drift, patch, layer) },
            "vibrato" => this with { Vibrato = Merge(Vibrato, patch, layer) },
            "dynamics" => this with { Dynamics = Merge(Dynamics, patch, layer) },
            "consonants" => this with { Consonants = Merge(Consonants, patch, layer) },
            "formant" => this with { Formant = Merge(Formant, patch, layer) },
            "breaths" => this with { Breaths = Merge(Breaths, patch, layer) },
            "color" => this with { Color = Merge(Color, patch, layer) },
            _ => throw new ArgumentException($"Unknown layer '{layer}'. Use {string.Join(", ", LayerNames)}."),
        };
    }

    static T Merge<T>(T current, JsonObject patch, string layer) {
        var node = JsonSerializer.SerializeToNode(current, json)!.AsObject();
        foreach (var (key, value) in patch) {
            var match = node.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"Unknown parameter layers.{layer}.{key}. Known: {string.Join(", ", node.Select(kv => kv.Key))}.");
            node[match] = value?.DeepClone();
        }
        return node.Deserialize<T>(json)!;
    }

    /// <summary>Parameters as JSON, for reports and catalogs.</summary>
    public JsonObject Describe() => JsonSerializer.SerializeToNode(this, json)!.AsObject();
}
