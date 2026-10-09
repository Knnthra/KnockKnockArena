using System;
using System.Collections.Generic;
using System.Globalization;
using KnockKnockArena.Shared.Config;
using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Shared.Simulation
{
    /// <summary>
    /// One arena, loaded from an arena.json file.
    ///
    /// BOTH SIDES RUN THIS CODE. The server loads the file to get the collision data it
    /// simulates against; the client loads the same file, fetched from the server, to
    /// get the collision data it predicts against. Client prediction only matches if
    /// both sides end up with the same float BITS, and the way to guarantee that is for
    /// there to be exactly one loader — this one, compiled into both — sitting on top
    /// of exactly one number parser (see <see cref="JsonValue.AsFloat"/>).
    ///
    /// The previous design put the map in generated C# so the constants were shared at
    /// compile time. That worked, but it meant a new map needed a recompile of both
    /// sides. The determinism now lives in the shared parser instead of in a shared
    /// constant, and <see cref="Hash"/> is how anyone can check it held.
    /// </summary>
    public sealed class ArenaMapData
    {
        /// <summary>Human-readable map name, for logs and the HUD.</summary>
        public readonly string Name;

        public readonly float BoundsMinX;
        public readonly float BoundsMaxX;
        public readonly float BoundsMinZ;
        public readonly float BoundsMaxZ;

        /// <summary>World Y of the playable floor — CLIENT RENDERING ONLY. The server
        /// simulates pure XZ (there is no height in the protocol); the map's visual
        /// layers are cosmetic and the playable area is ONE level.</summary>
        public readonly float GroundY;

        /// <summary>Walls in FIXED iteration order. The order is part of the protocol:
        /// collision push-out visits them in exactly this order, so a client that
        /// iterates differently will predict differently at a corner.</summary>
        public readonly Aabb[] Walls;

        public readonly SpawnPoint[] SpawnPoints;

        /// <summary>Pickup id = index here. Entity id in the snapshot = 100 + index.</summary>
        public readonly PickupSpawn[] Pickups;

        /// <summary>
        /// A fingerprint of the map, computed over the PARSED FLOAT BITS rather than
        /// over the file text. That distinction is the point: if the server and the
        /// client ever turned the same file into different floats, the text would match
        /// and the bits would not — so hashing the bits turns a silent prediction
        /// desync into a mismatch the client can report at join.
        /// </summary>
        public readonly uint Hash;

        private ArenaMapData(string name, float boundsMinX, float boundsMaxX,
            float boundsMinZ, float boundsMaxZ, float groundY,
            Aabb[] walls, SpawnPoint[] spawnPoints, PickupSpawn[] pickups)
        {
            Name = name;
            BoundsMinX = boundsMinX;
            BoundsMaxX = boundsMaxX;
            BoundsMinZ = boundsMinZ;
            BoundsMaxZ = boundsMaxZ;
            GroundY = groundY;
            Walls = walls;
            SpawnPoints = spawnPoints;
            Pickups = pickups;
            Hash = ComputeHash();
        }

        // ------------------------------------------------------------------ loading

        /// <summary>
        /// Parses an arena.json and builds the collision geometry. Throws
        /// <see cref="JsonException"/> with a line and column on anything malformed —
        /// these files are hand-authorable, so failing loudly at load beats starting a
        /// match on a map with no walls.
        /// </summary>
        public static ArenaMapData Parse(string json)
        {
            JsonValue root = Json.Parse(json);

            string name = root.Has("name") ? root["name"].AsString() : "unnamed";

            JsonValue bounds = root["bounds"];
            float minX = bounds["minX"].AsFloat();
            float maxX = bounds["maxX"].AsFloat();
            float minZ = bounds["minZ"].AsFloat();
            float maxZ = bounds["maxZ"].AsFloat();
            if (!(maxX > minX) || !(maxZ > minZ))
                throw new JsonException("\"bounds\" must have maxX > minX and maxZ > minZ");

            float groundY = root.Has("groundY") ? root["groundY"].AsFloat() : 0f;

            IReadOnlyList<JsonValue> wallItems = root["walls"].Items;
            Aabb[] walls = new Aabb[wallItems.Count];
            for (int i = 0; i < wallItems.Count; i++)
            {
                JsonValue wall = wallItems[i];
                float wallMinX = wall["minX"].AsFloat();
                float wallMinZ = wall["minZ"].AsFloat();
                float wallMaxX = wall["maxX"].AsFloat();
                float wallMaxZ = wall["maxZ"].AsFloat();
                if (!(wallMaxX > wallMinX) || !(wallMaxZ > wallMinZ))
                    throw new JsonException($"wall {i} is inside out: max must exceed min on both axes");
                walls[i] = new Aabb(wallMinX, wallMinZ, wallMaxX, wallMaxZ);
            }

            IReadOnlyList<JsonValue> spawnItems = root["spawnPoints"].Items;
            if (spawnItems.Count == 0)
                throw new JsonException("\"spawnPoints\" must hold at least one point");
            SpawnPoint[] spawnPoints = new SpawnPoint[spawnItems.Count];
            for (int i = 0; i < spawnItems.Count; i++)
                spawnPoints[i] = new SpawnPoint(spawnItems[i]["x"].AsFloat(), spawnItems[i]["z"].AsFloat());

            IReadOnlyList<JsonValue> pickupItems = root["pickups"].Items;
            PickupSpawn[] pickups = new PickupSpawn[pickupItems.Count];
            for (int i = 0; i < pickupItems.Count; i++)
            {
                JsonValue pickup = pickupItems[i];
                string typeName = pickup["type"].AsString();
                if (!TryParsePickupType(typeName, out PickupType type))
                    throw new JsonException($"pickup {i} has unknown type \"{typeName}\"");
                pickups[i] = new PickupSpawn(type, pickup["x"].AsFloat(), pickup["z"].AsFloat());
            }

            ArenaMapData map = new ArenaMapData(name, minX, maxX, minZ, maxZ, groundY,
                walls, spawnPoints, pickups);

            // A file written by the exporter carries the hash the exporter computed. If
            // it disagrees with what we just computed, the file was edited after the
            // bake (or parsed differently here) — either way the visual map and this
            // collision data are no longer the same map, which is worth refusing.
            JsonValue? stamped = root.Optional("hash");
            if (stamped != null && stamped.Kind != JsonKind.Null)
            {
                uint expected = ParseHash(stamped.AsString());
                if (expected != map.Hash)
                    throw new JsonException(
                        $"map hash mismatch: the file says {FormatHash(expected)} but its contents " +
                        $"hash to {FormatHash(map.Hash)}. Re-export the map instead of editing it by hand.");
            }

            return map;
        }

        private static bool TryParsePickupType(string name, out PickupType type)
        {
            foreach (PickupType candidate in PickupTypes)
            {
                if (string.Equals(candidate.ToString(), name, StringComparison.Ordinal))
                {
                    type = candidate;
                    return true;
                }
            }
            type = default;
            return false;
        }

        private static readonly PickupType[] PickupTypes =
        {
            PickupType.Ak47Weapon, PickupType.RocketLauncherWeapon,
            PickupType.GlockAmmo, PickupType.Ak47Ammo, PickupType.RocketAmmo, PickupType.Health,
        };

        // --------------------------------------------------------------------- hash

        /// <summary>
        /// FNV-1a over the float bits and counts, in a fixed order. Not a security
        /// hash — it answers one question: "are these two sides holding the same
        /// numbers?" Order-dependent on purpose, because wall order is part of the
        /// collision rule.
        /// </summary>
        private uint ComputeHash()
        {
            uint hash = 2166136261u;
            MixFloat(ref hash, BoundsMinX);
            MixFloat(ref hash, BoundsMaxX);
            MixFloat(ref hash, BoundsMinZ);
            MixFloat(ref hash, BoundsMaxZ);
            MixFloat(ref hash, GroundY);
            MixFloat(ref hash, PlayerHalfExtentForHash);

            MixUInt(ref hash, (uint)Walls.Length);
            foreach (Aabb wall in Walls)
            {
                MixFloat(ref hash, wall.MinX);
                MixFloat(ref hash, wall.MinZ);
                MixFloat(ref hash, wall.MaxX);
                MixFloat(ref hash, wall.MaxZ);
            }

            MixUInt(ref hash, (uint)SpawnPoints.Length);
            foreach (SpawnPoint spawn in SpawnPoints)
            {
                MixFloat(ref hash, spawn.X);
                MixFloat(ref hash, spawn.Z);
            }

            MixUInt(ref hash, (uint)Pickups.Length);
            foreach (PickupSpawn pickup in Pickups)
            {
                MixUInt(ref hash, (uint)pickup.Type);
                MixFloat(ref hash, pickup.X);
                MixFloat(ref hash, pickup.Z);
            }

            return hash;
        }

        /// <summary>The player's collision size is part of what makes two clients agree
        /// about where a wall stops you, so it belongs in the fingerprint even though it
        /// does not come from the file.</summary>
        private const float PlayerHalfExtentForHash = ArenaMap.PlayerHalfExtent;

        private static void MixFloat(ref uint hash, float value)
            => MixUInt(ref hash, (uint)BitConverter.ToInt32(BitConverter.GetBytes(value), 0));

        private static void MixUInt(ref uint hash, uint value)
        {
            for (int shift = 0; shift < 32; shift += 8)
            {
                hash ^= (byte)(value >> shift);
                hash *= 16777619u;
            }
        }

        public static string FormatHash(uint hash)
            => "0x" + hash.ToString("X8", CultureInfo.InvariantCulture);

        public static uint ParseHash(string text)
        {
            string digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? text.Substring(2)
                : text;
            if (!uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
                throw new JsonException($"\"{text}\" is not a map hash (expected 8 hex digits, e.g. 0x1A2B3C4D)");
            return value;
        }
    }

    public readonly struct SpawnPoint
    {
        public readonly float X;
        public readonly float Z;

        public SpawnPoint(float x, float z)
        {
            X = x;
            Z = z;
        }

        public void Deconstruct(out float x, out float z)
        {
            x = X;
            z = Z;
        }
    }

    public readonly struct PickupSpawn
    {
        public readonly PickupType Type;
        public readonly float X;
        public readonly float Z;

        public PickupSpawn(PickupType type, float x, float z)
        {
            Type = type;
            X = x;
            Z = z;
        }

        public void Deconstruct(out PickupType type, out float x, out float z)
        {
            type = Type;
            x = X;
            z = Z;
        }
    }
}
