using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using KnockKnockArena.Server.Auth;
using KnockKnockArena.Shared.Protocol.Udp;
using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Server.Game;

/// <summary>
/// The authoritative game state: every player in the world, advanced by the tick
/// loop. All mutation happens under one lock; the UDP receive thread only does
/// lock-free lookups (concurrent dictionary) and enqueues into per-player queues.
///
/// Module 7: movement, pickups that are taken and come back, and a snapshot with every
/// player and pickup each tick. What a pickup GIVES (health, ammo, weapons) comes in
/// M09 with damage and death, as do rockets.
/// </summary>
public sealed class GameWorld
{
    /// <summary>One input is one tick's step, so over time the tick loop applies at most
    /// one input per tick: every tick adds 1 to the player's input budget, every applied
    /// input costs 1. A tick that finds no input keeps its place, so an input that
    /// arrives late is caught up on a later tick - but no more than this many places are
    /// kept. However many inputs a client sends, it never gets more steps than ticks have
    /// passed plus this reserve: 60 inputs a second move it as fast as 30.</summary>
    private const int MaxInputBudget = 4;

    /// <summary>Inputs that may wait per player: room for jitter, not for a flood. An
    /// input that does not fit is dropped ("input queue full").</summary>
    private const int MaxQueuedInputs = 8;

    /// <summary>Ticks to wait for a missing input index - overtaken by a later input in
    /// the network - before the tick loop skips it.</summary>
    private const int MaxGapWaitTicks = 3;

    private readonly object _gate = new();
    private readonly Dictionary<byte, PlayerState> _playersById = new();
    private readonly ConcurrentDictionary<IPEndPoint, PlayerState> _playersByEndPoint = new();

    /// <summary>Scratch list for timed-out players whose kick never despawned them.</summary>
    private readonly List<PlayerState> _orphaned = new();

    /// <summary>Scratch list: one player's inputs for the current tick (TakeInputs).</summary>
    private readonly List<InputPacket> _inputsThisTick = new();
    private readonly PickupSpot[] _pickups;

    public GameWorld()
    {
        _pickups = new PickupSpot[ArenaMap.Pickups.Length];
        for (int i = 0; i < _pickups.Length; i++)
            _pickups[i] = new PickupSpot { PickupId = (byte)i };
    }

    public uint ServerTick { get; private set; }
    public uint Sequence { get; private set; }

    public int PlayerCount
    {
        get { lock (_gate) return _playersById.Count; }
    }

    /// <summary>Lock-free lookup used by the UDP receive thread.</summary>
    public bool TryGetByEndPoint(IPEndPoint endPoint, out PlayerState player) =>
        _playersByEndPoint.TryGetValue(endPoint, out player!);

    /// <summary>
    /// Handles a UDP join: spawns the player, or rebinds the endpoint if the player
    /// is already in the world (client restart, NAT rebind — the token proves identity).
    /// Returns true if this was a new spawn.
    /// </summary>
    public bool SpawnOrRebind(PlayerSession session, IPEndPoint endPoint, out PlayerState player)
    {
        lock (_gate)
        {
            if (_playersById.TryGetValue(session.PlayerId, out PlayerState? existing))
            {
                _playersByEndPoint.TryRemove(existing.EndPoint, out _);
                existing.EndPoint = endPoint;
                existing.LastUdpPacketUtc = DateTime.UtcNow;
                existing.TimedOut = false; // back on UDP: the timeout no longer applies
                _playersByEndPoint[endPoint] = existing;
                player = existing;
                return false;
            }

            // Each player id has its own spawn point from the map.
            (float spawnX, float spawnZ) = ArenaMap.SpawnPoints[(session.PlayerId - 1) % ArenaMap.SpawnPoints.Length];
            player = new PlayerState(session, endPoint)
            {
                X = spawnX,
                Z = spawnZ,
            };
            _playersById[session.PlayerId] = player;
            _playersByEndPoint[endPoint] = player;
            return true;
        }
    }

    /// <summary>Removes a player from the world.</summary>
    public bool TryDespawn(byte playerId)
    {
        lock (_gate)
        {
            if (!_playersById.Remove(playerId, out PlayerState? player))
                return false;
            _playersByEndPoint.TryRemove(player.EndPoint, out _);
            return true;
        }
    }

