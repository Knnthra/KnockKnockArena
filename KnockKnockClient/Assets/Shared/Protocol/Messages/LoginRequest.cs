namespace KnockKnockArena.Shared.Protocol.Messages
{
    public sealed class LoginRequest
    {
        public string Username = "";
        public string Password = "";

        public byte[] ToPayload()
        {
            return ProtocolSerialization.WritePayload(writer =>
            {
                ProtocolSerialization.WriteString(writer, Username);
                ProtocolSerialization.WriteString(writer, Password);
            });
        }

        public static LoginRequest FromPayload(byte[] payload)
        {
            return ProtocolSerialization.ReadPayload(payload, reader => new LoginRequest
            {
                Username = ProtocolSerialization.ReadString(reader),
                Password = ProtocolSerialization.ReadString(reader),
            });
        }
    }
}