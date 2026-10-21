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
/// Module 6: position, aim and dash. Weapons come in M08, health in M09.
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

    public PlayerState(PlayerSession session, IPEndPoint endPoint)
    {
        Session = session;
        EndPoint = endPoint;
    }
}