    private static string Name(PlayerState player) =>
        $"{player.Session.Username}#{player.Session.PlayerId}";

    /// <summary>
    /// Pickups: a taken spot comes back after its respawn time; an active one is taken
    /// by the first player whose centre is within the pickup radius. Module 7: taking
    /// only - what the pickup gives the player comes in M09.
    /// </summary>
    private void UpdatePickups(List<PickupTakenEvent> pickupsTaken)
    {
        for (int i = 0; i < _pickups.Length; i++)
        {
            PickupSpot spot = _pickups[i];
            if (!spot.Active)
            {
                if (ServerTick >= spot.RespawnAtTick)
                    spot.Active = true;
                continue;
            }

            (PickupType type, float pickupX, float pickupZ) = ArenaMap.Pickups[i];
            foreach (PlayerState player in _playersById.Values)
            {
                float dx = player.X - pickupX;
                float dz = player.Z - pickupZ;
                if (dx * dx + dz * dz > GameConstants.PickupRadius * GameConstants.PickupRadius)
                    continue;

                spot.Active = false;
                spot.RespawnAtTick = ServerTick + (uint)(RespawnSecondsFor(type) * GameConstants.TickRate);
                pickupsTaken.Add(new PickupTakenEvent(player.Session.PlayerId, player.Session.Username, (byte)i, type));
                Log.Info($"Pickup: {Name(player)} took {type} (pickup {i})");
                break; // one winner per pickup per tick
            }
        }
    }

    private static float RespawnSecondsFor(PickupType type) => type switch
    {
        PickupType.Ak47Weapon or PickupType.RocketLauncherWeapon => GameConstants.WeaponPickupRespawnSeconds,
        PickupType.Health => GameConstants.HealthPickupRespawnSeconds,
        _ => GameConstants.AmmoPickupRespawnSeconds,
    };

    /// <summary>Called by the receive thread. False when the input is dropped; the
    /// reason says why.</summary>
    public bool TryEnqueueInput(PlayerState player, InputPacket input, out string reason)
    {
        lock (player.PendingInputs)
        {
            // Already applied, or already waiting: a duplicate, or an input that
            // arrived after the tick loop gave up waiting for it.
            if (input.InputIndex <= player.LastProcessedInputIndex ||
                player.PendingInputs.ContainsKey(input.InputIndex))
            {
                reason = $"stale input {input.InputIndex}";
                return false;
            }
            if (player.PendingInputs.Count >= MaxQueuedInputs)
            {
                reason = "input queue full";
                return false;
            }
            player.PendingInputs.Add(input.InputIndex, input);
            reason = "";
            return true;
        }
    }

    /// <summary>
    /// The inputs the player gets to apply this tick, in index order: the budget rule
    /// (<see cref="MaxInputBudget"/>). Acknowledges them too - LastProcessedInputIndex
    /// moves here, under the same lock the receive thread checks it under.
    /// </summary>
    private static void TakeInputs(PlayerState player, List<InputPacket> taken)
    {
        taken.Clear();
        lock (player.PendingInputs)
        {
            player.InputBudget = Math.Min(player.InputBudget + 1, MaxInputBudget);
            while (player.InputBudget > 0 && player.PendingInputs.Count > 0)
            {
                InputPacket next = player.PendingInputs.Values[0];

                // A gap before the lowest index: the missing input may have been
                // overtaken in the network and still be on its way. Wait a few ticks
                // for it - unless the queue is full, then it could not get in anyway.
                if (next.InputIndex != player.LastProcessedInputIndex + 1 &&
                    player.GapWaitTicks < MaxGapWaitTicks &&
                    player.PendingInputs.Count < MaxQueuedInputs)
                {
                    player.GapWaitTicks++;
                    break;
                }

                player.GapWaitTicks = 0;
                player.PendingInputs.RemoveAt(0);
                player.InputBudget--;
                player.LastProcessedInputIndex = next.InputIndex;
                taken.Add(next);
            }
        }
    }

