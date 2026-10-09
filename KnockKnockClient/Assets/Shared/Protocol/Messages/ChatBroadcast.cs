namespace KnockKnockArena.Shared.Protocol.Messages
{
    public sealed class ChatBroadcast
    {
        public byte PlayerId;
        public string Username = "";
        public string Text = "";


        public byte[] ToPayload()
        {
            return ProtocolSerialization.WritePayload(writer =>
            {
                writer.Write(PlayerId);
                ProtocolSerialization.WriteString(writer, Username);
                ProtocolSerialization.WriteString(writer, Text);
            });
        }

        public static ChatBroadcast FromPayload(byte[] payload)
        {
            return ProtocolSerialization.ReadPayload(payload, reader => new ChatBroadcast
            {
                PlayerId = reader.ReadByte(),
                Username = ProtocolSerialization.ReadString(reader),
                Text = ProtocolSerialization.ReadString(reader),
            });
        }

    }
}