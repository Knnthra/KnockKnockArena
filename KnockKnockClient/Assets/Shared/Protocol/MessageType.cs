namespace KnockKnockArena.Shared.Protocol
{
    /// <summary>
    /// TCP message types. The byte value goes on the wire right after the length prefix.
    /// Convention: 0-99 are client-to-server, 100-199 are server-to-client.
    /// UDP packet types will live in a separate enum (they are a different channel).
    /// </summary>
    public enum MessageType : byte
    {
        // Client -> server
        LoginRequest = 0,
        ChatSend = 1,

        // Server -> client
        LoginResponse = 100,
        ServerInfo = 101,
        ChatBroadcast = 102,
        PlayerJoined = 103,
        PlayerLeft = 104,
        PlayerDied = 105,
        PickupTaken = 106,
        RocketExploded = 107,
        PlayerDamaged = 108,
    }
}