    /// <summary>
    /// Advances the world one tick and builds the outgoing snapshots.
    /// Snapshots are personalized: LastProcessedInputIndex belongs to the recipient.
    /// Players whose UDP has been silent too long are returned in timedOut; pickups
    /// taken this tick in pickupsTaken (broadcast on TCP by the caller).
    /// </summary>
    public void Tick(List<(IPEndPoint EndPoint, byte[] Datagram)> snapshotSends, List<PlayerState> timedOut,
        List<PickupTakenEvent> pickupsTaken)
    {
        lock (_gate)
        {
            ServerTick++;
            Sequence++;

            DateTime now = DateTime.UtcNow;

            foreach (PlayerState player in _playersById.Values)
            {
                // TakeInputs hands out this tick's inputs by the budget rule, in index
                // order, and acknowledges them.
                TakeInputs(player, _inputsThisTick);
                foreach (InputPacket input in _inputsThisTick)
                {
                    // Each input is worth exactly one fixed time step — the same rule
                    // the client uses for prediction.
                    float x = player.X;
                    float z = player.Z;
                    MovementSimulation.DashState dash = player.Dash;
                    MovementSimulation.Step(ref x, ref z, ref dash, input.Movement,
                        input.AimX, input.AimZ, GameConstants.TickDeltaTime);
                    player.X = x;
                    player.Z = z;
                    player.Dash = dash;
                    player.AimX = input.AimX;
                    player.AimZ = input.AimZ;
                }

                // A timed-out player is reported ONCE and left in the world: the caller
                // kicks the TCP connection, and the ordinary disconnect path despawns
                // the player.
                double silentSeconds = (now - player.LastUdpPacketUtc).TotalSeconds;
                if (!player.TimedOut && silentSeconds > GameConstants.UdpTimeoutSeconds)
                {
                    player.TimedOut = true;
                    timedOut.Add(player);
                }
                else if (player.TimedOut && silentSeconds > GameConstants.UdpTimeoutSeconds * 3)
                {
                    _orphaned.Add(player);
                }
            }

            // Safety net: the kick should have despawned them within milliseconds. If
            // the TCP side never came back (no connection left to close), remove them
            // so no ghost stays in the world.
            foreach (PlayerState player in _orphaned)
            {
                Log.Warn($"Removing {player.Session.Username}#{player.Session.PlayerId}: timed out and no TCP disconnect followed");
                _playersById.Remove(player.Session.PlayerId);
                _playersByEndPoint.TryRemove(player.EndPoint, out _);
            }
            _orphaned.Clear();

            UpdatePickups(pickupsTaken);
            

            if (_playersById.Count == 0)
                return;

            List<PlayerEntityState> entities = new(_playersById.Count);
            foreach (PlayerState player in _playersById.Values)
            {
                entities.Add(new PlayerEntityState
                {
                    EntityId = player.Session.PlayerId,
                    X = player.X,
                    Z = player.Z,
                    AimX = player.AimX,
                    AimZ = player.AimZ,
                    Health = GameConstants.MaxHealth, // no damage before M09
                    State = PlayerLifeState.Alive,
                    DashTicksLeft = player.Dash.TicksLeft,
                    DashCooldownTicks = player.Dash.CooldownTicks,
                    DashDirectionX = player.Dash.DirectionX,
                    DashDirectionZ = player.Dash.DirectionZ,
                });
            }

            List<PickupEntityState> pickupEntities = new(_pickups.Length);
            for (int i = 0; i < _pickups.Length; i++)
            {
                pickupEntities.Add(new PickupEntityState
                {
                    EntityId = (ushort)(GameConstants.PickupEntityIdBase + i),
                    Type = ArenaMap.Pickups[i].Type,
                    Active = _pickups[i].Active,
                });
            }

            foreach (PlayerState recipient in _playersById.Values)
            {
                SnapshotPacket snapshot = new()
                {
                    Sequence = Sequence,
                    ServerTick = ServerTick,
                    LastProcessedInputIndex = recipient.LastProcessedInputIndex,
                    Players = entities,
                    Pickups = pickupEntities,
                    
                };
                snapshotSends.Add((recipient.EndPoint, snapshot.ToDatagram()));
            }
        }
    }
}
