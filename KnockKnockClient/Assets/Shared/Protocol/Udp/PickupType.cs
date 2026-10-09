namespace KnockKnockArena.Shared.Protocol.Udp
{
    /// <summary>Pickup types. Weapon pickups grant the weapon (plus bundled ammo);
    /// ammo pickups only add to the pool; health heals +25 capped at 100.
    ///
    /// Its own file because the base project (M04) ships it: the map parser
    /// (ArenaMapData) and PickupMarker need it, and the students' own Shared files
    /// do not contain it until they reach pickups.</summary>
    public enum PickupType : byte
    {
        Ak47Weapon = 0,
        RocketLauncherWeapon = 1,
        GlockAmmo = 2,
        Ak47Ammo = 3,
        RocketAmmo = 4,
        Health = 5,
    }
}
