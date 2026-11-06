using System;
using System.Globalization;
using System.Threading;
using KnockKnockArena.Server;
using KnockKnockArena.Server.Auth;
using KnockKnockArena.Server.Game;
using KnockKnockArena.Server.Net;

// The log writes numbers the same way on every machine, in Docker too: 0.17, never 0,17.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

ServerConfig config;
try
{
    config = ServerConfig.Load(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

Log.VerboseEnabled = config.Verbose;

Log.Info($"KnockKnock Arena server v{TcpGameServer.ServerVersion} " +
         $"(protocol v{KnockKnockArena.Shared.Protocol.ProtocolConstants.Version})");
Log.Info($"name=\"{config.ServerName}\" port={config.Port} httpPort={config.HttpPort} maxPlayers={config.MaxPlayers} " +
         $"dataDir={config.DataDir} " +
         $"verbose={(config.Verbose ? "on" : "off")}");

// The arena is DATA, loaded before anything can simulate. The loader lives in the
// shared code, so the client that reads its own copy of the file turns it into the
// same floats and can predict against them.
GameContent content;
try
{
    content = GameContent.Load(config);
}
catch (Exception ex)
{
    Log.Error($"Could not load the game content: {ex.Message}");
    return 1;
}

SessionManager sessions = new(config.MaxPlayers);
// Accounts (M11): DataDir/accounts.json holds the PBKDF2 hashes and salts.
AccountStore accounts = new(config.DataDir, config.DevAccounts);
GameWorld world = new();

TcpGameServer server = new(config, sessions, accounts);
UdpGameServer udpServer = new(config, sessions, world);

// TCP disconnect (or kick) -> remove the player from the game world, and put the
// session's score on the account (M11).
server.PlayerDisconnected += session =>
{
    if (world.TryDespawn(session.PlayerId, out ushort frags, out ushort deaths))
    {
        Log.Info($"Despawned {session.Username}#{session.PlayerId} from game world");
        accounts.AccumulateStats(session.Username, frags, deaths);
    }
};
// UDP silence -> full disconnect (S01: timeout gives a TCP event + despawn).
udpServer.KickPlayer = server.KickByPlayerId;
// Events from the tick loop -> TCP: they must arrive, so they go on the reliable channel.
udpServer.PlayerKilled = kill =>
    server.BroadcastPlayerDied(kill.KillerId, kill.KillerName, kill.VictimId, kill.VictimName, (byte)kill.Weapon);
udpServer.PickupTaken = pickup =>
    server.BroadcastPickupTaken(pickup.PlayerId, pickup.PlayerName, pickup.PickupId, (byte)pickup.Type);
udpServer.RocketExploded = explosion =>
    server.BroadcastRocketExploded(explosion.RocketEntityId, explosion.X, explosion.Z, explosion.OwnerId);
udpServer.PlayerDamaged = hit =>
    server.BroadcastPlayerDamaged(hit.VictimId, hit.X, hit.Z, hit.AttackerId, (byte)hit.Weapon, hit.Damage);

server.Start();
udpServer.Start();
// The REST API (M10): what the server browser asks (status), and what a client
// fetches before it joins (config).
RestApi.Start(config, accounts, content, () => new
{
    serverName = config.ServerName,
    version = TcpGameServer.ServerVersion,
    protocolVersion = KnockKnockArena.Shared.Protocol.ProtocolConstants.Version,
    uptimeSeconds = (long)server.Uptime.TotalSeconds,
    playersOnline = server.PlayersOnline,
    playersInGame = world.PlayerCount,
    maxPlayers = config.MaxPlayers,
    gamePort = config.Port,
    httpPort = config.HttpPort,
    tickRate = KnockKnockArena.Shared.Simulation.GameConstants.TickRate,
    mapName = content.Map.Name,
    mapHash = KnockKnockArena.Shared.Simulation.ArenaMapData.FormatHash(content.Map.Hash),
});

using ManualResetEventSlim shutdown = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Set();
};

// Console commands for local debugging.
Log.Info("Commands: status | rewind on | rewind off | quit");
while (!shutdown.IsSet)
{
    string? line = Console.ReadLine();
    if (line == null)
        break;

    switch (line.Trim().ToLowerInvariant())
    {
        case "status":
            Log.Info($"version={TcpGameServer.ServerVersion} " +
                     $"uptime={server.Uptime:hh\\:mm\\:ss} " +
                     $"players={server.PlayersOnline}/{config.MaxPlayers} " +
                     $"inGame={world.PlayerCount} tick={world.ServerTick} " +
                     $"rewind={(world.RewindEnabled ? "on" : "off")}");
            break;
        // Lag compensation on and off in one take, no restart (M08).
        case "rewind on":
            world.RewindEnabled = true;
            Log.Info("Rewind is on: shots are judged where the targets were at the tick the shooter saw");
            break;
        case "rewind off":
            world.RewindEnabled = false;
            Log.Info("Rewind is off: shots are judged where the targets are now");
            break;
        case "quit":
            shutdown.Set();
            break;
        case "":
            break;
        default:
            Log.Info("Unknown command. Commands: status | rewind on | rewind off | quit");
            break;
    }
}

shutdown.Wait();
Log.Info("Shutting down.");
return 0;
