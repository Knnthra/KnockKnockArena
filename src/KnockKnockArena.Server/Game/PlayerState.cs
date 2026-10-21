using System.Net;
using KnockKnockArena.Server.Auth;
using System.Collections.Concurrent;
using KnockKnockArena.Shared.Protocol.Udp;
using KnockKnockArena.Shared.Simulation;
namespace KnockKnockArena.Server.Game
{
    public sealed class PlayerState
    {
        public PlayerSession Session{get;}
        
        public IPEndPoint EndPoint;
        
        public float X;
        public float Z;
        public float AimX;
        public float AimZ;
        
        public MovementSimulation.DashState Dash;
        
        public uint LastProcessedInputIndex;
        
        
        public DateTime LastUdpPacketUtc = DateTime.UtcNow;
        
        public bool TimedOut;
        
        public readonly ConcurrentQueue<InputPacket> PendingInputs = new();
        
        public PlayerState(PlayerSession session, IPEndPoint endPoint)
        {
            Session = session;
            EndPoint = endPoint;
        }
        
    }
}
