using OpenUtauMcp.Music.Randomness;

namespace OpenUtauMcp.Music.Tests;

/// <summary>
/// Exact port of CPython's random.Random (MT19937 seeded with init_by_array, 53-bit random(),
/// uniform, gauss with its cached second value). Test oracle only: it lets the C# humanizer reproduce
/// the Python prototype's numbers bit for bit.
/// </summary>
public sealed class PythonRandom : IRandomSource {
    const int N = 624, M = 397;
    readonly uint[] mt = new uint[N];
    int mti;
    double? gaussNext;

    public PythonRandom(uint seed) => InitByArray([seed]);

    void InitGenrand(uint s) {
        mt[0] = s;
        for (mti = 1; mti < N; mti++) {
            mt[mti] = 1812433253u * (mt[mti - 1] ^ (mt[mti - 1] >> 30)) + (uint)mti;
        }
    }

    void InitByArray(uint[] key) {
        InitGenrand(19650218u);
        int i = 1, j = 0;
        for (int k = Math.Max(N, key.Length); k > 0; k--) {
            mt[i] = (mt[i] ^ ((mt[i - 1] ^ (mt[i - 1] >> 30)) * 1664525u)) + key[j] + (uint)j;
            i++;
            j++;
            if (i >= N) {
                mt[0] = mt[N - 1];
                i = 1;
            }
            if (j >= key.Length) {
                j = 0;
            }
        }
        for (int k = N - 1; k > 0; k--) {
            mt[i] = (mt[i] ^ ((mt[i - 1] ^ (mt[i - 1] >> 30)) * 1566083941u)) - (uint)i;
            i++;
            if (i >= N) {
                mt[0] = mt[N - 1];
                i = 1;
            }
        }
        mt[0] = 0x80000000u;
    }

    uint GenrandInt32() {
        uint y;
        if (mti >= N) {
            int kk;
            for (kk = 0; kk < N - M; kk++) {
                y = (mt[kk] & 0x80000000u) | (mt[kk + 1] & 0x7fffffffu);
                mt[kk] = mt[kk + M] ^ (y >> 1) ^ ((y & 1u) * 0x9908b0dfu);
            }
            for (; kk < N - 1; kk++) {
                y = (mt[kk] & 0x80000000u) | (mt[kk + 1] & 0x7fffffffu);
                mt[kk] = mt[kk + (M - N)] ^ (y >> 1) ^ ((y & 1u) * 0x9908b0dfu);
            }
            y = (mt[N - 1] & 0x80000000u) | (mt[0] & 0x7fffffffu);
            mt[N - 1] = mt[M - 1] ^ (y >> 1) ^ ((y & 1u) * 0x9908b0dfu);
            mti = 0;
        }
        y = mt[mti++];
        y ^= y >> 11;
        y ^= (y << 7) & 0x9d2c5680u;
        y ^= (y << 15) & 0xefc60000u;
        y ^= y >> 18;
        return y;
    }

    public double Random() {
        uint a = GenrandInt32() >> 5, b = GenrandInt32() >> 6;
        return (a * 67108864.0 + b) * (1.0 / 9007199254740992.0);
    }

    public double Uniform(double a, double b) => a + (b - a) * Random();

    public double Gauss(double mu, double sigma) {
        double z;
        if (gaussNext is double cached) {
            z = cached;
            gaussNext = null;
        } else {
            double x2pi = Random() * (2.0 * Math.PI);
            double g2rad = Math.Sqrt(-2.0 * Math.Log(1.0 - Random()));
            z = Math.Cos(x2pi) * g2rad;
            gaussNext = Math.Sin(x2pi) * g2rad;
        }
        return mu + z * sigma;
    }
}
