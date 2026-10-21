using System;

namespace KnockKnockArena.Server;

/// <summary>
/// Server configuration, from command-line arguments:
///
///   args: --port N, --max-players N, --name TEXT, --map PATH, --verbose
/// </summary>
public sealed class ServerConfig
{
    /// <summary>Used for BOTH the TCP listener and the UDP socket (same number, two protocols).</summary>
    public int Port { get; private set; } = 36363;

    public int MaxPlayers { get; private set; } = 12;

    /// <summary>What LAN discovery shows for this instance. Defaults to the machine
    /// name, so two servers on one LAN tell apart.</summary>
    public string ServerName { get; private set; } = Environment.MachineName;

    /// <summary>The arena file (M06): a LOCAL file, read at startup. The client has its
    /// own copy in the Unity project — the two only agree because they are the same file.</summary>
    public string MapPath { get; private set; } = "data/arena.json";

    public bool Verbose { get; private set; }

    public static ServerConfig Load(string[] args)
    {
        ServerConfig config = new();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out int port):
                    config.Port = port;
                    i++;
                    break;
                case "--max-players" when i + 1 < args.Length && int.TryParse(args[i + 1], out int max):
                    config.MaxPlayers = max;
                    i++;
                    break;
                case "--name" when i + 1 < args.Length:
                    config.ServerName = args[i + 1];
                    i++;
                    break;
                case "--map" when i + 1 < args.Length:
                    config.MapPath = args[i + 1];
                    i++;
                    break;
                case "--verbose":
                    config.Verbose = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'. " +
                        "Usage: --port N --max-players N --name TEXT --map PATH --verbose");
            }
        }

        if (config.Port is < 1024 or > 65535)
            throw new ArgumentException($"Port {config.Port} is out of range (1024-65535).");
        if (config.MaxPlayers < 1)
            throw new ArgumentException($"Max players {config.MaxPlayers} is out of range (at least 1).");

        // Player ids double as snapshot entity ids, and pickups start at entity id
        // 100. A player id of 100+ would collide with a pickup, so the id ranges are
        // absolute: players 1-99, pickups 100-999, rockets 1000+.
        int maxPlayerId = KnockKnockArena.Shared.Simulation.GameConstants.PickupEntityIdBase - 1;
        if (config.MaxPlayers > maxPlayerId)
        {
            Log.Warn($"Max players {config.MaxPlayers} clamped to {maxPlayerId}: player ids must stay " +
                     $"below the pickup entity id range ({maxPlayerId + 1}+).");
            config.MaxPlayers = maxPlayerId;
        }

        return config;
    }
}
