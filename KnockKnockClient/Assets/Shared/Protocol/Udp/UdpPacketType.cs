using System;

namespace KnockKnockArena.Shared.Protocol.Udp
{
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