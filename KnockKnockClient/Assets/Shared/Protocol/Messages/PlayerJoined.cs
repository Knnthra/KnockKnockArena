namespace KnockKnockArena.Shared.Protocol.Messages
{
    public sealed class PlayerJoined
    {
        public byte PlayerId;
        private string Username = "";

        public byte[] ToPayload()
        {
            return ProtocolSerialization.WritePayload(writer =>
            {
                writer.Write(PlayerId);
                ProtocolSerialization.WriteString(writer, Username);
            });
        }

        public static PlayerJoined FromPayload(byte[] payload)
        {
            return ProtocolSerialization.ReadPayload(payload, reader => new PlayerJoined
            {
                PlayerId = reader.ReadByte(),
                Username = ProtocolSerialization.ReadString(reader),
            });
        }
    }
}