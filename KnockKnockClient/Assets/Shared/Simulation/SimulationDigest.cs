using System;
using System.Text;
using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Shared.Simulation
{
    /// <summary>
    /// The bit-perfect self-check (M06). Loads a map, replays a fixed input script
    /// through the shared movement rule, and prints every float as raw bits.
    ///
    /// Run it on two runtimes and diff the output. Identical text means those two can
    /// predict each other's movement exactly; a single differing line names what
    /// diverged. We run it on .NET (the server), Unity Mono (the editor) and IL2CPP
    /// (the standalone build), because "same source" is not the same as "same bits" —
    /// it took explicit float casts throughout the movement rule to make it true.
    ///
    /// This lives in the SHARED code on purpose: a check that each side implements
    /// separately can agree by accident. There is one routine, and all three runtimes
    /// compile it.
    ///
    /// Note what the script deliberately does NOT use: trigonometry. Math.Cos and
    /// Math.Sin are not bit-identical across runtimes, so an aim point computed with
    /// them would measure the platform's maths library instead of this simulation. In
    /// the real game the aim point is an INPUT — the client sends two floats and the
    /// server replays those exact bits — so feeding fixed directions here is also the
    /// more faithful test.
    /// </summary>
    public static class SimulationDigest
    {
        /// <summary>Long enough to walk the whole arena and pile into the bounds and
        /// every wall repeatedly.</summary>
        public const int Ticks = 2000;

        public static string Describe(ArenaMapData map)
        {
            if (map == null)
                throw new ArgumentNullException(nameof(map));

            ArenaMap.Load(map);

            StringBuilder sb = new StringBuilder();
            sb.Append("map         ").Append(map.Name).Append('\n');
            sb.Append("hash        ").Append(ArenaMapData.FormatHash(map.Hash)).Append('\n');
            sb.Append("bounds      ")
              .Append(Bits(map.BoundsMinX)).Append(' ').Append(Bits(map.BoundsMaxX)).Append(' ')
              .Append(Bits(map.BoundsMinZ)).Append(' ').Append(Bits(map.BoundsMaxZ)).Append('\n');
            sb.Append("groundY     ").Append(Bits(map.GroundY)).Append('\n');

            for (int i = 0; i < map.Walls.Length; i++)
            {
                Aabb w = map.Walls[i];
                sb.Append("wall ").Append(Pad(i)).Append(Bits(w.MinX)).Append(' ').Append(Bits(w.MinZ))
                  .Append(' ').Append(Bits(w.MaxX)).Append(' ').Append(Bits(w.MaxZ)).Append('\n');
            }
            for (int i = 0; i < map.SpawnPoints.Length; i++)
                sb.Append("spawn ").Append(Pad(i)).Append(Bits(map.SpawnPoints[i].X))
                  .Append(' ').Append(Bits(map.SpawnPoints[i].Z)).Append('\n');
            for (int i = 0; i < map.Pickups.Length; i++)
                sb.Append("pickup ").Append(Pad(i)).Append(map.Pickups[i].Type).Append(' ')
                  .Append(Bits(map.Pickups[i].X)).Append(' ').Append(Bits(map.Pickups[i].Z)).Append('\n');

            float x = map.SpawnPoints[0].X;
            float z = map.SpawnPoints[0].Z;
            MovementSimulation.DashState dash = default;
            uint digest = 2166136261u;

            for (int tick = 0; tick < Ticks; tick++)
            {
                MovementBits movement = MovementForTick(tick);
                if (tick % 53 == 0)
                    movement |= MovementBits.Dash;

                (float dirX, float dirZ) = AimDirections[tick % AimDirections.Length];
                float aimX = (float)(x + (float)(dirX * 10f));
                float aimZ = (float)(z + (float)(dirZ * 10f));

                MovementSimulation.Step(ref x, ref z, ref dash, movement, aimX, aimZ,
                    GameConstants.TickDeltaTime);

                Mix(ref digest, (uint)BitConverter.ToInt32(BitConverter.GetBytes(x), 0));
                Mix(ref digest, (uint)BitConverter.ToInt32(BitConverter.GetBytes(z), 0));
            }

            sb.Append("sim final   ").Append(Bits(x)).Append(' ').Append(Bits(z)).Append('\n');
            sb.Append("sim digest  0x").Append(digest.ToString("X8")).Append('\n');

            AppendWallTour(sb, map);
            return sb.ToString();
        }

        /// <summary>Ticks spent driving into each face of each wall.</summary>
        public const int TourTicksPerFace = 45;

        /// <summary>
        /// The walk above starts at spawn 0 and, with the aim turning every tick,
        /// stays near it — on a large map it touches only a couple of walls. The
        /// wall tour makes sure the collision rule meets EVERY wall: for each wall,
        /// for each of its four faces, the player starts 3 m out from the face's
        /// centre and drives into it (run, then sprint, then walk, with a dash).
        /// Its digest is reported separately, so the walk's digest stays comparable
        /// with earlier runs.
        /// </summary>
        private static void AppendWallTour(StringBuilder sb, ArenaMapData map)
        {
            uint tour = 2166136261u;
            float x = 0f, z = 0f;
            const float half = ArenaMap.PlayerHalfExtent;

            for (int i = 0; i < map.Walls.Length; i++)
            {
                Aabb w = map.Walls[i];
                float centreX = (float)((float)(w.MinX + w.MaxX) * 0.5f);
                float centreZ = (float)((float)(w.MinZ + w.MaxZ) * 0.5f);

                for (int face = 0; face < 4; face++)
                {
                    switch (face)
                    {
                        case 0: x = (float)(w.MinX - (float)(half + 3f)); z = centreZ; break; // west
                        case 1: x = (float)(w.MaxX + (float)(half + 3f)); z = centreZ; break; // east
                        case 2: x = centreX; z = (float)(w.MinZ - (float)(half + 3f)); break; // south
                        default: x = centreX; z = (float)(w.MaxZ + (float)(half + 3f)); break; // north
                    }
                    MovementSimulation.DashState dash = default;

                    for (int t = 0; t < TourTicksPerFace; t++)
                    {
                        MovementBits movement = t < 15 ? MovementBits.W
                            : t < 30 ? MovementBits.W | MovementBits.Sprint
                            : MovementBits.W | MovementBits.Walk;
                        if (t == 5)
                            movement |= MovementBits.Dash;

                        // Aim at the wall's centre: character-relative W drives into it.
                        MovementSimulation.Step(ref x, ref z, ref dash, movement, centreX, centreZ,
                            GameConstants.TickDeltaTime);

                        Mix(ref tour, (uint)BitConverter.ToInt32(BitConverter.GetBytes(x), 0));
                        Mix(ref tour, (uint)BitConverter.ToInt32(BitConverter.GetBytes(z), 0));
                    }
                }
            }

            sb.Append("tour final  ").Append(Bits(x)).Append(' ').Append(Bits(z)).Append('\n');
            sb.Append("tour digest 0x").Append(tour.ToString("X8")).Append('\n');
        }

        /// <summary>Every movement combination in turn: walk, run, sprint, strafe,
        /// backwards, and the diagonals — each held long enough to reach a wall.</summary>
        private static MovementBits MovementForTick(int tick)
        {
            switch (tick / 97 % 9)
            {
                case 0: return MovementBits.W;
                case 1: return MovementBits.W | MovementBits.D;
                case 2: return MovementBits.D | MovementBits.Walk;
                case 3: return MovementBits.W | MovementBits.Sprint;
                case 4: return MovementBits.S;
                case 5: return MovementBits.A | MovementBits.S;
                case 6: return MovementBits.A;
                case 7: return MovementBits.W | MovementBits.A | MovementBits.Sprint;
                default: return MovementBits.W | MovementBits.D | MovementBits.Sprint;
            }
        }

        private static readonly (float X, float Z)[] AimDirections =
        {
            (1f, 0f), (0.70710678f, 0.70710678f), (0f, 1f), (-0.70710678f, 0.70710678f),
            (-1f, 0f), (-0.70710678f, -0.70710678f), (0f, -1f), (0.70710678f, -0.70710678f),
        };

        private static string Pad(int index) => (index + "       ").Substring(0, 7);

        private static string Bits(float value)
            => BitConverter.ToInt32(BitConverter.GetBytes(value), 0).ToString("X8");

        private static void Mix(ref uint hash, uint value)
        {
            for (int shift = 0; shift < 32; shift += 8)
            {
                hash ^= (byte)(value >> shift);
                hash *= 16777619u;
            }
        }
    }
}
