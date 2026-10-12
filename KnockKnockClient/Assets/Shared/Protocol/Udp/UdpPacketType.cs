using System;

namespace KnockKnockArena.Shared.Protocol.Udp
{
    public enum UdpPacketType : byte
    {
        Join = 0,
        Heartbeat = 2,
        DiscoveryRequest = 3,
        
        DiscoveryResponse = 102,
    }
    
    [Flags]
    public enum MovementBits : byte
    {
        None = 0,
        W = 1 << 0,
        A = 1 << 1,
        S = 1 << 2,
        D = 1 << 3,
        Walk = 1 << 4,
        Sprint = 1 << 5,
        Dash = 1 << 6,
    }

    [Flags]
    public enum ButtonBits : byte
    {
        None = 0,
        Fire = 1 << 0,
        SwitchWeapon = 1 << 1,
        Respawn = 1 << 2,
    }
}