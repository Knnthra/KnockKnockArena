using System;

namespace KnockKnockArena.Server;

/// <summary>
/// Server configuration, from command-line arguments:
///
///   args: --port N, --http-port N, --max-players N, --name TEXT, --map PATH,
///         --weapons PATH, --pickups PATH, --data-dir PATH, --verbose, --dev-accounts
/// </summary>
public sealed class ServerConfig
{
    /// <summary>Used for BOTH the TCP listener and the UDP socket (same number, two protocols).</summary>
    public int Port { get; private set; } = 36363;

    /// <summary>The REST API (M10), on its own port: status and config over HTTP.</summary>
    public int HttpPort { get; private set; } = 8080;

    public int MaxPlayers { get; private set; } = 12;

    /// <summary>What LAN discovery shows for this instance. Defaults to the machine
    /// name, so two servers on one LAN tell apart.</summary>
    public string ServerName { get; private set; } = Environment.MachineName;

    /// <summary>The arena file (M06): a LOCAL file, read at startup. The client has its
    /// own copy in the Unity project — the two only agree because they are the same file.</summary>
    public string MapPath { get; private set; } = "data/arena.json";

    /// <summary>The weapon table (M08): damage, cooldown, spread - balance as data.</summary>
    public string WeaponsPath { get; private set; } = "data/weapons.json";

    /// <summary>The pickup table (M09): the pickup radius per type. Optional - without
    /// it every pickup keeps the default radius.</summary>
    public string PickupsPath { get; private set; } = "data/pickups.json";

    /// <summary>Where the accounts live (M11): DataDir/accounts.json - password hashes
    /// and salts, never the passwords. Created on first start; never commit it.</summary>
    public string DataDir { get; private set; } = "data";

    public bool Verbose { get; private set; }

    /// <summary>Seed a NEW accounts.json with alice, bob and test/test (M11) - a local
    /// development server only: on a shared server everyone could log in as test.</summary>
    public bool DevAccounts { get; private set; }

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
                case "--http-port" when i + 1 < args.Length && int.TryParse(args[i + 1], out int httpPort):
                    config.HttpPort = httpPort;
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
                case "--weapons" when i + 1 < args.Length:
                    config.WeaponsPath = args[i + 1];
                    i++;
                    break;
                case "--pickups" when i + 1 < args.Length:
                    config.PickupsPath = args[i + 1];
                    i++;
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    config.DataDir = args[i + 1];
                    i++;
                    break;
                case "--verbose":
                    config.Verbose = true;
                    break;
                case "--dev-accounts":
                    config.DevAccounts = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'. " +
                        "Usage: --port N --http-port N --max-players N --name TEXT --map PATH --weapons PATH --pickups PATH --data-dir PATH --verbose --dev-accounts");
            }
        }

        if (config.Port is < 1024 or > 65535)
            throw new ArgumentException($"Port {config.Port} is out of range (1024-65535).");
        if (config.HttpPort is < 1024 or > 65535 || config.HttpPort == config.Port)
            throw new ArgumentException($"HTTP port {config.HttpPort} is out of range or collides with the game port.");
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
