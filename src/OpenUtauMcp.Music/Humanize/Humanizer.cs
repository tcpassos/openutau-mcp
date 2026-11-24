using OpenUtauMcp.Music.Randomness;

namespace OpenUtauMcp.Music.Humanize;

/// <summary>
/// Humanization: turns a part's notes into planned pitch points, vibrato,
/// voice colors, breaths and curves (pitd, dyn, velc, genc). Pure and deterministic for a given seed.
///
/// Everything is computed over the WHOLE part (phrase context: neighbours, phrase peak, gaps) and then cropped
/// to the selection, so humanizing whole phrases gives exactly the whole-part result inside them.
/// The per-note loop draws random numbers in the prototype's order (pitch, vibrato, drift, fall); with one
/// shared Python-compatible source this reproduces exportar_ustx.py exactly (parity tests), while the product
/// uses an independent stream per (layer, note).
/// </summary>
public static class Humanizer {
    /// <summary>Algorithm version, recorded in part.comment. Bump when a layer's rule changes.</summary>
    public const int Version = 1;

    public const int Ppq = 480;
    public const int Grid = 5;

    public static HumanizeResult Plan(HumanizeInput input, HumanizeParams p, IRandomStreams rng, bool legacyTail = false) {
        var warnings = new List<string>();
        var counts = HumanizeParams.LayerNames.ToDictionary(n => n, _ => 0);
        var ordered = input.Notes.OrderBy(n => n.Pos).ThenBy(n => n.Tone).ToList();
        var oldBreaths = ordered.Where(n => n.IsBreath).ToList();
        var notes = ordered.Where(n => !n.IsBreath).ToList();
        if (notes.Count == 0) {
            return Empty(warnings, counts, "The part has no sung notes.");
        }

        double I = p.Intensity;
        double msPerBeat = 60000.0 / input.Bpm;
        double ticksPerMs = Ppq * input.Bpm / 60000.0;
        int MsToTicks(double ms) => RoundEven(ms * ticksPerMs);

        // Phrase marks: a phrase starts after a gap; a "+" continuation never starts a phrase.
        int count = notes.Count;
        var gapBefore = new bool[count + 1];
        for (int i = 1; i < count; i++) {
            gapBefore[i] = notes[i - 1].End < notes[i].Pos && !notes[i].Lyric.StartsWith('+');
        }
        bool Ini(int i) => i == 0 || gapBefore[i];
        bool Fim(int i) => i == count - 1 || gapBefore[i + 1];

        var phrases = new List<List<int>>();
        for (int i = 0; i < count; i++) {
            if (Ini(i)) {
                phrases.Add([]);
            }
            phrases[^1].Add(i);
        }
        var arc = new double[count];
        var dyn = p.Dynamics;
        foreach (var g in phrases) {
            int peak = g.OrderByDescending(i => notes[i].Tone).ThenBy(i => i).First();
            foreach (int i in g) {
                arc[i] = i <= peak
                    ? -dyn.Rise / 2 + dyn.Rise * ((i - g[0]) / (double)Math.Max(peak - g[0], 1))
                    : dyn.Rise / 2 - dyn.FallTo * ((i - peak) / (double)Math.Max(g[^1] - peak, 1));
            }
        }

        var plans = notes.Select(n => new NotePlan(n.Id)).ToList();
        // Reports count only the selected notes (everything is computed for context, little is applied).
        void Count(string layer, int i) {
            if (input.Selection is null || input.Selection.Contains(notes[i].Id)) {
                counts[layer]++;
            }
        }
        var pitd = new Dictionary<int, double>();
        var dynPts = new Dictionary<int, double>();
        var velc = new Dictionary<int, double>();
        var genc = new Dictionary<int, double>();
        if (legacyTail) {
            pitd[0] = 0;
        }
        bool pitdUsed = p.Drift.Enabled || p.Fall.Enabled;
        double splitAfter = Math.Round(1 - p.Portamento.Split, 10);

        for (int i = 0; i < count; i++) {
            var n = notes[i];
            var plan = plans[i];
            int abs = input.PartPos + n.Pos;
            var section = SectionAt(input.Sections, abs);
            var style = Style(p, section.Role);
            int a = n.Pos, b = n.End;
            double beats = n.Dur / (double)Ppq;
            double durMs = beats * msPerBeat;

            // scoop / portamento / overshoot
            if (Ini(i)) {
                if (p.Scoop.Enabled) {
                    var r = rng.For("scoop", abs, n.Tone);
                    double depth = -(p.Scoop.DepthCents / 10 + r.Uniform(0, p.Scoop.JitterCents / 10)) * I;
                    plan.Pitch = [new(0, Round1(depth)), new(RoundEven(Math.Min(p.Scoop.RiseMs, durMs * p.Scoop.MaxNoteFraction)), 0)];
                    Count("scoop", i);
                }
            } else if (p.Portamento.Enabled) {
                int leap = notes[i - 1].Tone - n.Tone;  // > 0: coming from above
                if (leap == 0) {
                    plan.Pitch = [new(-10, 0), new(25, 0)];
                } else {
                    var pp = p.Portamento;
                    var r = rng.For("portamento", abs, n.Tone);
                    double glide = (pp.BaseMs + pp.PerSemitoneMs * Math.Min(Math.Abs(leap), pp.CapSemitones))
                                   * r.Uniform(1 - pp.Variation, 1 + pp.Variation);
                    int before = RoundEven(-glide * pp.Split);
                    int after = RoundEven(Math.Min(glide * splitAfter, durMs * pp.MaxNoteFraction));
                    var points = new List<PitchPoint> { new(before, leap * 10), new(after, 0) };
                    if (p.Overshoot.Enabled && leap <= -p.Overshoot.MinLeapSemitones && durMs > p.Overshoot.MinNoteMs) {
                        points[^1] = new(after, Round1(p.Overshoot.AmountCents / 10 * I));
                        points.Add(new(after + p.Overshoot.SettleMs, 0));
                        Count("overshoot", i);
                    }
                    plan.Pitch = points;
                }
                Count("portamento", i);
            }

            // vibrato
            if (p.Vibrato.Enabled) {
                var v = p.Vibrato;
                if (beats >= v.MinBeats) {
                    var r = rng.For("vibrato", abs, n.Tone);
                    plan.Vibrato = new Vibrato(
                        Length: RoundEven(Math.Min(v.MaxLength, v.LengthBase + beats * v.LengthPerBeat)),
                        Period: RoundEven(r.Uniform(v.PeriodMin, v.PeriodMax)),
                        Depth: RoundEven(r.Uniform(v.DepthMin, v.DepthMax) * (style.Strong ? v.StrongFactor : v.WeakFactor) * I),
                        In: RoundEven(r.Uniform(v.InMin, v.InMax)),
                        Out: RoundEven(r.Uniform(v.OutMin, v.OutMax)),
                        Shift: RoundEven(r.Uniform(0, v.ShiftMax)));
                    Count("vibrato", i);
                } else {
                    plan.Vibrato = new Vibrato(0, 175, 20, 30, 15, 0);
                }
            }

            // voice color
            if (p.Color.Enabled && ColorFor(input, section, style) is int color) {
                plan.Color = color;
                Count("color", i);
            }

            // drift and fall (pitd)
            bool fall = p.Fall.Enabled && Fim(i) && beats >= p.Fall.MinBeats;
            if (p.Drift.Enabled) {
                var d = p.Drift;
                var r = rng.For("drift", abs, n.Tone);
                int start = a + MsToTicks(d.StartMs);
                int stop = b - MsToTicks((fall ? p.Fall.FallMs : 0) + d.EndMarginMs);
                double value = 0;
                for (int x = start; x < stop; x += MsToTicks(d.StepMs)) {
                    value = d.Rho * value + r.Gauss(0, d.SigmaCents) * I;
                    pitd[x] = value;
                }
                if (stop > start) {
                    pitd[stop] = 0;
                }
                int anchor = Math.Max(a - 1, 0);
                pitd[anchor] = pitd.GetValueOrDefault(anchor, 0);
                Count("drift", i);
            }
            if (fall) {
                var r = rng.For("fall", abs, n.Tone);
                pitd[b - MsToTicks(p.Fall.FallMs)] = 0;
                pitd[b] = p.Fall.Cents * r.Uniform(0.8, 1.3);
                pitd[b + Grid] = 0;  // the prototype used end + 1, which the 5-tick grid would merge into end
                Count("fall", i);
            }

            // dynamics: section base + phrase arc + swell inside long notes
            if (p.Dynamics.Enabled) {
                dynPts[a] = style.Dynamics + arc[i] - (Ini(i) ? dyn.AttackDip : 0);
                if (beats >= dyn.SwellBeats) {
                    dynPts[(a + b) / 2] = style.Dynamics + arc[i] + dyn.Swell;
                }
                if (Fim(i)) {
                    dynPts[b] = style.Dynamics + arc[i] - dyn.Release;
                }
                Count("dynamics", i);
            }

            if (p.Consonants.Enabled) {
                var c = p.Consonants;
                velc[a] = beats <= c.FastMaxBeats ? c.Fast : beats < c.SlowMinBeats ? c.Normal : c.Slow;
                Count("consonants", i);
            }

            if (p.Formant.Enabled) {
                var f = p.Formant;
                genc[a] = f.Base + (n.Tone >= f.HighPitch ? f.Bright : n.Tone <= f.LowPitch ? f.Dark : 0);
                Count("formant", i);
            }
        }

        // breaths before phrases that follow a long enough rest
        var breaths = new List<(BreathNote note, int noteIndex)>();
        if (p.Breaths.Enabled) {
            var br = p.Breaths;
            for (int i = 0; i < count; i++) {
                if (!Ini(i)) {
                    continue;
                }
                var n = notes[i];
                int previousEnd = i > 0 ? notes[i - 1].End : 0;
                double gap = (n.Pos - previousEnd) / (double)Ppq;
                if (gap < br.MinGapBeats) {
                    continue;
                }
                double durBeats = Math.Min(br.MaxBeats, Math.Max(br.MinBeats, gap * br.GapFactor));
                double startBeats = n.Pos / (double)Ppq - durBeats;
                var breath = new BreathNote(RoundEven(startBeats * Ppq), RoundEven(durBeats * Ppq), n.Tone, br.Lyric);
                breaths.Add((breath, i));
                if (p.Dynamics.Enabled) {
                    var style = Style(p, SectionAt(input.Sections, input.PartPos + n.Pos).Role);
                    dynPts[breath.Pos] = style.Dynamics - br.Dip;
                }
                Count("breaths", i);
            }
        }
        if (legacyTail && p.Dynamics.Enabled) {
            int tail = RoundEven((notes.Max(n => n.End) / (double)Ppq + 4) * Ppq);
            dynPts[tail] = Style(p, Roles.Outro).Dynamics;
        }

        // Crop to the selection (plus margins for breaths before and falls after).
        var selected = input.Selection is null ? null : new HashSet<string>(input.Selection);
        var chosen = Enumerable.Range(0, count).Where(i => selected is null || selected.Contains(notes[i].Id)).ToList();
        if (chosen.Count == 0) {
            return Empty(warnings, counts, "No sung note of the part is selected.");
        }
        int rangeFrom, rangeTo;
        if (selected is null) {
            rangeFrom = 0;
            rangeTo = int.MaxValue;
        } else {
            int margin = RoundEven(p.Breaths.MaxBeats * Ppq);
            rangeFrom = Math.Max(0, (notes[chosen[0]].Pos - margin) / Grid * Grid);
            rangeTo = (chosen.Max(i => notes[i].End) + 2 * Grid + Grid - 1) / Grid * Grid;
            foreach (var g in phrases) {
                int inside = g.Count(i => selected.Contains(notes[i].Id));
                if (inside > 0 && inside < g.Count) {
                    warnings.Add($"PARTIAL_PHRASE: phrase {phrases.IndexOf(g) + 1} is only partly selected; its dynamics arc is still computed over the whole phrase.");
                }
            }
        }

        var curves = new Dictionary<string, SortedDictionary<int, int>>();
        void AddCurve(string abbr, bool enabled, Dictionary<int, double> raw) {
            if (enabled) {
                curves[abbr] = new SortedDictionary<int, int>(
                    Quantize(raw).Where(kv => kv.Key >= rangeFrom && kv.Key <= rangeTo).ToDictionary(kv => kv.Key, kv => kv.Value));
            }
        }
        AddCurve("pitd", pitdUsed, pitd);
        AddCurve("dyn", p.Dynamics.Enabled, dynPts);
        AddCurve("velc", p.Consonants.Enabled, velc);
        AddCurve("genc", p.Formant.Enabled, genc);

        var chosenSet = chosen.ToHashSet();
        return new HumanizeResult {
            Notes = chosen.Select(i => plans[i]).ToList(),
            BreathsToAdd = breaths.Where(x => chosenSet.Contains(x.noteIndex)).Select(x => x.note).ToList(),
            BreathsToRemove = p.Breaths.Enabled
                ? oldBreaths.Where(n => n.Pos >= rangeFrom && n.Pos <= rangeTo).Select(n => n.Id).ToList()
                : [],
            Curves = curves,
            RangeFrom = rangeFrom,
            RangeTo = rangeTo == int.MaxValue ? curves.Values.SelectMany(c => c.Keys).DefaultIfEmpty(0).Max() : rangeTo,
            Warnings = warnings,
            LayerCounts = counts,
        };
    }

