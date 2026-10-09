using System;

namespace KnockKnockArena.Shared.Simulation
{
    /// <summary>
    /// One axis-aligned wall box on the XZ plane.
    /// </summary>
    public readonly struct Aabb
    {
        public readonly float MinX;
        public readonly float MinZ;
        public readonly float MaxX;
        public readonly float MaxZ;

        public Aabb(float minX, float minZ, float maxX, float maxZ)
        {
            MinX = minX;
            MinZ = minZ;
            MaxX = maxX;
            MaxZ = maxZ;
        }
    }

    /// <summary>
    /// The map the simulation is currently running on.
    ///
    /// The geometry is no longer compiled in — it is loaded from arena.json by
    /// <see cref="ArenaMapData.Parse"/>, which both the server and the reference client
    /// run. The server loads the file it was started with; the client fetches the same
    /// file from the server at join and loads it with the same code. This type is just
    /// the one place they both point at afterwards, so the simulation can ask for
    /// "the walls" without carrying a map reference through every call.
    ///
    /// <see cref="Load"/> must be called before any simulation runs. Reading the
    /// geometry before then throws rather than returning an empty map, because an
    /// arena with no walls looks like it works right up until someone walks through one.
    /// </summary>
    public static class ArenaMap
    {
        /// <summary>
        /// Half the width of the player's collision square: the player collides as a
        /// 2.3 x 2.3 m axis-aligned square (see the collision rule for why a square).
        ///
        /// The number is the character model's BODY width, measured in the reference
        /// client — collide with what the players can see, or they clip into walls.
        /// The hands are deliberately excluded (arms out, the model spans 3.7 m):
        /// limbs may clip, a torso may not. Change this and the model together.
        ///
        /// This one stays in code: it describes the CHARACTER, not the map, and every
        /// map shares it. It is folded into the map hash all the same, because two
        /// sides that disagree about it disagree about where the walls are.
        /// </summary>
        public const float PlayerHalfExtent = 1.15f;

        private static ArenaMapData? _active;

        public static bool IsLoaded => _active != null;

        public static ArenaMapData Active =>
            _active ?? throw new InvalidOperationException(
                "No arena loaded. Call ArenaMap.Load(ArenaMapData.Parse(json)) before simulating — " +
                "the server does this at startup from --map, the client after fetching /api/config.");

        public static void Load(ArenaMapData map)
        {
            _active = map ?? throw new ArgumentNullException(nameof(map));
        }

        /// <summary>Fingerprint of the loaded geometry; compare against the server's to
        /// prove both sides are simulating the same arena.</summary>
        public static uint Hash => Active.Hash;

        public static string Name => Active.Name;

        public static float BoundsMinX => Active.BoundsMinX;
        public static float BoundsMaxX => Active.BoundsMaxX;
        public static float BoundsMinZ => Active.BoundsMinZ;
        public static float BoundsMaxZ => Active.BoundsMaxZ;

        /// <summary>World Y of the playable floor — CLIENT RENDERING ONLY.</summary>
        public static float GroundY => Active.GroundY;

        /// <summary>Walls in FIXED iteration order (published on the wiki).</summary>
        public static Aabb[] Walls => Active.Walls;

        public static SpawnPoint[] SpawnPoints => Active.SpawnPoints;

        public static PickupSpawn[] Pickups => Active.Pickups;
    }
}
