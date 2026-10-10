using System.Globalization;
using KnockKnockArena.Server;
using KnockKnockArena.Server.Auth;
using KnockKnockArena.Server.Net;
using KnockKnockArena.Shared.Protocol;

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
Log.Info($"port={config.Port} maxPlayers={config.MaxPlayers} verbose={(config.Verbose ? "on" : "off")}");

SessionManager sessions = new(config.MaxPlayers);
TcpGameServer server = new(config, sessions);
server.Start();

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
         $"players={server.PlayersOnline}/{config.MaxPlayers}");
            break;
        case "quit":
            shutdown.Set();
            break;
    }
}

shutdown.Wait();
Log.Info("Shutting down.");
return 0;
