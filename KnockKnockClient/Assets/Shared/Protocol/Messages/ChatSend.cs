namespace KnockKnockArena.Shared.Protocol.Messages
{
    public sealed class ChatSend
    {
        public string Text = "";

        public byte[] ToPayload()
        {
            return ProtocolSerialization.WritePayload(writer =>
            {
                ProtocolSerialization.WriteString(writer, Text);
            });
        }

        public static ChatSend FromPayload(byte[] payload)
        {
            return ProtocolSerialization.ReadPayload(payload, reader => new ChatSend
            {
                Text = ProtocolSerialization.ReadString(reader),
            });
        }

    }
}