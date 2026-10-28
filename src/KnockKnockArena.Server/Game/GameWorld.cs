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
/// Module 9: what a hit DOES. Damage and death (PlayerDamaged and PlayerDied on TCP),
/// respawn, the punch (the same rewind as a shot, an arc instead of a ray), the
/// rocket (an entity in the snapshot, RocketExploded on TCP), and what a pickup
/// gives the player, with the pickup radius per type from data/pickups.json.
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

    private volatile bool _rewindEnabled = true;

    /// <summary>Lag compensation on or off — the server console's "rewind on" and
    /// "rewind off" (M08). Off judges every shot and swing against where the targets
    /// are NOW. Both judgements are logged either way, so the difference is in the log
    /// whatever the setting.</summary>
    public bool RewindEnabled
    {
        get => _rewindEnabled;
        set => _rewindEnabled = value;
    }
    private readonly Dictionary<byte, PlayerState> _playersById = new();
    private readonly List<RocketState> _rockets = new();

    /// <summary>Damage raised during this tick's hit detection, drained at the end of
    /// <see cref="Tick"/>. A field rather than a parameter because the call sites sit
    /// several levels down inside hit detection.</summary>
    private readonly List<DamageEvent> _damageThisTick = new();

    /// <summary>Every shot resolved this tick, hit or miss, broadcast to all clients
    /// in one ShotsFired datagram after the snapshots — so other players' shots are
    /// visible, not only their consequences.</summary>
    private readonly List<ShotRecord> _shotsThisTick = new();

    /// <summary>Scratch list for timed-out players whose kick never despawned them.</summary>
    private readonly List<PlayerState> _orphaned = new();

    /// <summary>Scratch list: one player's inputs for the current tick (TakeInputs).</summary>
    private readonly List<InputPacket> _inputsThisTick = new();
    private readonly PickupSpot[] _pickups;
    private ushort _nextRocketId = GameConstants.RocketEntityIdBase;

    public GameWorld()
    {
        _pickups = new PickupSpot[ArenaMap.Pickups.Length];
        for (int i = 0; i < _pickups.Length; i++)
            _pickups[i] = new PickupSpot { PickupId = (byte)i };
    }
    private readonly ConcurrentDictionary<IPEndPoint, PlayerState> _playersByEndPoint = new();

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

            // Each player id has its own spawn point from the map; a respawn picks a
            // random one (Respawn).
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

    private static string Name(PlayerState player) =>
        $"{player.Session.Username}#{player.Session.PlayerId}";

    private void TryFire(PlayerState shooter, InputPacket input, List<KillEvent> kills)
    {
        // Still cooling down: normal when the fire button is held, not an error.
        if (ServerTick < shooter.NextFireAllowedTick)
            return;

        WeaponStats stats = WeaponStats.For(shooter.ActiveWeapon);

        if (!stats.IsMelee && shooter.Ammo[(int)shooter.ActiveWeapon] == 0)
        {
            Log.Packet($"REJECT fire from {Name(shooter)}: no ammo");
            return;
        }

        // The server derives the shot direction itself: muzzle (player center in the
        // server simulation) toward the aim point. For firearms, aim points inside the
        // dead zone give a meaningless direction and are rejected (S01 2.2).
        //
        // Melee is exempt from the dead zone (S01 2.5): a punch reaches 1.8 m, so the
        // natural way to throw one is with the cursor ON the opponent, well inside
        // 2 m. Only an aim point on the puncher's own position is refused — there is
        // no direction at all there. Same threshold as the body-yaw rule.
        float directionX = input.AimX - shooter.X;
        float directionZ = input.AimZ - shooter.Z;
        float aimDistanceSquared = directionX * directionX + directionZ * directionZ;
        float aimDistance = MathF.Sqrt(aimDistanceSquared);
        if (stats.IsMelee)
        {
            if (aimDistanceSquared < GameConstants.FacingDeadZoneSquared)
            {
                Log.Packet($"REJECT fire from {Name(shooter)}: melee aim point on own position");
                return;
            }
        }
        else if (aimDistance < GameConstants.DeadZoneMeters)
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

        if (stats.IsMelee)
            FireMelee(shooter, stats, directionX, directionZ, rewindTick, kills);
        else if (shooter.ActiveWeapon == WeaponId.RocketLauncher)
            FireRocket(shooter, directionX, directionZ, input.AimX, input.AimZ);
        else
            FireHitscan(shooter, stats, directionX, directionZ, rewindTick, kills);
    }

    private void FireRocket(PlayerState shooter, float facingX, float facingZ, float aimX, float aimZ)
    {
        shooter.Ammo[(int)WeaponId.RocketLauncher]--;

        // The rocket leaves the MUZZLE, not the shooter's navel — and the muzzle is
        // not on the shooter's midline. The weapon is held in a hand, off to one
        // side, so the offset has a SIDEWAYS component as well as a forward one.
        // Getting only the forward part right puts the rocket a metre and a half
        // beside the barrel, which is exactly as wrong as it sounds.
        //
        // Same convention as the movement rule: right = (forwardZ, -forwardX).
        WeaponStats stats = WeaponStats.For(WeaponId.RocketLauncher);
        float rightX = facingZ;
        float rightZ = -facingX;
        float muzzleX = (float)((float)(facingX * stats.MuzzleForwardMeters) +
                                (float)(rightX * stats.MuzzleRightMeters));
        float muzzleZ = (float)((float)(facingZ * stats.MuzzleForwardMeters) +
                                (float)(rightZ * stats.MuzzleRightMeters));

        // Walk that offset like a tiny flight step rather than teleporting to it, so
        // something in the way stops it: fire while hugging a wall and the rocket
        // appears against the wall (and detonates on it next tick) instead of
        // materialising on the far side.
        float reach = MathF.Sqrt(muzzleX * muzzleX + muzzleZ * muzzleZ);
        float offset = reach;
        if (reach > 0.0001f)
        {
            offset = ClearDistanceAhead(shooter, muzzleX / reach, muzzleZ / reach, reach);
            muzzleX = muzzleX / reach * offset;
            muzzleZ = muzzleZ / reach * offset;
        }

        float spawnX = (float)(shooter.X + muzzleX);
        float spawnZ = (float)(shooter.Z + muzzleZ);

        // The direction is MUZZLE -> AIM POINT (S01 2.2), recomputed from where the
        // rocket actually starts. Keeping the centre->aim direction here would make
        // the rocket fly PARALLEL to the aim line — starting 1.5 m right of centre
        // and staying 1.5 m right of everything the player aims at, forever. From
        // the muzzle, the path passes exactly through the aimed point.
        float directionX = (float)(aimX - spawnX);
        float directionZ = (float)(aimZ - spawnZ);
        float length = MathF.Sqrt(directionX * directionX + directionZ * directionZ);
        if (length < 0.0001f)
        {
            // Aimed exactly at the muzzle: fall back to the facing.
            directionX = facingX;
            directionZ = facingZ;
        }
        else
        {
            directionX = (float)(directionX / length);
            directionZ = (float)(directionZ / length);
        }

        // Others see the launcher kick; the rocket itself arrives as an entity.
        RecordShot(shooter, WeaponId.RocketLauncher, spawnX, spawnZ, false);

        _rockets.Add(new RocketState
        {
            EntityId = _nextRocketId,
            OwnerId = shooter.Session.PlayerId,
            OwnerName = shooter.Session.Username,
            X = spawnX,
            Z = spawnZ,
            DirectionX = directionX,
            DirectionZ = directionZ,
            // Detonate at the aimed point if nothing is hit on the way — that is
            // what firing at the ground means in a world that has none. The
            // degenerate aim-on-muzzle case keeps infinity (no self-destruct point).
            DistanceLeft = length < 0.0001f ? float.PositiveInfinity : length,
        });

        _nextRocketId++;
        if (_nextRocketId < GameConstants.RocketEntityIdBase)
            _nextRocketId = GameConstants.RocketEntityIdBase; // ushort wrapped around

        Log.Packet($"Rocket fired by {Name(shooter)} toward ({directionX:F2}, {directionZ:F2}) " +
                   $"from {offset:F2} m ahead");
    }

    /// <summary>
    /// How far the shooter can place something straight ahead before it would be
    /// inside a wall, outside the arena, or inside another player — capped at
    /// <paramref name="desired"/>.
    ///
    /// This is what stops the muzzle offset from becoming a teleport. Standing
    /// against a wall, the returned distance is the distance to that wall, so the
    /// rocket appears against it rather than behind it. Standing nose to nose with
    /// someone, it stops at them, so a point-blank shot still connects instead of
    /// spawning past its target.
    /// </summary>
    private float ClearDistanceAhead(PlayerState shooter, float directionX, float directionZ, float desired)
    {
        float clear = desired;

        foreach (Aabb wall in ArenaMap.Walls)
        {
            if (Raycast.RayIntersectsAabb(shooter.X, shooter.Z, directionX, directionZ,
                    wall.MinX, wall.MinZ, wall.MaxX, wall.MaxZ, out float t) && t < clear)
                clear = t;
        }

        // The arena edge counts as a wall, same as it does in flight.
        if (directionX > 0f) clear = MathF.Min(clear, (ArenaMap.BoundsMaxX - shooter.X) / directionX);
        else if (directionX < 0f) clear = MathF.Min(clear, (ArenaMap.BoundsMinX - shooter.X) / directionX);
        if (directionZ > 0f) clear = MathF.Min(clear, (ArenaMap.BoundsMaxZ - shooter.Z) / directionZ);
        else if (directionZ < 0f) clear = MathF.Min(clear, (ArenaMap.BoundsMinZ - shooter.Z) / directionZ);

        foreach (PlayerState other in _playersById.Values)
        {
            if (other == shooter || other.LifeState != PlayerLifeState.Alive)
                continue;
            const float half = ArenaMap.PlayerHalfExtent;
            if (Raycast.RayIntersectsAabb(shooter.X, shooter.Z, directionX, directionZ,
                    other.X - half, other.Z - half, other.X + half, other.Z + half, out float t) && t < clear)
                clear = t;
        }

        // Never behind the shooter, and stop a hair short of whatever was hit so the
        // rocket starts OUTSIDE it and the normal flight check detonates it cleanly.
        return clear >= desired ? desired : MathF.Max(0f, clear - 0.01f);
    }

    private void UpdateRockets(List<KillEvent> kills, List<RocketExplodedEvent> explosions)
    {
        float step = GameConstants.ResolveRocketSpeed() * GameConstants.TickDeltaTime;

        for (int i = _rockets.Count - 1; i >= 0; i--)
        {
            RocketState rocket = _rockets[i];

            // Earliest obstacle along this tick's flight segment wins.
            float hitDistance = float.PositiveInfinity;
            PlayerState? directVictim = null;

            foreach (Aabb wall in ArenaMap.Walls)
            {
                if (Raycast.RayIntersectsAabb(rocket.X, rocket.Z, rocket.DirectionX, rocket.DirectionZ,
                        wall.MinX, wall.MinZ, wall.MaxX, wall.MaxZ, out float t) && t <= step && t < hitDistance)
                {
                    hitDistance = t;
                    directVictim = null;
                }
            }

            // The arena bounds detonate rockets too (treat the outside as walls).
            float boundsDistance = float.PositiveInfinity;
            if (rocket.DirectionX > 0f) boundsDistance = (ArenaMap.BoundsMaxX - rocket.X) / rocket.DirectionX;
            else if (rocket.DirectionX < 0f) boundsDistance = (ArenaMap.BoundsMinX - rocket.X) / rocket.DirectionX;
            float boundsDistanceZ = float.PositiveInfinity;
            if (rocket.DirectionZ > 0f) boundsDistanceZ = (ArenaMap.BoundsMaxZ - rocket.Z) / rocket.DirectionZ;
            else if (rocket.DirectionZ < 0f) boundsDistanceZ = (ArenaMap.BoundsMinZ - rocket.Z) / rocket.DirectionZ;
            if (boundsDistanceZ < boundsDistance) boundsDistance = boundsDistanceZ;
            if (boundsDistance <= step && boundsDistance < hitDistance)
            {
                hitDistance = boundsDistance;
                directVictim = null;
            }

            // Players: present-time positions (the rocket is visible in every
            // snapshot, so dodging happens in real time — no rewind). The owner is
            // exempt from CONTACT (the rocket spawns inside their own square);
            // self damage happens through splash only.
            foreach (PlayerState other in _playersById.Values)
            {
                if (other.LifeState != PlayerLifeState.Alive || other.Session.PlayerId == rocket.OwnerId)
                    continue;
                const float half = ArenaMap.PlayerHalfExtent;
                if (Raycast.RayIntersectsAabb(rocket.X, rocket.Z, rocket.DirectionX, rocket.DirectionZ,
                        other.X - half, other.Z - half, other.X + half, other.Z + half, out float t) &&
                    t <= step && t < hitDistance)
                {
                    hitDistance = t;
                    directVictim = other;
                }
            }

            // The aimed point itself: reaching it detonates the rocket ("shooting
            // the ground"). Anything physically in the way still wins, being nearer.
            if (rocket.DistanceLeft <= step && rocket.DistanceLeft < hitDistance)
            {
                hitDistance = rocket.DistanceLeft;
                directVictim = null;
            }

            if (float.IsPositiveInfinity(hitDistance))
            {
                rocket.X += rocket.DirectionX * step;
                rocket.Z += rocket.DirectionZ * step;
                rocket.DistanceLeft -= step;
                continue;
            }

            _rockets.RemoveAt(i);
            // Pull the explosion point slightly back along the flight path: detonating
            // EXACTLY on a wall face would make the splash line-of-sight ray hit that
            // same wall at distance 0 and block ALL splash (FEJLLOG #34).
            float explodeAt = hitDistance > 0.05f ? hitDistance - 0.05f : 0f;
            ExplodeRocket(rocket,
                rocket.X + rocket.DirectionX * explodeAt,
                rocket.Z + rocket.DirectionZ * explodeAt,
                directVictim, kills, explosions);
        }
    }

    private void ExplodeRocket(RocketState rocket, float x, float z, PlayerState? directVictim,
        List<KillEvent> kills, List<RocketExplodedEvent> explosions)
    {
        // The owner may have disconnected mid-flight (or the id may have been reused
        // by a new login) — only credit frags when it is still the same person.
        _playersById.TryGetValue(rocket.OwnerId, out PlayerState? owner);
        if (owner != null && owner.Session.Username != rocket.OwnerName)
            owner = null;

        if (directVictim != null)
        {
            ApplyDamage(owner, rocket.OwnerId, rocket.OwnerName, WeaponId.RocketLauncher,
                directVictim, WeaponStats.For(WeaponId.RocketLauncher).Damage, kills, x, z);
        }

        // Splash with linear falloff, walls block, self damage included. The direct
        // victim already took the full 100 and is skipped.
        foreach (PlayerState player in _playersById.Values)
        {
            if (player.LifeState != PlayerLifeState.Alive || player == directVictim)
                continue;

            float dx = player.X - x;
            float dz = player.Z - z;
            float distance = MathF.Sqrt(dx * dx + dz * dz);
            if (distance > GameConstants.RocketSplashRadius)
                continue;

            if (distance > 0.0001f)
            {
                bool blocked = false;
                foreach (Aabb wall in ArenaMap.Walls)
                {
                    if (Raycast.RayIntersectsAabb(x, z, dx / distance, dz / distance,
                            wall.MinX, wall.MinZ, wall.MaxX, wall.MaxZ, out float t) && t < distance)
                    {
                        blocked = true;
                        break;
                    }
                }
                if (blocked)
                    continue;
            }

            int damage = (int)(GameConstants.RocketSplashMaxDamage * (1f - distance / GameConstants.RocketSplashRadius));
            if (damage <= 0)
                continue;
            ApplyDamage(owner, rocket.OwnerId, rocket.OwnerName, WeaponId.RocketLauncher, player, damage, kills, player.X, player.Z);
        }

        explosions.Add(new RocketExplodedEvent(rocket.EntityId, x, z, rocket.OwnerId));
        Log.Packet($"Rocket {rocket.EntityId} exploded at ({x:F1}, {z:F1})");
    }

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
                if (player.LifeState != PlayerLifeState.Alive)
                    continue;
                float dx = player.X - pickupX;
                float dz = player.Z - pickupZ;
                float radius = PickupTable.Current.RadiusFor(type);
                if (dx * dx + dz * dz > radius * radius)
                    continue;

                // A health pack is only for the wounded: at full health you walk over
                // it and it stays for someone who needs it.
                if (type == PickupType.Health && player.Health >= GameConstants.MaxHealth)
                    continue;

                ApplyPickup(player, type);
                spot.Active = false;
                spot.RespawnAtTick = ServerTick + (uint)(RespawnSecondsFor(type) * GameConstants.TickRate);
                pickupsTaken.Add(new PickupTakenEvent(player.Session.PlayerId, player.Session.Username, (byte)i, type));
                Log.Info($"Pickup: {Name(player)} took {type} (pickup {i})");
                break; // one winner per pickup per tick
            }
        }
    }

    private static void ApplyPickup(PlayerState player, PickupType type)
    {
        switch (type)
        {
            case PickupType.Ak47Weapon:
                TakeWeapon(player, WeaponId.Ak47, GameConstants.Ak47AmmoPickupAmount);
                break;
            case PickupType.RocketLauncherWeapon:
                TakeWeapon(player, WeaponId.RocketLauncher, GameConstants.RocketAmmoPickupAmount);
                break;
            case PickupType.GlockAmmo:
                player.Ammo[(int)WeaponId.Glock] =
                    (ushort)(player.Ammo[(int)WeaponId.Glock] + GameConstants.GlockAmmoPickupAmount);
                break;
            case PickupType.Ak47Ammo:
                player.Ammo[(int)WeaponId.Ak47] =
                    (ushort)(player.Ammo[(int)WeaponId.Ak47] + GameConstants.Ak47AmmoPickupAmount);
                break;
            case PickupType.RocketAmmo:
                player.Ammo[(int)WeaponId.RocketLauncher] =
                    (ushort)(player.Ammo[(int)WeaponId.RocketLauncher] + GameConstants.RocketAmmoPickupAmount);
                break;
            case PickupType.Health:
                int healed = player.Health + GameConstants.HealthPickupAmount;
                player.Health = healed > GameConstants.MaxHealth ? GameConstants.MaxHealth : (byte)healed;
                break;
        }
    }

    /// <summary>
    /// Picks up a weapon. A weapon you already carry only tops up its ammo; a new one
    /// is added to your arsenal and DRAWN IMMEDIATELY if it beats what you are
    /// holding, so running over something better arms you without a keypress. The
    /// ranking is the weapon table's Priority — ties keep the weapon in your hands.
    /// </summary>
    private static void TakeWeapon(PlayerState player, WeaponId weapon, ushort ammo)
    {
        bool alreadyOwned = (player.OwnedWeapons & 1 << (int)weapon) != 0;

        player.Ammo[(int)weapon] = (ushort)(player.Ammo[(int)weapon] + ammo);
        if (alreadyOwned)
            return;

        player.OwnedWeapons = (byte)(player.OwnedWeapons | 1 << (int)weapon);

        if (WeaponStats.For(weapon).Priority > WeaponStats.For(player.ActiveWeapon).Priority)
        {
            player.ActiveWeapon = weapon;
            player.SpreadBloom = 0f; // bloom does not carry across weapons
        }
    }

    private static float RespawnSecondsFor(PickupType type) => type switch
    {
        PickupType.Ak47Weapon or PickupType.RocketLauncherWeapon
            => GameConstants.WeaponPickupRespawnSeconds,
        PickupType.Health => GameConstants.HealthPickupRespawnSeconds,
        _ => GameConstants.AmmoPickupRespawnSeconds,
    };

    private uint ClampRewindTick(uint clientTick)
    {
        if (clientTick > ServerTick)
            return ServerTick;
        uint oldest = ServerTick >= GameConstants.RewindBufferTicks - 1
            ? ServerTick - (uint)(GameConstants.RewindBufferTicks - 1)
            : 0;
        return clientTick < oldest ? oldest : clientTick;
    }

    private void FireHitscan(PlayerState shooter, WeaponStats stats, float directionX, float directionZ,
        uint rewindTick, List<KillEvent> kills)
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
                 JudgedBy(rewind, rewindTick));

        if (victim != null)
        {
            // Where the ray entered the victim's box — the point the blood plays at.
            ApplyDamage(shooter, shooter.Session.PlayerId, shooter.Session.Username, shooter.ActiveWeapon,
                victim, stats.Damage, kills,
                (float)(shooter.X + (float)(shotX * victimDistance)),
                (float)(shooter.Z + (float)(shotZ * victimDistance)));
        }

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

    /// <summary>The nearest living player the ray enters before the first wall — at a
    /// past tick (rewind), or where they stand now (atTick null).</summary>
    private PlayerState? FindVictim(PlayerState shooter, float shotX, float shotZ, float wallDistance,
        uint? atTick, out float distance, out float offCentre)
    {
        PlayerState? victim = null;
        distance = float.PositiveInfinity;
        offCentre = 0f;
        foreach (PlayerState other in _playersById.Values)
        {
            if (other == shooter || other.LifeState != PlayerLifeState.Alive)
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

    private static string Verdict(PlayerState? victim) => victim == null ? "miss" : $"hit {Name(victim)}";

    private static string Verdict(PlayerState? victim, float offCentre) =>
        victim == null ? "miss" : $"hit {Name(victim)} ({offCentre:0.00} m from centre)";

    private string JudgedBy(bool rewind, uint rewindTick) => rewind
        ? $"judged by rewind (tick {rewindTick}, {ServerTick - rewindTick} ticks back)"
        : "judged in the present";

    private void FireMelee(PlayerState shooter, WeaponStats stats, float directionX, float directionZ,
        uint rewindTick, List<KillEvent> kills)
    {
        // Arc check in front of the player, with rewind, max ONE victim per swing
        // (the nearest). Walls block melee just like hitscan.
        float maxDistance = stats.MeleeRangeMeters + ArenaMap.PlayerHalfExtent;
        float minCos = MathF.Cos(stats.MeleeArcDeg * 0.5f * (MathF.PI / 180f));

        // The same two judgements as a shot (FireHitscan) — an arc instead of a ray.
        PlayerState? rewound = FindMeleeVictim(shooter, directionX, directionZ, maxDistance, minCos, rewindTick, out float rewoundDistance);
        PlayerState? present = FindMeleeVictim(shooter, directionX, directionZ, maxDistance, minCos, null, out float presentDistance);
        bool rewind = RewindEnabled;
        PlayerState? victim = rewind ? rewound : present;
        float victimDistance = rewind ? rewoundDistance : presentDistance;
        Log.Info($"Swing: {Name(shooter)} {shooter.ActiveWeapon}, rewind: {Verdict(rewound)} · present: {Verdict(present)} · " +
                 JudgedBy(rewind, rewindTick));

        if (victim != null)
        {
            ApplyDamage(shooter, shooter.Session.PlayerId, shooter.Session.Username, shooter.ActiveWeapon,
                victim, stats.Damage, kills,
                (float)(shooter.X + (float)(directionX * victimDistance)),
                (float)(shooter.Z + (float)(directionZ * victimDistance)));
        }

        float swingEnd = victim != null ? victimDistance : maxDistance;
        RecordShot(shooter, shooter.ActiveWeapon,
            (float)(shooter.X + (float)(directionX * swingEnd)),
            (float)(shooter.Z + (float)(directionZ * swingEnd)),
            victim != null);
    }

    /// <summary>The nearest living player inside the swing's arc and reach, not behind
    /// a wall — at a past tick (rewind), or where they stand now (atTick null).</summary>
    private PlayerState? FindMeleeVictim(PlayerState shooter, float directionX, float directionZ,
        float maxDistance, float minCos, uint? atTick, out float victimDistance)
    {
        PlayerState? victim = null;
        victimDistance = float.PositiveInfinity;
        foreach (PlayerState other in _playersById.Values)
        {
            if (other == shooter || other.LifeState != PlayerLifeState.Alive)
                continue;

            (float targetX, float targetZ) = atTick is uint tick ? other.GetPositionAt(tick) : (other.X, other.Z);
            float toX = targetX - shooter.X;
            float toZ = targetZ - shooter.Z;
            float distance = MathF.Sqrt(toX * toX + toZ * toZ);
            if (distance > maxDistance || distance >= victimDistance)
                continue;

            if (distance > 0.0001f)
            {
                float cosAngle = (toX * directionX + toZ * directionZ) / distance;
                if (cosAngle < minCos)
                    continue;

                bool blocked = false;
                foreach (Aabb wall in ArenaMap.Walls)
                {
                    if (Raycast.RayIntersectsAabb(shooter.X, shooter.Z, toX / distance, toZ / distance,
                            wall.MinX, wall.MinZ, wall.MaxX, wall.MaxZ, out float t) && t < distance)
                    {
                        blocked = true;
                        break;
                    }
                }
                if (blocked)
                    continue;
            }

            victim = other;
            victimDistance = distance;
        }
        return victim;
    }

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
    /// Applies damage and handles death. The attacker is passed both as state and as
    /// id/name values: a rocket's owner may have disconnected mid-flight, in which
    /// case the kill feed still names them but nobody's frag counter moves.
    /// Self-kills (rocket splash) count a death but never a frag.
    /// </summary>
    /// <param name="hitX">Where the damage landed, for the client's blood. The entry
    /// point into the victim's box for hitscan and melee; the victim's own position
    /// for splash, which arrives from every direction at once.</param>
    private void ApplyDamage(PlayerState? attacker, byte attackerId, string attackerName, WeaponId weapon,
        PlayerState victim, int damage, List<KillEvent> kills, float hitX, float hitZ)
    {
        byte dealt = (byte)Math.Min(damage, victim.Health);
        victim.Health = damage >= victim.Health ? (byte)0 : (byte)(victim.Health - damage);

        // Announced even when it kills: the blood plays on the hit, and the kill feed
        // is a separate event about a separate thing.
        _damageThisTick.Add(new DamageEvent(victim.Session.PlayerId, hitX, hitZ,
            attackerId, weapon, dealt));

        if (victim.Health > 0)
            return;

        victim.LifeState = PlayerLifeState.Dead;
        victim.DeathTick = ServerTick;
        victim.DeathX = victim.X;
        victim.DeathZ = victim.Z;
        victim.Deaths++;
        if (attacker != null && attacker != victim)
            attacker.Frags++;
        kills.Add(new KillEvent(attackerId, attackerName,
            victim.Session.PlayerId, victim.Session.Username, weapon));
        Log.Info($"Kill: {attackerName}#{attackerId} killed {Name(victim)} with {weapon}");
    }

    private void Respawn(PlayerState player)
    {
        // Quake respawn: a random spawn point, excluding the one nearest the death
        // spot. No spawn protection.
        int nearest = 0;
        float nearestDistanceSq = float.PositiveInfinity;
        for (int i = 0; i < ArenaMap.SpawnPoints.Length; i++)
        {
            (float spawnX, float spawnZ) = ArenaMap.SpawnPoints[i];
            float dx = spawnX - player.DeathX;
            float dz = spawnZ - player.DeathZ;
            float distanceSq = dx * dx + dz * dz;
            if (distanceSq < nearestDistanceSq)
            {
                nearestDistanceSq = distanceSq;
                nearest = i;
            }
        }
        int pick;
        if (ArenaMap.SpawnPoints.Length <= 1)
        {
            pick = 0; // a one-spawn map cannot exclude the nearest point
        }
        else
        {
            pick = _random.Next(ArenaMap.SpawnPoints.Length - 1);
            if (pick >= nearest)
                pick++;
        }
        (player.X, player.Z) = ArenaMap.SpawnPoints[pick];

        // Death costs everything: back to punch + glock with start ammo.
        player.Health = GameConstants.MaxHealth;
        player.LifeState = PlayerLifeState.Alive;
        player.OwnedWeapons = (1 << (int)WeaponId.Punch) | (1 << (int)WeaponId.Glock);
        player.ActiveWeapon = WeaponId.Glock;
        Array.Clear(player.Ammo, 0, player.Ammo.Length);
        player.Ammo[(int)WeaponId.Glock] = GameConstants.GlockStartAmmo;
        player.SpreadBloom = 0f;
        player.NextFireAllowedTick = ServerTick;
        player.Dash = default;
        Log.Info($"Respawn: {Name(player)} at ({player.X}, {player.Z})");
    }

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
    /// Players whose UDP has been silent too long are returned in timedOut; kills
    /// produced this tick are returned in kills (broadcast on TCP by the caller).
    /// </summary>
    public void Tick(List<(IPEndPoint EndPoint, byte[] Datagram)> snapshotSends, List<PlayerState> timedOut,
        List<KillEvent> kills, List<PickupTakenEvent> pickupsTaken, List<RocketExplodedEvent> explosions,
        List<DamageEvent> damage)
    {
        lock (_gate)
        {
            // Damage is raised deep inside hit detection, several call sites down, so
            // it collects here rather than threading a list through every one of them.
            _damageThisTick.Clear();
            _shotsThisTick.Clear();

            ServerTick++;
            Sequence++;

            DateTime now = DateTime.UtcNow;

            foreach (PlayerState player in _playersById.Values)
            {
                // TakeInputs hands out this tick's inputs by the budget rule, in index
                // order, and acknowledges them - ALWAYS, also while dead, otherwise the
                // client's reconciliation would stall waiting for its index.
                TakeInputs(player, _inputsThisTick);
                foreach (InputPacket input in _inputsThisTick)
                {
                    if (player.LifeState == PlayerLifeState.Dead)
                    {
                        // Quake respawn: press fire, at the earliest RespawnDelaySeconds
                        // after death. Movement and aim are ignored while dead.
                        if ((input.Buttons & (ButtonBits.Fire | ButtonBits.Respawn)) != 0 &&
                            ServerTick - player.DeathTick >= (uint)(GameConstants.RespawnDelaySeconds * GameConstants.TickRate))
                            Respawn(player);
                        continue;
                    }

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
                        TryFire(player, input, kills);
                }

                if (player.LifeState == PlayerLifeState.Alive)
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
                Log.Warn($"Removing {Name(player)}: timed out and no TCP disconnect followed");
                _playersById.Remove(player.Session.PlayerId);
                _playersByEndPoint.TryRemove(player.EndPoint, out _);
            }
            _orphaned.Clear();

            UpdateRockets(kills, explosions);
            UpdatePickups(pickupsTaken);

            // Hand the tick's damage to the caller BEFORE the early return below:
            // a hit that empties the last player out of the world still happened.
            damage.AddRange(_damageThisTick);

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
                    Health = player.Health,
                    ActiveWeapon = player.ActiveWeapon,
                    OwnedWeapons = player.OwnedWeapons,
                    Ammo = player.Ammo[(int)player.ActiveWeapon],
                    Frags = player.Frags,
                    Deaths = player.Deaths,
                    State = player.LifeState,
                    DashTicksLeft = player.Dash.TicksLeft,
                    DashCooldownTicks = player.Dash.CooldownTicks,
                    DashDirectionX = player.Dash.DirectionX,
                    DashDirectionZ = player.Dash.DirectionZ,
                });
            }

            List<RocketEntityState> rocketEntities = new(_rockets.Count);
            foreach (RocketState rocket in _rockets)
            {
                rocketEntities.Add(new RocketEntityState
                {
                    EntityId = rocket.EntityId,
                    X = rocket.X,
                    Z = rocket.Z,
                    DirectionX = rocket.DirectionX,
                    DirectionZ = rocket.DirectionZ,
                    OwnerId = rocket.OwnerId,
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
                    Rockets = rocketEntities,
                    Pickups = pickupEntities,
                };
                snapshotSends.Add((recipient.EndPoint, snapshot.ToDatagram()));
                if (shotsDatagram != null)
                    snapshotSends.Add((recipient.EndPoint, shotsDatagram));
            }
        }
    }
}
