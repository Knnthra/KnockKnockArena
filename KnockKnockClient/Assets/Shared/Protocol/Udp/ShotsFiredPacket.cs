using System.Collections.Generic;

namespace KnockKnockArena.Shared.Protocol.Udp
{
    /// <summary>One shot the server resolved this tick.</summary>
    public struct ShotRecord
    {
        public byte ShooterId;
        public WeaponId Weapon;

        /// <summary>Where the shot ENDED, as the server decided it: the entry point
        /// into the player it hit, else the first wall or the arena edge. For melee
        /// the end of the swing; for the rocket launcher the rocket's spawn point
        /// (the rocket itself is an entity in the snapshot).</summary>
        public float EndX;
        public float EndZ;

        /// <summary>True when the shot struck a player (the end point is on them).</summary>
        public bool HitPlayer;
    }

    /// <summary>
    /// Server -> all clients, UDP, at most one per tick: every shot fired that tick,
    /// hits and misses alike. Without it a client only ever saw OTHER players' shots
    /// through their consequences — blood on a hit, a rocket entity — so a burst that
    /// missed was invisible: no recoil, no muzzle flash, no tracer.
    ///
    /// Cosmetic, so unreliable is right: a lost packet costs one tracer, while TCP
    /// would hold up every shot behind it. Added as a NEW packet type in protocol
    /// v5; nothing existing changed.
    ///
    /// Wire: [type=101][version] [serverTick:u32] [count:u8]
    ///       then per shot: [shooterId:u8] [weapon:u8] [endX:f32] [endZ:f32] [hitPlayer:u8]
    /// </summary>
    public sealed class ShotsFiredPacket
    {
        /// <summary>Upper bound per packet: count is one byte. Twelve players on a
        /// 30 Hz tick fire at most a handful of shots per tick, far below this.</summary>
        public const int MaxShots = 255;

        public uint ServerTick;
        public List<ShotRecord> Shots = new List<ShotRecord>();

        public byte[] ToDatagram()
        {
            int count = Shots.Count > MaxShots ? MaxShots : Shots.Count;
            return UdpPacketIO.Build(UdpPacketType.ShotsFired, writer =>
            {
                writer.Write(ServerTick);
                writer.Write((byte)count);
                for (int i = 0; i < count; i++)
                {
                    ShotRecord shot = Shots[i];
                    writer.Write(shot.ShooterId);
                    writer.Write((byte)shot.Weapon);
                    writer.Write(shot.EndX);
                    writer.Write(shot.EndZ);
                    writer.Write(shot.HitPlayer ? (byte)1 : (byte)0);
                }
            });
        }

        public static ShotsFiredPacket FromDatagram(byte[] datagram)
        {
            return UdpPacketIO.Parse(datagram, reader =>
            {
                ShotsFiredPacket packet = new ShotsFiredPacket { ServerTick = reader.ReadUInt32() };
                int count = reader.ReadByte();
                for (int i = 0; i < count; i++)
                {
                    byte shooter = reader.ReadByte();
                    byte weapon = reader.ReadByte();
                    if (weapon > (byte)WeaponId.RocketLauncher)
                        throw new ProtocolException($"unknown weapon id {weapon}");
                    packet.Shots.Add(new ShotRecord
                    {
                        ShooterId = shooter,
                        Weapon = (WeaponId)weapon,
                        EndX = reader.ReadSingle(),
                        EndZ = reader.ReadSingle(),
                        HitPlayer = reader.ReadByte() != 0,
                    });
                }
                return packet;
            });
        }
    }
}
