using System.Globalization;
using KnockKnockArena.Server;
using KnockKnockArena.Server.Auth;
using KnockKnockArena.Server.Net;
using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Server.Game;


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
Log.Info($"KnockKnock Arena server v{TcpGameServer.ServerVersion} (protocol v{ProtocolConstants.Version})");
Log.Info($"name=\"{config.ServerName}\" port={config.Port} maxPlayers={config.MaxPlayers} " +
         $"verbose={(config.Verbose ? "on" : "off")}");

SessionManager sessions = new(config.MaxPlayers);
GameWorld world = new();
TcpGameServer server = new(config, sessions);
UdpGameServer udpServer = new(config, sessions, world);
server.PlayerDisconnected += session =>
{
    if (world.TryDespawn(session.PlayerId))
        Log.Info($"Despawned {session.Username}#{session.PlayerId} from game world");
};
udpServer.KickPlayer = server.KickByPlayerId;
server.Start();
udpServer.Start();

using ManualResetEventSlim shutdown = new();

Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Set(); };
Log.Info("Commands: status | quit");
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
                     $"inGame={world.PlayerCount} tick={world.ServerTick}");
            break;
        case "quit":
            shutdown.Set();
            break;
    }
}

shutdown.Wait();
Log.Info("Shutting down.");
return 0;
