using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Server.Game;

/// <summary>A kill produced by the tick loop; broadcast on TCP as PlayerDied.</summary>
public readonly record struct KillEvent(
    byte KillerId,
    string KillerName,
    byte VictimId,
    string VictimName,
    WeaponId Weapon);

/// <summary>
/// Damage dealt this tick; broadcast on TCP as PlayerDamaged.
///
/// X/Z is where the hit LANDED — the ray's entry point into the victim's box for
/// hitscan and melee, the victim's own position for splash, which has no single
/// point of entry. Hit detection computes it anyway; before this event it was
/// discarded, and the client had no way to know where to put the blood.
/// </summary>
public readonly record struct DamageEvent(
    byte VictimId,
    float X,
    float Z,
    byte AttackerId,
    WeaponId Weapon,
    byte Damage);

/// <summary>A rocket detonation this tick; broadcast on TCP as RocketExploded.</summary>
public readonly record struct RocketExplodedEvent(
    ushort RocketEntityId,
    float X,
    float Z,
    byte OwnerId);
