using System;
using System.Collections.Generic;
using KnockKnockArena.Shared.Config;
using KnockKnockArena.Shared.Protocol.Udp;
using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Server.Game;

/// <summary>
/// Per-pickup-type tuning loaded from pickups.json: how close a player has to be
/// to collect each type. Data, not constants, so a group can make health packs
/// generous and the rocket launcher demanding without rebuilding the server.
///
/// Server-only on purpose: the client never predicts pickups — the server rules
/// who took what and announces it (PickupTaken), so the client has no use for the
/// radius. A type the file leaves out keeps <see cref="GameConstants.PickupRadius"/>.
/// </summary>
public sealed class PickupTable
{
    /// <summary>Sanity cap. A radius larger than this would let players sweep
    /// pickups off the map from across a room — almost certainly a typo.</summary>
    private const float MaxRadiusMeters = 20f;

    private static readonly int TypeCount = Enum.GetValues(typeof(PickupType)).Length;

    private readonly float[] _radiusMeters;

    private PickupTable(float[] radiusMeters) => _radiusMeters = radiusMeters;

    /// <summary>The table the game runs on. Starts as the defaults so a server
    /// without pickups.json behaves exactly as before the file existed.</summary>
    public static PickupTable Current { get; private set; } = Defaults();

    public static void Load(PickupTable table) => Current = table;

    public static PickupTable Defaults()
    {
        float[] radius = new float[TypeCount];
        Array.Fill(radius, GameConstants.PickupRadius);
        return new PickupTable(radius);
    }

    public float RadiusFor(PickupType type) => _radiusMeters[(int)type];

    /// <summary>
    /// Parses <c>{ "pickups": [ { "type": "Health", "radiusMeters": 2.0 }, ... ] }</c>.
    /// Types are matched by NAME, so the order in the file does not matter and an
    /// entry cannot silently land on the wrong type. Throws on an unknown type, a
    /// duplicate, or a radius outside (0, 20] m.
    /// </summary>
    public static PickupTable Parse(string json)
    {
        PickupTable table = Defaults();
        HashSet<PickupType> seen = new HashSet<PickupType>();

        foreach (JsonValue entry in Json.Parse(json)["pickups"].Items)
        {
            string name = entry["type"].AsString();
            if (!Enum.TryParse(name, ignoreCase: false, out PickupType type) || !Enum.IsDefined(type))
                throw new JsonException($"unknown pickup type \"{name}\"; valid types are " +
                                        string.Join(", ", Enum.GetNames(typeof(PickupType))));
            if (!seen.Add(type))
                throw new JsonException($"pickup type \"{name}\" is listed twice");

            float radius = entry["radiusMeters"].AsFloat();
            if (!(radius > 0f && radius <= MaxRadiusMeters))
                throw new JsonException($"pickup \"{name}\": radiusMeters must be above 0 and at most {MaxRadiusMeters}");

            table._radiusMeters[(int)type] = radius;
        }
        return table;
    }

    /// <summary>One line for the startup log: every type with its radius.</summary>
    public string Describe()
    {
        List<string> parts = new List<string>();
        foreach (PickupType type in Enum.GetValues(typeof(PickupType)))
            parts.Add($"{type} {RadiusFor(type):0.##} m");
        return string.Join(", ", parts);
    }
}
