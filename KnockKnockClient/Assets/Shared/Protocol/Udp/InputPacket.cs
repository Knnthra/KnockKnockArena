using System.IO;

namespace KnockKnockArena.Shared.Protocol.Udp
{
    /// <summary>
    /// Client -> server. One sampled input, sent every client tick (30 Hz).
    ///
    /// InputIndex increases by 1 per packet and is the key to server reconciliation:
    /// the snapshot echoes the last processed index back, and the client re-applies
    /// every input newer than that on top of the server's authoritative position.
    ///
    /// Datagram: [type=1] [version] [playerId:byte] [inputIndex:uint32] [tick:uint32]
    ///           [movement:byte] [aimX:float] [aimZ:float] [buttons:byte] [weaponId:byte]
    /// </summary>
    public sealed class InputPacket
    {
        public byte PlayerId;

        /// <summary>Client-side counter, +1 per input packet, starting at 1.</summary>
        public uint InputIndex;

        /// <summary>The client's estimate of the current server tick (used for lag compensation later).</summary>
        public uint Tick;

        public MovementBits Movement;

        /// <summary>World-space aim point on the XZ plane (from the client's cursor raycast).</summary>
        public float AimX;
        public float AimZ;

        public ButtonBits Buttons;

        /// <summary>The weapon to switch to; only meaningful when Buttons has SwitchWeapon set.</summary>
        public byte WeaponId;

        public byte[] ToDatagram()
        {
            return UdpPacketIO.Build(UdpPacketType.Input, writer =>
            {
                writer.Write(PlayerId);
                writer.Write(InputIndex);
                writer.Write(Tick);
                writer.Write((byte)Movement);
                writer.Write(AimX);
                writer.Write(AimZ);
                writer.Write((byte)Buttons);
                writer.Write(WeaponId);
            });
        }

        public static InputPacket FromDatagram(byte[] datagram)
        {
            return UdpPacketIO.Parse(datagram, reader => new InputPacket
            {
                PlayerId = reader.ReadByte(),
                InputIndex = reader.ReadUInt32(),
                Tick = reader.ReadUInt32(),
                Movement = (MovementBits)reader.ReadByte(),
                AimX = reader.ReadSingle(),
                AimZ = reader.ReadSingle(),
                Buttons = (ButtonBits)reader.ReadByte(),
                WeaponId = reader.ReadByte(),
            });
        }
    }
}
