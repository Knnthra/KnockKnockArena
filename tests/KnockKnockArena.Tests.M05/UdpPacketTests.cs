using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Tests.M05;

/// <summary>
/// Module 5: the UDP packets, byte for byte. There is no framing on UDP - a datagram
/// IS the packet - so the layout is [type:byte] [version:byte] [fields], and every
/// field must sit exactly where the server (and every other client) expects it.
/// </summary>
public class UdpPacketTests
{
    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes);
    private static byte[] Bytes(string dump) => dump.Split('-').Select(h => Convert.ToByte(h, 16)).ToArray();
    private static readonly byte[] Token = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

    // ------------------------------------------------------------------ the types

    [Theory]
    [InlineData(UdpPacketType.Join, 0)]
    [InlineData(UdpPacketType.Heartbeat, 2)]
    [InlineData(UdpPacketType.DiscoveryRequest, 3)]
    [InlineData(UdpPacketType.DiscoveryResponse, 102)]
    public void UdpPacketType_HasTheProtocolsNumbers(UdpPacketType type, byte value)
    {
        Assert.Equal(value, (byte)type);
    }

    // ------------------------------------------------------------------ UdpPacketIO

    [Fact]
    public void Build_WritesTypeThenVersionThenFields()
    {
        byte[] datagram = UdpPacketIO.Build(UdpPacketType.Heartbeat, writer => writer.Write((byte)9));
        Assert.Equal("02-06-09", Hex(datagram));
    }

    [Fact]
    public void PeekType_ReadsTheFirstByte()
    {
        Assert.Equal(UdpPacketType.DiscoveryRequest, UdpPacketIO.PeekType(Bytes("03-06-00-00-00-00")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PeekType_RefusesADatagramUnderTwoBytes(int length)
    {
        Assert.Throws<ProtocolException>(() => UdpPacketIO.PeekType(new byte[length]));
    }

    // ------------------------------------------------------------------ Join

    [Fact]
    public void Join_Is18Bytes_TypeVersionToken()
    {
        byte[] datagram = new JoinPacket { SessionToken = Token }.ToDatagram();

        Assert.Equal(18, datagram.Length);
        Assert.Equal("00-06-01-02-03-04-05-06-07-08-09-0A-0B-0C-0D-0E-0F-10", Hex(datagram));
    }

    [Fact]
    public void Join_RoundTrips()
    {
        JoinPacket back = JoinPacket.FromDatagram(new JoinPacket { SessionToken = Token }.ToDatagram());
        Assert.Equal(Token, back.SessionToken);
    }

    // ------------------------------------------------------------------ Heartbeat

    [Fact]
    public void Heartbeat_Is3Bytes_TypeVersionPlayerId()
    {
        Assert.Equal("02-06-07", Hex(new HeartbeatPacket { PlayerId = 7 }.ToDatagram()));
    }

    [Fact]
    public void Heartbeat_RoundTrips()
    {
        Assert.Equal(42, HeartbeatPacket.FromDatagram(Bytes("02-06-2A")).PlayerId);
    }

    // ------------------------------------------------------------------ Discovery

    [Fact]
    public void DiscoveryRequest_Is6Bytes_NonceLittleEndian()
    {
        byte[] datagram = new DiscoveryRequestPacket { Nonce = 0x12345678 }.ToDatagram();

        Assert.Equal(DiscoveryRequestPacket.Size, datagram.Length);
        Assert.Equal("03-06-78-56-34-12", Hex(datagram));
    }

    [Fact]
    public void DiscoveryRequest_FromAnotherVersion_IsStillRead()
    {
        // Discovery must work across versions: an old client should see this server
        // (marked incompatible) instead of nothing.
        DiscoveryRequestPacket request = DiscoveryRequestPacket.FromDatagramAnyVersion(Bytes("03-05-78-56-34-12"));

        Assert.Equal(5, request.ClientVersion);
        Assert.Equal(0x12345678u, request.Nonce);
    }

    [Fact]
    public void DiscoveryRequest_UnderSixBytes_IsMalformed()
    {
        Assert.Throws<ProtocolException>(
            () => DiscoveryRequestPacket.FromDatagramAnyVersion(Bytes("03-06-78-56-34")));
    }

    private static DiscoveryResponsePacket Alpha() => new()
    {
        Nonce = 0x12345678,
        GamePort = 36363,
        HttpPort = 0,
        PlayersOnline = 0,
        MaxPlayers = 12,
        ServerName = "Alpha",
        MapName = "",
    };

    // 66 06 | nonce 78 56 34 12 | gamePort 0B 8E | httpPort 00 00 | 00 0C | "Alpha" | ""
    private const string AlphaDump = "66-06-78-56-34-12-0B-8E-00-00-00-0C-05-00-41-6C-70-68-61-00-00";

    [Fact]
    public void DiscoveryResponse_IsTheAnswerKeysBytes()
    {
        byte[] datagram = Alpha().ToDatagram();

        Assert.Equal(21, datagram.Length);
        Assert.Equal(AlphaDump, Hex(datagram));
    }

    [Fact]
    public void DiscoveryResponse_RoundTrips()
    {
        DiscoveryResponsePacket back = DiscoveryResponsePacket.FromDatagram(Bytes(AlphaDump));

        Assert.Equal(0x12345678u, back.Nonce);
        Assert.Equal(36363, back.GamePort);
        Assert.Equal(0, back.HttpPort);
        Assert.Equal(0, back.PlayersOnline);
        Assert.Equal(12, back.MaxPlayers);
        Assert.Equal("Alpha", back.ServerName);
        Assert.Equal("", back.MapName);
    }

    [Fact]
    public void DiscoveryResponse_PeekVersion_ReadsTheSecondByte()
    {
        Assert.Equal(5, DiscoveryResponsePacket.PeekVersion(Bytes("66-05-00")));
    }

    // ------------------------------------------------------------------ short packets

    public static IEnumerable<object[]> ShortJoins() => Enumerable.Range(2, 16).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(ShortJoins))]
    public void Join_Truncated_IsMalformed(int length)
    {
        // Two bytes are enough for PeekType; anything short of 18 lacks token bytes.
        byte[] datagram = new JoinPacket { SessionToken = Token }.ToDatagram().Take(length).ToArray();

        Assert.Throws<ProtocolException>(() => JoinPacket.FromDatagram(datagram));
    }

    [Fact]
    public void Heartbeat_WithoutPlayerId_IsMalformed()
    {
        Assert.Throws<ProtocolException>(() => HeartbeatPacket.FromDatagram(Bytes("02-06")));
    }

    [Fact]
    public void DiscoveryResponse_Truncated_IsMalformed()
    {
        byte[] datagram = Bytes(AlphaDump).Take(15).ToArray(); // cut inside "Alpha"
        Assert.Throws<ProtocolException>(() => DiscoveryResponsePacket.FromDatagram(datagram));
    }

    // ------------------------------------------------------------------ the version byte

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(0)]
    public void Join_FromAnotherVersion_IsRejected(byte version)
    {
        byte[] datagram = new JoinPacket { SessionToken = Token }.ToDatagram();
        datagram[1] = version;

        Assert.Throws<ProtocolException>(() => JoinPacket.FromDatagram(datagram));
    }

    [Fact]
    public void Heartbeat_FromAnotherVersion_IsRejected()
    {
        Assert.Throws<ProtocolException>(() => HeartbeatPacket.FromDatagram(Bytes("02-05-07")));
    }

    [Fact]
    public void DiscoveryResponse_FromAnotherVersion_IsRejectedByFromDatagram()
    {
        byte[] datagram = Bytes(AlphaDump);
        datagram[1] = 5;

        Assert.Throws<ProtocolException>(() => DiscoveryResponsePacket.FromDatagram(datagram));
    }
}