    /// <summary>
    /// Curve points on OpenUtau's 5-tick grid: values rounded half-to-even, x snapped in ascending order,
    /// a later point wins when two land on the same grid position.
    /// </summary>
    public static SortedDictionary<int, int> Quantize(IReadOnlyDictionary<int, double> raw) {
        var result = new SortedDictionary<int, int>();
        foreach (var x in raw.Keys.Order()) {
            result[RoundEven(x / (double)Grid) * Grid] = RoundEven(raw[x]);
        }
        return result;
    }

    public static int RoundEven(double x) => (int)Math.Round(x, MidpointRounding.ToEven);

    static double Round1(double x) => Math.Round(x, 1, MidpointRounding.ToEven);

    static Section SectionAt(IReadOnlyList<Section> sections, int absTick) =>
        sections.LastOrDefault(s => s.From <= absTick && absTick < s.To) ?? new Section("", Roles.Other, 0, int.MaxValue);

    static RoleStyle Style(HumanizeParams p, string role) =>
        p.RoleStyles.TryGetValue(role, out var style) ? style : p.RoleStyles[Roles.Other];

    static int? ColorFor(HumanizeInput input, Section section, RoleStyle style) {
        if (section.Name.Length > 0 && input.Colors.TryGetValue(section.Name, out int byName)) {
            return byName;
        }
        if (input.Colors.TryGetValue(section.Role, out int byRole)) {
            return byRole;
        }
        return style.Color is { } role && input.Colors.TryGetValue(role, out int byColor) ? byColor : null;
    }

    static HumanizeResult Empty(List<string> warnings, Dictionary<string, int> counts, string why) {
        warnings.Add(why);
        return new HumanizeResult {
            Notes = [], BreathsToAdd = [], BreathsToRemove = [], Curves = new Dictionary<string, SortedDictionary<int, int>>(),
            RangeFrom = 0, RangeTo = 0, Warnings = warnings, LayerCounts = counts,
        };
    }
}
