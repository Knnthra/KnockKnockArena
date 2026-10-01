namespace KnockKnockArena.Shared.Protocol.Udp
{
    public static class WeaponBits
    {
        public const byte StartingWeapons = (1 << (int)WeaponId.Punch) | 1 << (int)(WeaponId.Glock);

        public static string ToBinaryString(byte owned)
            => System.Convert.ToString(owned, 2).PadLeft(8, '0');

        public static bool HasWeapon(byte owned, WeaponId weapon)
         => (owned & (1 << (int)weapon)) != 0;
         
        public static byte AddWeapon(byte owned, WeaponId weapon)
        => (byte)(owned | (1 << (int)weapon));
        
        public static byte RemoveWeapon(byte owned, WeaponId weapon)
        => (byte)(owned & ~(1 << (int)weapon));
        
    }
}