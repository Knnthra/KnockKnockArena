using System.IO;
using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Shared.Protocol.Messages
{
    /// <summary>
    /// Server -> all clients. A player walked over a pickup.
    /// Payload: [version:byte] [playerId:byte] [username:string] [pickupId:byte] [pickupType:byte]
    /// </summary>
    public sealed class PickupTaken
    {
        public byte PlayerId;
        public string Username = "";

        /// <summary>Index into the published pickup table.</summary>
        public byte PickupId;

        public PickupType Type;

        public byte[] ToPayload()
        {
            return ProtocolSerialization.WritePayload(writer =>
            {
                writer.Write(PlayerId);
                ProtocolSerialization.WriteString(writer, Username);
                writer.Write(PickupId);
                writer.Write((byte)Type);
            });
        }

        public static PickupTaken FromPayload(byte[] payload)
        {
            return ProtocolSerialization.ReadPayload(payload, reader => new PickupTaken
            {
                PlayerId = reader.ReadByte(),
                Username = ProtocolSerialization.ReadString(reader),
                PickupId = reader.ReadByte(),
                Type = (PickupType)reader.ReadByte(),
            });
        }
    }
}
