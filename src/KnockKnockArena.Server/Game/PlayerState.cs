using System.Net;
using KnockKnockArena.Server.Auth;

namespace KnockKnockArena.Server.Game
{
    public sealed class PlayerState
    {
        public PlayerSession Session{get;}
        
        public IPEndPoint EndPoint;
        
        public DateTime LastUdpPacketUtc = DateTime.UtcNow;
        
        public bool TimedOut;
        
        public PlayerState(PlayerSession session, IPEndPoint endPoint)
        {
            Session = session;
            EndPoint = endPoint;
        }
        
    }
}
