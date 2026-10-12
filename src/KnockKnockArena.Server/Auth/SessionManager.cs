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
        private readonly object _gate = new();
        private readonly Dictionary<byte, PlayerSession> _sessionsById = new();
        
        private readonly int _maxPlayers;
        
        public int Count
        {
            get{lock(_gate) return _sessionsById.Count;}
        }
        
        public SessionManager(int maxPlayers)
        {
            _maxPlayers = maxPlayers;
        }
        
        public bool TryCreate(string username, out PlayerSession? session, out string error)
        {
            lock(_gate)
            {
                if(_sessionsById.Values.Any(s => s.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
                {
                    session = null;
                    error = "already logged in";
                    return false;
                }
                for (byte id = 1; id < _maxPlayers; id++)
                {
                    if(!_sessionsById.ContainsKey(id))
                    {
                        session = new PlayerSession(id, username, Guid.NewGuid().ToByteArray());
                        _sessionsById[id] = session;
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
            lock(_gate)
                _sessionsById.Remove(playerId);
        }
        
        public List<PlayerSession> GetAll()
        {
            lock(_gate)
                return new List<PlayerSession>(_sessionsById.Values);
        }
        
        public bool TryGetByToken(byte[] token, out PlayerSession? session)
        {
            lock(_gate)
            {
                foreach (PlayerSession candidate in _sessionsById.Values)
                {
                    if(candidate.Token.AsSpan().SequenceEqual(token))
                    {
                        session = candidate;
                        return true;
                    }
                }
            }
            
            session = null;
            return false;
        }
    }
    
}