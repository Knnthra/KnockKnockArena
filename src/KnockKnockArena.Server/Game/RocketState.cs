namespace KnockKnockArena.Server.Game;

/// <summary>
/// A rocket in flight, simulated by the tick loop in present time (no rewind —
/// the rocket is visible in every snapshot, so dodging it happens in real time).
/// Owner identity is stored by value: the rocket survives its owner disconnecting.
/// </summary>
public sealed class RocketState
{
    public ushort EntityId;
    public byte OwnerId;
    public string OwnerName = "";
    public float X;
    public float Z;
    public float DirectionX;
    public float DirectionZ;

    /// <summary>
    /// Flight left before the rocket detonates on its own — set at launch to the
    /// distance from the muzzle to the AIM POINT.
    ///
    /// This is what makes "shooting the ground" work in a world with no ground:
    /// the server is pure XZ, so a shot aimed at the floor has nothing to collide
    /// with there — without this the rocket would sail straight through the spot
    /// the player aimed at and fly on until it found a wall. Reaching the aimed
    /// point IS the impact.
    /// </summary>
    public float DistanceLeft = float.PositiveInfinity;
}
