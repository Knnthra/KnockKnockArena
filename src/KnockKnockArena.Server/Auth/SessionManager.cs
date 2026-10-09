using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Server.Auth
{
    public sealed class PlayerSession
    {
        public byte PlayerId {get;}
        public string Username {get;}
        public byte[] Token {get;}
        
        public PlayerSession(byte playerId, string username, byte[] token)
        {
            this.PlayerId = playerId;
            this.Username = username;
            this.Token = token;
        }
        
    }
    
    public sealed class SessionManager
    {
        private readonly object gate = new();
        private readonly Dictionary<byte, PlayerSession> sessionsById = new();
        
        private readonly int maxPlayers;
        
        public int Count
        {
            get{lock(gate) return sessionsById.Count;}
        }
        
        public SessionManager(int maxPlayers)
        {
            this.maxPlayers = maxPlayers;
        }
        
        public bool TryCreate(string username, out PlayerSession? session, out string error)
        {
            lock(gate)
            {
                if(sessionsById.Values.Any(s => s.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
                {
                    session = null;
                    error = "already logged in";
                    return false;
                }
                for (byte id = 1; id < maxPlayers; id++)
                {
                    if(!sessionsById.ContainsKey(id))
                    {
                        session = new PlayerSession(id, username, Guid.NewGuid().ToByteArray());
                        sessionsById[id] = session;
                        error ="";
                        return true;
                    }
                }
                session = null;
                error = "server full";
                return false;
            }
        }
        
        public void Remove(byte playerId)
        {
            lock(gate)
                sessionsById.Remove(playerId);
        }
        
        public List<PlayerSession> GetAll()
        {
            lock(gate)
                return new List<PlayerSession>(sessionsById.Values);
        }
    }
    
}