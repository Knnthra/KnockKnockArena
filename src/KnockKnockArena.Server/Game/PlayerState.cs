using System;
using System.Collections.Generic;
using System.Net;
using KnockKnockArena.Server.Auth;
using KnockKnockArena.Shared.Protocol.Udp;
using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Server.Game;

/// <summary>
/// Server-side state for one player that has joined the game world via UDP.
/// Inputs are enqueued by the UDP receive thread and consumed by the tick thread.
/// Module 9: position, aim, dash, weapons, ammo, spread, the position history lag
/// compensation rewinds into - and health, frags/deaths and the life state.
/// </summary>
public sealed class PlayerState
{
    public PlayerSession Session { get; }

    /// <summary>The UDP endpoint snapshots are sent to. Updated if the client rejoins.</summary>
    public IPEndPoint EndPoint;

    public float X;
    public float Z;
    public float AimX;
    public float AimZ;
    public byte Health = 100;
    public WeaponId ActiveWeapon = WeaponId.Glock;
    public byte OwnedWeapons = (1 << (int)WeaponId.Punch) | (1 << (int)WeaponId.Glock);
    public ushort Frags;
    public ushort Deaths;
    public PlayerLifeState LifeState = PlayerLifeState.Alive;

    /// <summary>Dash simulation state (shared rule) — carried in every snapshot so
    /// the owning client can seed prediction replay from it.</summary>
    public MovementSimulation.DashState Dash;

    /// <summary>Newest input index applied by the tick loop; echoed in this player's snapshots.</summary>
    public uint LastProcessedInputIndex;

    /// <summary>Time of the last UDP packet from this player (input or heartbeat); drives the timeout.</summary>
    public DateTime LastUdpPacketUtc = DateTime.UtcNow;

    /// <summary>Reported as timed out; the kick is under way. Set once, so a silent
    /// player is not kicked again every tick until the disconnect lands.</summary>
    public bool TimedOut;

    /// <summary>Inputs that have arrived but are not applied yet, sorted by InputIndex -
    /// jitter can deliver them out of order. Filled by the UDP receive thread, emptied by
    /// the tick thread; both lock this list.</summary>
    public readonly SortedList<uint, InputPacket> PendingInputs = new();

    /// <summary>Inputs the tick loop may still apply: +1 every tick (up to the world's
    /// MaxInputBudget), -1 per applied input.</summary>
    public int InputBudget;

    /// <summary>Ticks spent waiting for a missing input index (a gap in PendingInputs).</summary>
    public int GapWaitTicks;

    /// <summary>A dropped input is warned about at most once a second (UdpGameServer);
    /// the drops in between are counted here.</summary>
    public DateTime LastInputRejectWarningUtc;
    public int InputRejectsSinceWarning;

    /// <summary>Ammo per weapon id. Melee slots stay 0 (never consumed).</summary>
    public readonly ushort[] Ammo = new ushort[WeaponStats.WeaponCount];

    /// <summary>Accumulated spread bloom in degrees on top of the active weapon's base spread.</summary>
    public float SpreadBloom;

    /// <summary>The first server tick the player may fire again (cooldown gate).</summary>
    public uint NextFireAllowedTick;

    public uint DeathTick;
    public float DeathX;
    public float DeathZ;

    // Position history for lag compensation: a ring buffer with one slot per tick,
    // covering the last RewindBufferTicks ticks (1 second).
    private readonly uint[] _historyTick = new uint[GameConstants.RewindBufferTicks];
    private readonly float[] _historyX = new float[GameConstants.RewindBufferTicks];
    private readonly float[] _historyZ = new float[GameConstants.RewindBufferTicks];

    public PlayerState(PlayerSession session, IPEndPoint endPoint)
    {
        Session = session;
        EndPoint = endPoint;
        Ammo[(int)WeaponId.Glock] = GameConstants.GlockStartAmmo;
    }

    /// <summary>Called once per tick after this player's movement is resolved.</summary>
    public void RecordHistory(uint tick)
    {
        int slot = (int)(tick % GameConstants.RewindBufferTicks);
        _historyTick[slot] = tick;
        _historyX[slot] = X;
        _historyZ[slot] = Z;
    }

    /// <summary>
    /// Position at a past tick, for lag compensation. Falls back to the current
    /// position when the tick is outside the buffer (too old, or not recorded yet
    /// right after spawn) — a shot is then judged against the present, which is the
    /// safe default.
    /// </summary>
    public (float X, float Z) GetPositionAt(uint tick)
    {
        int slot = (int)(tick % GameConstants.RewindBufferTicks);
        if (_historyTick[slot] == tick)
            return (_historyX[slot], _historyZ[slot]);
        return (X, Z);
    }
}
