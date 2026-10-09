namespace KnockKnockArena.Shared.Protocol.Messages
{
    public sealed class LoginResponse
    {
        public bool Success;
        public string Error = "";
        public byte PlayerId;
        public byte[] SessionToken = new byte[ProtocolConstants.SessionTokenLength];
        public byte[] ToPayload()
        {
            return ProtocolSerialization.WritePayload(writer =>
            {
                writer.Write(Success);
                ProtocolSerialization.WriteString(writer, Error);
                writer.Write(PlayerId);
                writer.Write(SessionToken);
            });
        }

        public static LoginResponse FromPayload(byte[] payload)
        {
            return ProtocolSerialization.ReadPayload(payload, reader => new LoginResponse
            {
                Success = reader.ReadBoolean(),
                Error = ProtocolSerialization.ReadString(reader),
                PlayerId = reader.ReadByte(),
                SessionToken = reader.ReadBytes(ProtocolConstants.SessionTokenLength),
            });
        }
    }
}