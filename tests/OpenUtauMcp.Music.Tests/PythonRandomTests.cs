using System.Text.Json;

namespace OpenUtauMcp.Music.Tests;

public class PythonRandomTests {
    [Theory]
    [InlineData(7u)]
    [InlineData(23u)]
    [InlineData(5u)]
    public void Sequence_MatchesCPython(uint seed) {
        using var doc = JsonDocument.Parse(File.ReadAllText("python-random-golden.json"));
        var rng = new PythonRandom(seed);

        foreach (var entry in doc.RootElement.GetProperty(seed.ToString()).EnumerateArray()) {
            string kind = entry[0].GetString()!;
            double expected = entry[1].GetDouble();
            double actual = kind switch {
                "random" => rng.Random(),
                "uniform" when expected < 2 => rng.Uniform(0.8, 1.3),
                "uniform" => rng.Uniform(150, 185),
                "gauss" => rng.Gauss(0, 4),
                _ => throw new InvalidOperationException(kind),
            };
            Assert.Equal(expected, actual);
        }
    }
}
