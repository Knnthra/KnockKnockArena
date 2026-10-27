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
/// copy too. Fetching both from the server comes in M10; the pickup table in M09.
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

        ArenaMap.Load(map);
        WeaponStats.Load(weapons);

        Log.Info($"Arena \"{map.Name}\" loaded from {mapPath}: " +
                 $"{map.Walls.Length} walls, {map.SpawnPoints.Length} spawn points, " +
                 $"hash {ArenaMapData.FormatHash(map.Hash)}");
        Log.Info($"Weapon table loaded from {weaponsPath}: {weapons.Length} weapons");

        return new GameContent(map, mapPath, weaponsPath);
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
