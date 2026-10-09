using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Server
{
    public sealed class ServerConfig
    {
        public int Port { get; private set; } = 36363;
        public int MaxPlayers { get; private set; } = 12;
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
                    case "--verbose":
                        config.Verbose = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument '{args[i]}'. " +
                            "Usage: --port N --max-players N --verbose");
                }
            }

            int maxPlayerId = GameConstants.PickupEntityIdBase - 1;
            if (config.MaxPlayers > maxPlayerId)
            {
                Log.Warn($"Max players {config.MaxPlayers} clamped to {maxPlayerId}");
                config.MaxPlayers = maxPlayerId;
            }
            
            return config;
        }
    }
}