namespace OpenUtauMcp.Music.Randomness;

/// <summary>Source of random numbers for the musical layers.</summary>
public interface IRandomSource {
    /// <summary>a + (b - a) * u, u in [0, 1).</summary>
    double Uniform(double a, double b);

    /// <summary>Normal distribution.</summary>
    double Gauss(double mu, double sigma);
}

/// <summary>Gives each (layer, note) its own random stream.</summary>
public interface IRandomStreams {
    IRandomSource For(string layer, int noteTick, int tone);
}

/// <summary>
/// Product streams: an independent PCG32 per (seed, layer, note position, tone). Turning a layer off does not
/// change the others, iteration order does not matter, and moving a note only changes that note's draws.
/// </summary>
public sealed class SubstreamRandom(long seed) : IRandomStreams {
    public IRandomSource For(string layer, int noteTick, int tone) {
        ulong h = SplitMix((ulong)seed);
        foreach (char c in layer) {
            h = SplitMix(h ^ c);
        }
        h = SplitMix(h ^ (ulong)(uint)noteTick);
        h = SplitMix(h ^ ((ulong)(uint)tone << 32));
        return new Pcg32(h, SplitMix(h ^ 0x9E3779B97F4A7C15UL));
    }

    static ulong SplitMix(ulong x) {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}

/// <summary>One shared sequential source for every layer and note (used by the parity tests).</summary>
public sealed class SharedRandom(IRandomSource source) : IRandomStreams {
    public IRandomSource For(string layer, int noteTick, int tone) => source;
}

/// <summary>PCG-XSH-RR 32-bit generator (O'Neill), 53-bit doubles, Box-Muller normals.</summary>
public sealed class Pcg32 : IRandomSource {
    const ulong Multiplier = 6364136223846793005UL;
    ulong state;
    readonly ulong increment;
    double? spare;

    public Pcg32(ulong seed, ulong sequence) {
        increment = (sequence << 1) | 1UL;
        state = 0;
        NextUInt();
        state += seed;
        NextUInt();
    }

    public uint NextUInt() {
        ulong old = state;
        state = old * Multiplier + increment;
        uint xorShifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorShifted >> rot) | (xorShifted << (-rot & 31));
    }

    public double NextDouble() {
        ulong a = NextUInt() >> 5, b = NextUInt() >> 6;
        return (a * 67108864.0 + b) * (1.0 / 9007199254740992.0);
    }

    public double Uniform(double a, double b) => a + (b - a) * NextDouble();

    public double Gauss(double mu, double sigma) {
        if (spare is double z) {
            spare = null;
            return mu + z * sigma;
        }
        double angle = NextDouble() * 2.0 * Math.PI;
        double radius = Math.Sqrt(-2.0 * Math.Log(1.0 - NextDouble()));
        spare = Math.Sin(angle) * radius;
        return mu + Math.Cos(angle) * radius * sigma;
    }
}
