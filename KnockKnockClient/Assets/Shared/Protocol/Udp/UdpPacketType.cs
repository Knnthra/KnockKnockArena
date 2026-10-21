namespace KnockKnockArena.Shared.Protocol.Udp
{
    /// <summary>
    /// UDP packet types. UDP has real datagram boundaries, so there is no length
    /// framing: a datagram IS one packet. Layout: [type:byte] [version:byte] [fields].
    /// Convention (same as TCP): 0-99 client-to-server, 100-199 server-to-client.
    /// </summary>
    public enum UdpPacketType : byte
    {
        // Client -> server
        Join = 0,
        Input = 1,
        Heartbeat = 2,
        DiscoveryRequest = 3,   // new in protocol v6: LAN broadcast (see DiscoveryPackets)

        // Server -> client
        Snapshot = 100,
        DiscoveryResponse = 102, // new in protocol v6: answer to DiscoveryRequest
    }

    /// <summary>Movement keys held down, packed as bit flags (Video 1.5).</summary>
    [System.Flags]
    public enum MovementBits : byte
    {
        None = 0,
        W = 1 << 0,
        A = 1 << 1,
        S = 1 << 2,
        D = 1 << 3,

        /// <summary>Walk modifier (Ctrl held): move at WalkSpeed.</summary>
        Walk = 1 << 4,

        /// <summary>Sprint modifier (Shift held): SprintSpeed, but only while the
        /// net movement is forward (W without S) and Walk is not set.</summary>
        Sprint = 1 << 5,

        /// <summary>Dash (Space) — EDGE-triggered: the client sets this bit only on
        /// the tick where the key was pressed, not while it is held.</summary>
        Dash = 1 << 6,
    }

    /// <summary>Action buttons, packed as bit flags.</summary>
    [System.Flags]
    public enum ButtonBits : byte
    {
        None = 0,
        Fire = 1 << 0,
        SwitchWeapon = 1 << 1,
        Respawn = 1 << 2,
    }
}
