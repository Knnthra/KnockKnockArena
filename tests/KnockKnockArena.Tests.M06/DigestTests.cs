using KnockKnockArena.Shared.Protocol.Udp;
using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Tests.M06;

/// <summary>
/// Module 6: the bit-perfect self-check. SimulationDigest loads the course map, prints
/// every float as raw bits, replays a fixed input script through YOUR movement rule
/// (2000 ticks, then 45 ticks into every face of all 128 walls) and prints the
/// positions' digests. The output must equal the answer key's, character for character:
/// identical text is identical bits, and identical bits is what prediction rests on.
/// </summary>
[Collection("ArenaMap")]
public class DigestTests
{
    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static readonly string Facit = Normalize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "facit_digest.txt")));

    private static string Yours() =>
        Normalize(SimulationDigest.Describe(ArenaMapData.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "arena.json")))));

    [Fact]
    public void TheMapIsTheAnswerKeysMap()
    {
        ArenaMapData map = ArenaMapData.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "arena.json")));
        Assert.Equal("0x98A578E2", ArenaMapData.FormatHash(map.Hash));
        Assert.Equal(128, map.Walls.Length);
    }

    [Fact]
    public void Digest_EqualsTheAnswerKey_LineForLine()
    {
        string[] expected = Facit.TrimEnd('\n').Split('\n');
        string[] actual = Yours().TrimEnd('\n').Split('\n');

        // Report the FIRST differing line: where in the chain it diverges says why
        // (see README.md - the map, the walk or the wall tour).
        for (int i = 0; i < Math.Min(expected.Length, actual.Length); i++)
        {
            if (expected[i] != actual[i])
                Assert.Fail($"first difference at line {i + 1}:\n  facit: {expected[i]}\n  yours: {actual[i]}");
        }
        Assert.Equal(expected.Length, actual.Length);
    }

    /// <summary>
    /// SimulationDigest aims only in "nice" directions (the axes and the diagonals), and
    /// along those a float chain and a double calculation happen to round to the same
    /// bits. This walk aims at ODD points - a fixed pseudo-random sequence, no
    /// trigonometry - so a step computed in double, or a facing from Atan2/Cos/Sin,
    /// shows up as different bits. The expected digest is the answer key's.
    /// </summary>
    [Fact]
    public void OddAimWalk_EqualsTheAnswerKey()
    {
        ArenaMap.Load(ArenaMapData.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "arena.json"))));
        float x = ArenaMap.SpawnPoints[3].X, z = ArenaMap.SpawnPoints[3].Z;
        MovementSimulation.DashState dash = default;
        uint seed = 12345, digest = 2166136261u;
        MovementBits[] moves =
        {
            MovementBits.W, MovementBits.W | MovementBits.D, MovementBits.A | MovementBits.Walk,
            MovementBits.W | MovementBits.Sprint, MovementBits.S | MovementBits.D, MovementBits.W | MovementBits.A | MovementBits.Sprint,
        };

        for (int tick = 0; tick < 6000; tick++)
        {
            seed = seed * 1664525u + 1013904223u;
            float offsetX = (int)(seed >> 8) % 20001 / 1000f - 10f;   // -10..10 m, in odd steps
            seed = seed * 1664525u + 1013904223u;
            float offsetZ = (int)(seed >> 8) % 20001 / 1000f - 10f;
            MovementBits movement = moves[tick / 40 % moves.Length];
            if (tick % 61 == 7)
                movement |= MovementBits.Dash;

            MovementSimulation.Step(ref x, ref z, ref dash, movement, x + offsetX, z + offsetZ, GameConstants.TickDeltaTime);

            foreach (float value in new[] { x, z })
            {
                uint bits = (uint)BitConverter.SingleToInt32Bits(value);
                for (int shift = 0; shift < 32; shift += 8)
                {
                    digest ^= (byte)(bits >> shift);
                    digest *= 16777619u;
                }
            }
        }

        Assert.Equal("0x78A74F3D", "0x" + digest.ToString("X8"));
    }

    [Theory]
    [InlineData("sim final   430DC1D0 4306DCCB")]
    [InlineData("sim digest  0xEBBDFD52")]
    [InlineData("tour final  43080000 434F25A1")]
    [InlineData("tour digest 0x89E0AA3B")]
    public void Digest_KeyLines(string line)
    {
        Assert.Contains(line + "\n", Yours());
    }
}
