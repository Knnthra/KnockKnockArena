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
/// Module 8: shooting. Weapon switches and shots ride in the inputs; a shot is judged
/// with lag compensation (rewind) and in the present, both are logged, and every shot
/// goes out to the others in a ShotsFired datagram. What a hit DOES (damage, death) and
/// what a pickup gives come in M09, as do the punch and the rocket.
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
    private readonly Random _random = new();

    /// <summary>Every shot resolved this tick, hit or miss, broadcast to all clients
    /// in one ShotsFired datagram after the snapshots — so other players' shots are
    /// visible, not only their consequences.</summary>
    private readonly List<ShotRecord> _shotsThisTick = new();

    private volatile bool _rewindEnabled = true;

    /// <summary>Lag compensation on or off — the server console's "rewind on" and
    /// "rewind off". Off judges every shot against where the targets are NOW. Both
    /// judgements are logged either way, so the difference is in the log whatever the
    /// setting.</summary>
    public bool RewindEnabled
    {
        get => _rewindEnabled;
        set => _rewindEnabled = value;
    }
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
    /// Switches the active weapon — server-authoritative: the weapon must be owned
    /// (its bit set in the ownership bitfield), anything else is ignored. A client
    /// that sends unowned weapon ids is buggy or cheating, so it is worth a warning.
    /// </summary>
    private static void TrySwitchWeapon(PlayerState player, byte weaponId)
    {
        if (weaponId > (byte)WeaponId.RocketLauncher)
        {
            Log.Warn($"REJECT weapon switch from {player.Session.Username}#{player.Session.PlayerId}: " +
                     $"unknown weapon {weaponId}");
            return;
        }
        if ((player.OwnedWeapons & 1 << weaponId) == 0)
        {
            Log.Warn($"REJECT weapon switch from {player.Session.Username}#{player.Session.PlayerId}: " +
                     $"weapon {(WeaponId)weaponId} not owned");
            return;
        }
        player.ActiveWeapon = (WeaponId)weaponId;
        player.SpreadBloom = 0f; // bloom does not carry across weapons
    }

    private void TryFire(PlayerState shooter, InputPacket input)
    {
        // Still cooling down: normal when the fire button is held, not an error.
        if (ServerTick < shooter.NextFireAllowedTick)
            return;

        WeaponStats stats = WeaponStats.For(shooter.ActiveWeapon);

        // Module 8: firearms. The punch comes in M09 (an arc instead of a ray), and the
        // rocket launcher cannot be picked up before M09.
        if (stats.IsMelee || shooter.ActiveWeapon == WeaponId.RocketLauncher)
            return;

        if (shooter.Ammo[(int)shooter.ActiveWeapon] == 0)
        {
            Log.Packet($"REJECT fire from {Name(shooter)}: no ammo");
            return;
        }

        // The server derives the shot direction itself: the player's centre toward the
        // aim point. Aim points inside the dead zone give a meaningless direction and
        // are rejected (S01 2.2).
        float directionX = input.AimX - shooter.X;
        float directionZ = input.AimZ - shooter.Z;
        float aimDistance = MathF.Sqrt(directionX * directionX + directionZ * directionZ);
        if (aimDistance < GameConstants.DeadZoneMeters)
        {
            Log.Packet($"REJECT fire from {Name(shooter)}: behind check failed (aim point in dead zone)");
            return;
        }
        directionX /= aimDistance;
        directionZ /= aimDistance;

        shooter.NextFireAllowedTick = ServerTick + (uint)MathF.Ceiling(stats.CooldownSeconds * GameConstants.TickRate);

        // Lag compensation: judge the shot at the tick the shooter SAW (the tick of
        // the picture they aimed at), clamped to the history buffer.
        uint rewindTick = ClampRewindTick(input.Tick);
        FireHitscan(shooter, directionX, directionZ, rewindTick);
    }

    private uint ClampRewindTick(uint clientTick)
    {
        if (clientTick > ServerTick)
            return ServerTick;
        uint oldest = ServerTick >= GameConstants.RewindBufferTicks - 1
            ? ServerTick - (uint)(GameConstants.RewindBufferTicks - 1)
            : 0;
        return clientTick < oldest ? oldest : clientTick;
    }

    private void FireHitscan(PlayerState shooter, float directionX, float directionZ, uint rewindTick)
    {
        shooter.Ammo[(int)shooter.ActiveWeapon]--;

        // Server-authoritative spread: the server rolls the deviation and owns the
        // bloom. The client only mirrors the bloom value for its crosshair.
        float spreadDeg = SpreadModel.CurrentSpreadDeg(shooter.ActiveWeapon, shooter.SpreadBloom);
        shooter.SpreadBloom = SpreadModel.OnShot(shooter.ActiveWeapon, shooter.SpreadBloom);
        float deviationRad = (float)(_random.NextDouble() * 2.0 - 1.0) * spreadDeg * (MathF.PI / 180f);
        float cos = MathF.Cos(deviationRad);
        float sin = MathF.Sin(deviationRad);
        float shotX = directionX * cos - directionZ * sin;
        float shotZ = directionX * sin + directionZ * cos;

        // Geometry blocks hitscan: the nearest wall along the ray caps the range.
        float wallDistance = float.PositiveInfinity;
        foreach (Aabb wall in ArenaMap.Walls)
        {
            if (Raycast.RayIntersectsAabb(shooter.X, shooter.Z, shotX, shotZ,
                    wall.MinX, wall.MinZ, wall.MaxX, wall.MaxZ, out float t) && t < wallDistance)
                wallDistance = t;
        }

        // Judged twice: against where the targets were at the tick the shooter saw
        // (rewind), and against where they are now (present). The setting decides
        // which one counts; the log always shows both.
        PlayerState? rewound = FindVictim(shooter, shotX, shotZ, wallDistance, rewindTick,
            out float rewoundDistance, out float rewoundOffCentre);
        PlayerState? present = FindVictim(shooter, shotX, shotZ, wallDistance, null,
            out float presentDistance, out float presentOffCentre);
        bool rewind = RewindEnabled;
        PlayerState? victim = rewind ? rewound : present;
        float victimDistance = rewind ? rewoundDistance : presentDistance;
        Log.Info($"Shot: {Name(shooter)} {shooter.ActiveWeapon}, rewind: {Verdict(rewound, rewoundOffCentre)} · present: {Verdict(present, presentOffCentre)} · " +
                 (rewind ? $"judged by rewind (tick {rewindTick}, {ServerTick - rewindTick} ticks back)" : "judged in the present"));
        // Module 8 stops at the verdict: what a hit DOES (damage, death) comes in M09.

        // What everyone else draws: the shot to where it actually ended — the entry
        // point into the victim, else the first wall or the arena edge.
        float endDistance = victim != null
            ? victimDistance
            : Raycast.ArenaHitDistance(shooter.X, shooter.Z, shotX, shotZ);
        RecordShot(shooter, shooter.ActiveWeapon,
            (float)(shooter.X + (float)(shotX * endDistance)),
            (float)(shooter.Z + (float)(shotZ * endDistance)),
            victim != null);
    }

    /// <summary>The nearest player the ray enters before the first wall — at a past
    /// tick (rewind), or where they stand now (atTick null).</summary>
    private PlayerState? FindVictim(PlayerState shooter, float shotX, float shotZ, float wallDistance,
        uint? atTick, out float distance, out float offCentre)
    {
        PlayerState? victim = null;
        distance = float.PositiveInfinity;
        offCentre = 0f;
        foreach (PlayerState other in _playersById.Values)
        {
            if (other == shooter)
                continue;

            (float targetX, float targetZ) = atTick is uint tick ? other.GetPositionAt(tick) : (other.X, other.Z);
            const float half = ArenaMap.PlayerHalfExtent;
            if (Raycast.RayIntersectsAabb(shooter.X, shooter.Z, shotX, shotZ,
                    targetX - half, targetZ - half, targetX + half, targetZ + half, out float t) &&
                t < distance && t < wallDistance)
            {
                victim = other;
                distance = t;
                // How far the ray passed from the target's centre (the shot direction
                // is a unit vector, so the cross product is the perpendicular distance).
                offCentre = MathF.Abs((targetX - shooter.X) * shotZ - (targetZ - shooter.Z) * shotX);
            }
        }
        return victim;
    }

    private static string Verdict(PlayerState? victim, float offCentre) =>
        victim == null ? "miss" : $"hit {Name(victim)} ({offCentre:0.00} m from centre)";

    private void RecordShot(PlayerState shooter, WeaponId weapon, float endX, float endZ, bool hitPlayer)
    {
        if (_shotsThisTick.Count >= ShotsFiredPacket.MaxShots)
            return; // never reached in practice; the packet's count is one byte
        _shotsThisTick.Add(new ShotRecord
        {
            ShooterId = shooter.Session.PlayerId,
            Weapon = weapon,
            EndX = endX,
            EndZ = endZ,
            HitPlayer = hitPlayer,
        });
    }

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
            _shotsThisTick.Clear();

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

                    if ((input.Buttons & ButtonBits.SwitchWeapon) != 0)
                        TrySwitchWeapon(player, input.WeaponId);

                    if ((input.Buttons & ButtonBits.Fire) != 0)
                        TryFire(player, input);
                }

                player.SpreadBloom = SpreadModel.Decay(player.ActiveWeapon, player.SpreadBloom, GameConstants.TickDeltaTime);

                // Record AFTER movement so the history holds this tick's final position.
                player.RecordHistory(ServerTick);

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
                    ActiveWeapon = player.ActiveWeapon,
                    OwnedWeapons = player.OwnedWeapons,
                    Ammo = player.Ammo[(int)player.ActiveWeapon],
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

            // The tick's shots go out once, to everyone (the shooter's client skips its
            // own — it drew them on the keypress already).
            byte[]? shotsDatagram = null;
            if (_shotsThisTick.Count > 0)
            {
                shotsDatagram = new ShotsFiredPacket
                {
                    ServerTick = ServerTick,
                    Shots = new List<ShotRecord>(_shotsThisTick),
                }.ToDatagram();
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
                if (shotsDatagram != null)
                    snapshotSends.Add((recipient.EndPoint, shotsDatagram));
            }
        }
    }
}
