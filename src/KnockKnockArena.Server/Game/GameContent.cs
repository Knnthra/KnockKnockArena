using System;
using System.IO;
using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Server.Game;

/// <summary>
/// The data the server runs on, loaded from JSON at startup. Module 6: the arena, read
/// from a LOCAL file (--map, default data/arena.json) with the shared parser. The
/// client reads its own copy (Assets/Arena/arena.json) with the same parser — the two
/// sides agree only because the two files happen to be the same. Module 8: the weapon
/// table (data/weapons.json), with the same shared JSON code - the client has its own
/// copy too. Fetching both from the server comes in M10. Module 9: the pickup table
/// (data/pickups.json, the pickup radius per type) - server-only, the client never
/// predicts pickups.
/// </summary>
public sealed class GameContent
{
    public ArenaMapData Map { get; }
    public string MapPath { get; }
    public string WeaponsPath { get; }

    private GameContent(ArenaMapData map, string mapPath, string weaponsPath)
    {
        Map = map;
        MapPath = mapPath;
        WeaponsPath = weaponsPath;
    }

    /// <summary>
    /// Reads the arena and the weapon table, installs them, and logs what was loaded.
    /// Throws with a message naming the file and the problem — a server that starts
    /// on a half-read map is worse than one that refuses to start.
    /// </summary>
    public static GameContent Load(ServerConfig config)
    {
        string mapPath = config.MapPath;
        string mapJson = ReadFile(mapPath, "arena");
        string weaponsPath = config.WeaponsPath;
        string weaponsJson = ReadFile(weaponsPath, "weapon table");

        ArenaMapData map;
        try
        {
            map = ArenaMapData.Parse(mapJson);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{mapPath}: {ex.Message}", ex);
        }

        WeaponStats[] weapons;
        try
        {
            weapons = WeaponStats.ParseTable(weaponsJson);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{weaponsPath}: {ex.Message}", ex);
        }

        PickupTable pickups = LoadPickups(config.PickupsPath);

        ArenaMap.Load(map);
        WeaponStats.Load(weapons);
        PickupTable.Load(pickups);

        Log.Info($"Arena \"{map.Name}\" loaded from {mapPath}: " +
                 $"{map.Walls.Length} walls, {map.SpawnPoints.Length} spawn points, " +
                 $"hash {ArenaMapData.FormatHash(map.Hash)}");
        Log.Info($"Weapon table loaded from {weaponsPath}: {weapons.Length} weapons");
        Log.Info($"Pickup radii: {pickups.Describe()}");

        return new GameContent(map, mapPath, weaponsPath);
    }

    /// <summary>The pickup table is optional — an older data directory without it
    /// runs on the default radius. A file that IS there but broken still stops the
    /// server, for the same reason a broken map does.</summary>
    private static PickupTable LoadPickups(string path)
    {
        if (!File.Exists(path))
        {
            Log.Warn($"No pickup table at {path} — every pickup uses the default radius " +
                     $"of {GameConstants.PickupRadius} m");
            return PickupTable.Defaults();
        }
        try
        {
            return PickupTable.Parse(ReadFile(path, "pickup table"));
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"{path}: {ex.Message}", ex);
        }
    }

    private static string ReadFile(string path, string what)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"could not read the {what} at {path}: {ex.Message}", ex);
        }
    }
}
