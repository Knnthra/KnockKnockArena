using System.IO;

namespace KnockKnockArena.Shared.Protocol.Messages
{
    /// <summary>
    /// Server -> all clients. Kill feed entry: a player died.
    /// Payload: [version:byte] [killerId:byte] [killerUsername:string]
    ///          [victimId:byte] [victimUsername:string] [weaponId:byte]
    /// </summary>
    public sealed class PlayerDied
    {
        public byte KillerId;
        public string KillerUsername = "";
        public byte VictimId;
        public string VictimUsername = "";
        public byte WeaponId;

        public byte[] ToPayload()
        {
            return ProtocolSerialization.WritePayload(writer =>
            {
                writer.Write(KillerId);
                ProtocolSerialization.WriteString(writer, KillerUsername);
                writer.Write(VictimId);
                ProtocolSerialization.WriteString(writer, VictimUsername);
                writer.Write(WeaponId);
            });
        }

        public static PlayerDied FromPayload(byte[] payload)
        {
            return ProtocolSerialization.ReadPayload(payload, reader => new PlayerDied
            {
                KillerId = reader.ReadByte(),
                KillerUsername = ProtocolSerialization.ReadString(reader),
                VictimId = reader.ReadByte(),
                VictimUsername = ProtocolSerialization.ReadString(reader),
                WeaponId = reader.ReadByte(),
            });
        }
    }
}
