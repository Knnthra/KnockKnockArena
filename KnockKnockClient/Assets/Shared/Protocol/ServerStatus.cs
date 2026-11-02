using System.Collections.Generic;
using KnockKnockArena.Shared.Config;

namespace KnockKnockArena.Shared.Protocol
{
    /// <summary>
    /// One server instance as GET /api/status describes it — what the server browser
    /// (M10) lists. REST, not the game protocol: servers beyond the LAN cannot be
    /// found by broadcast, so the browser asks a known list of host:httpPort instead.
    /// </summary>
    public sealed class ServerStatus
    {
        public string ServerName = "";
        public int GamePort;
        public int HttpPort;
        public int ProtocolVersion;
        public int PlayersOnline;
        public int MaxPlayers;
        public string MapName = "";

        public bool Compatible => ProtocolVersion == ProtocolConstants.Version;

        /// <summary>Parses the JSON body of GET /api/status with the shared parser.</summary>
        public static ServerStatus Parse(string json)
        {
            JsonValue root = Json.Parse(json);
            return new ServerStatus
            {
                ServerName = root["serverName"].AsString(),
                GamePort = root["gamePort"].AsInt(),
                HttpPort = root["httpPort"].AsInt(),
                ProtocolVersion = root["protocolVersion"].AsInt(),
                PlayersOnline = root["playersOnline"].AsInt(),
                MaxPlayers = root["maxPlayers"].AsInt(),
                MapName = root["mapName"].AsString(),
            };
        }

        /// <summary>
        /// The simplest possible matchmaking (M10): among the instances that answered,
        /// pick one that speaks our protocol and has room, preferring the fullest —
        /// players join where the game is. Returns the index into
        /// <paramref name="statuses"/> (null entries = no answer), or -1 when none fits.
        /// </summary>
        public static int PickQuickJoin(IReadOnlyList<ServerStatus?> statuses)
        {
            int best = -1;
            for (int i = 0; i < statuses.Count; i++)
            {
                ServerStatus? candidate = statuses[i];
                if (candidate == null || !candidate.Compatible || candidate.PlayersOnline >= candidate.MaxPlayers)
                    continue;
                if (best < 0 || candidate.PlayersOnline > statuses[best]!.PlayersOnline)
                    best = i;
            }
            return best;
        }

        /// <summary>
        /// Splits one "host:httpPort" entry from the browser's list. The port defaults
        /// to 8080 when left out.
        /// </summary>
        public static bool TryParseEntry(string entry, out string host, out int httpPort)
        {
            host = "";
            httpPort = 8080;
            string trimmed = (entry ?? "").Trim();
            if (trimmed.Length == 0)
                return false;
            int colon = trimmed.LastIndexOf(':');
            if (colon < 0)
            {
                host = trimmed;
                return true;
            }
            host = trimmed.Substring(0, colon);
            return host.Length > 0 && int.TryParse(trimmed.Substring(colon + 1), out httpPort) &&
                   httpPort > 0 && httpPort <= 65535;
        }
    }
}
