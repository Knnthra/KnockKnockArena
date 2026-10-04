using System;
using System.IO;

namespace KnockKnockArena.Shared.Protocol.Udp
{
    public sealed class PlayerEntityState
    {
        public const int SerializedSize = 39;

        public ushort EntityId;
        public float X;
        public float Z;
        public float AimX;
        public float AimZ;
        public byte Health;
        public WeaponId ActiveWeapon;
        public byte OwnedWeapons;
        public ushort Ammo;
        public ushort Frags;
        public ushort Deaths;
        public PlayerLifeState State;
        public byte DashTicksLeft;
        public byte DashCooldownTicks;
        public float DashDirectionX;
        public float DashDirectionZ;

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write(EntityId);
            writer.Write((byte)EntityType.Player);
            writer.Write(X);
            writer.Write(Z);
            writer.Write(AimX);
            writer.Write(AimZ);
            writer.Write(Health);
            writer.Write((byte)ActiveWeapon);
            writer.Write(OwnedWeapons);
            writer.Write(Ammo);
            writer.Write(Frags);
            writer.Write(Deaths);
            writer.Write((byte)State);
            writer.Write(DashTicksLeft);
            writer.Write(DashCooldownTicks);
            writer.Write(DashDirectionX);
            writer.Write(DashDirectionZ);
        }

        public static PlayerEntityState ReadFrom(BinaryReader reader)
        {
            ushort entityId = reader.ReadUInt16();
            byte entityType = reader.ReadByte();

            if (entityType != (byte)EntityType.Player)
                throw new ProtocolException($"Expected a player entity, got type{entityType}");

            return ReadData(reader, entityId);



        }

        public static PlayerEntityState ReadData(BinaryReader reader, ushort entityId)
        {
            return new PlayerEntityState
            {
                EntityId = entityId,
                X = reader.ReadSingle(),
                Z = reader.ReadSingle(),
                AimX = reader.ReadSingle(),
                AimZ = reader.ReadSingle(),
                Health = reader.ReadByte(),
                ActiveWeapon = (WeaponId)reader.ReadByte(),
                OwnedWeapons = reader.ReadByte(),
                Ammo = reader.ReadUInt16(),
                Frags = reader.ReadUInt16(),
                Deaths = reader.ReadUInt16(),
                State = (PlayerLifeState)reader.ReadByte(),
                DashTicksLeft = reader.ReadByte(),
                DashCooldownTicks = reader.ReadByte(),
                DashDirectionX = reader.ReadSingle(),
                DashDirectionZ = reader.ReadSingle(),
            };
        }



    }
}
