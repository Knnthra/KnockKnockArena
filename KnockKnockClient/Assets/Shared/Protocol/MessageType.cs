namespace KnockKnockArena.Shared.Protocol
{
    public enum MessageType : byte
    {
        LoginRequest = 0,
        ChatSend = 1,

        LoginResponse = 100,
        ServerInfo = 101,
        ChatBroadcast = 102,
        PlayerJoined = 103,
        PlayerLeft = 104,
    }
}