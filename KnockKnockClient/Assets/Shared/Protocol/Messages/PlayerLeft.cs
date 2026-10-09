namespace KnockKnockArena.Shared.Protocol.Messages
{
    public sealed class PlayerLeft
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

        public static PlayerLeft FromPayload(byte[] payload)
        {
            return ProtocolSerialization.ReadPayload(payload, reader => new PlayerLeft
            {
                PlayerId = reader.ReadByte(),
                Username = ProtocolSerialization.ReadString(reader),
            });
        }
    }
}