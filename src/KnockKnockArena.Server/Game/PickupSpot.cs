using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Server.Game;

/// <summary>Server-side state for one pickup spot (position/type come from ArenaMap).</summary>
public sealed class PickupSpot
{
    public byte PickupId;
    public bool Active = true;
    public uint RespawnAtTick;
}

/// <summary>A pickup taken this tick; broadcast on TCP as PickupTaken.</summary>
public readonly record struct PickupTakenEvent(
    byte PlayerId,
    string PlayerName,
    byte PickupId,
    PickupType Type);
