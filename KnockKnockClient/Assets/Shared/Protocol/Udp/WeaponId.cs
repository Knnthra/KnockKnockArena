namespace KnockKnockArena.Shared.Protocol.Udp
{
    public enum WeaponId : byte
    {
        Punch = 0,
        Glock = 1,
        Ak47 = 2,
        RocketLauncher = 3,
    }
    
    public enum EntityType : byte
    {
        Player = 1,
        Rocket = 2,
        Pickup = 3,
    }
    
    public enum PlayerLifeState : byte
    {
        Alive = 0,
        Dead = 1,
    }
}