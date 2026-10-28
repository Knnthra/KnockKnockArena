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

    /// <summary>
    /// Server -> all clients. A rocket detonated (play the explosion effect here —
    /// the entity also disappears from the snapshot).
    /// Payload: [version:byte] [rocketEntityId:ushort] [x:float] [z:float] [ownerId:byte]
    /// </summary>
    public sealed class RocketExploded
    {
        public ushort RocketEntityId;
        public float X;
        public float Z;
        public byte OwnerId;

        public byte[] ToPayload()
        {
            return ProtocolSerialization.WritePayload(writer =>
            {
                writer.Write(RocketEntityId);
                writer.Write(X);
                writer.Write(Z);
                writer.Write(OwnerId);
            });
        }

        public static RocketExploded FromPayload(byte[] payload)
        {
            return ProtocolSerialization.ReadPayload(payload, reader => new RocketExploded
            {
                RocketEntityId = reader.ReadUInt16(),
                X = reader.ReadSingle(),
                Z = reader.ReadSingle(),
                OwnerId = reader.ReadByte(),
            });
        }
    }
}
