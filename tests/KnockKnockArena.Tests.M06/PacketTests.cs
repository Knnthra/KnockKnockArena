using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Tests.M06;

/// <summary>
/// Module 6: the two game packets, byte for byte. The input goes 30 times a second
/// from every client, the snapshot 30 times a second to every client - both are read
/// blindly field by field, so every field must sit where the other side expects it.
/// </summary>
public class PacketTests
{
    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes);
    private static byte[] Bytes(string dump) => dump.Split('-').Select(h => Convert.ToByte(h, 16)).ToArray();

    [Theory]
    [InlineData(UdpPacketType.Input, 1)]
    [InlineData(UdpPacketType.Snapshot, 100)]
    public void UdpPacketType_HasTheProtocolsNumbers(UdpPacketType type, byte value)
    {
        Assert.Equal(value, (byte)type);
    }

    // ------------------------------------------------------------------ InputPacket

    private static InputPacket SampleInput() => new()
    {
        PlayerId = 3,
        InputIndex = 258,
        Tick = 0x1234,
        Movement = MovementBits.W | MovementBits.Sprint,
        AimX = 1.5f,
        AimZ = -2f,
    };

    // 01 06 | id 03 | index 02 01 00 00 | tick 34 12 00 00 | movement 21 |
    // aimX 00 00 C0 3F | aimZ 00 00 00 C0 | buttons 00 | weapon 00
    private const string InputDump = "01-06-03-02-01-00-00-34-12-00-00-21-00-00-C0-3F-00-00-00-C0-00-00";

    [Fact]
    public void Input_Is22Bytes_FieldsInOrder()
    {
        byte[] datagram = SampleInput().ToDatagram();

        Assert.Equal(22, datagram.Length);
        Assert.Equal(InputDump, Hex(datagram));
    }

    [Fact]
    public void Input_RoundTrips()
    {
        InputPacket back = InputPacket.FromDatagram(Bytes(InputDump));

        Assert.Equal(3, back.PlayerId);
        Assert.Equal(258u, back.InputIndex);
        Assert.Equal(0x1234u, back.Tick);
        Assert.Equal(MovementBits.W | MovementBits.Sprint, back.Movement);
        Assert.Equal(1.5f, back.AimX);
        Assert.Equal(-2f, back.AimZ);
    }

    [Fact]
    public void Input_Truncated_IsMalformed()
    {
        byte[] datagram = Bytes(InputDump).Take(15).ToArray(); // cut inside aimX
        Assert.Throws<ProtocolException>(() => InputPacket.FromDatagram(datagram));
    }

    // ------------------------------------------------------------------ SnapshotPacket

    private static PlayerEntityState Anna() => new()
    {
        EntityId = 1,
        X = 132f,
        Z = 134.5f,
        AimX = 140f,
        AimZ = 134.5f,
        Health = 100,
        State = PlayerLifeState.Alive,
        DashTicksLeft = 5,
        DashCooldownTicks = 139,
        DashDirectionX = 1f,
        DashDirectionZ = 0f,
    };

    [Fact]
    public void Snapshot_WithoutPlayers_Is15Bytes()
    {
        byte[] datagram = new SnapshotPacket { Sequence = 7, ServerTick = 9, LastProcessedInputIndex = 5 }.ToDatagram();

        // 64 06 | sequence 07 00 00 00 | tick 09 00 00 00 | lastProcessed 05 00 00 00 | count 00
        Assert.Equal("64-06-07-00-00-00-09-00-00-00-05-00-00-00-00", Hex(datagram));
    }

    [Fact]
    public void Snapshot_WithOnePlayer_Is15Plus39Bytes()
    {
        SnapshotPacket snapshot = new() { Sequence = 7, ServerTick = 9, LastProcessedInputIndex = 5 };
        snapshot.Players.Add(Anna());

        byte[] datagram = snapshot.ToDatagram();

        Assert.Equal(15 + PlayerEntityState.SerializedSize, datagram.Length);
        // The header, the entity count (1), then the entity: id 01 00, type 01, x = 132f.
        Assert.StartsWith("64-06-07-00-00-00-09-00-00-00-05-00-00-00-01-01-00-01-00-00-04-43", Hex(datagram));
    }

    [Fact]
    public void Snapshot_RoundTrips()
    {
        SnapshotPacket snapshot = new() { Sequence = 7, ServerTick = 9, LastProcessedInputIndex = 5 };
        snapshot.Players.Add(Anna());
        snapshot.Players.Add(new PlayerEntityState { EntityId = 2, X = 200f, Z = 210f, State = PlayerLifeState.Alive });

        SnapshotPacket back = SnapshotPacket.FromDatagram(snapshot.ToDatagram());

        Assert.Equal(7u, back.Sequence);
        Assert.Equal(9u, back.ServerTick);
        Assert.Equal(5u, back.LastProcessedInputIndex);
        Assert.Equal(2, back.Players.Count);
        Assert.Equal(1, back.Players[0].EntityId);
        Assert.Equal(132f, back.Players[0].X);
        Assert.Equal(134.5f, back.Players[0].Z);
        Assert.Equal(5, back.Players[0].DashTicksLeft);
        Assert.Equal(139, back.Players[0].DashCooldownTicks);
        Assert.Equal(1f, back.Players[0].DashDirectionX);
        Assert.Equal(2, back.Players[1].EntityId);
        Assert.Equal(210f, back.Players[1].Z);
    }

    [Fact]
    public void Snapshot_UnknownEntityType_IsRejected()
    {
        // count 1, entity id 01 00, entity type 99: entities have no length prefix, so
        // an unknown type cannot be skipped.
        byte[] datagram = Bytes("64-06-07-00-00-00-09-00-00-00-05-00-00-00-01-01-00-63-00-00");
        Assert.Throws<ProtocolException>(() => SnapshotPacket.FromDatagram(datagram));
    }

    [Fact]
    public void Snapshot_Truncated_IsMalformed()
    {
        SnapshotPacket snapshot = new() { Sequence = 7, ServerTick = 9, LastProcessedInputIndex = 5 };
        snapshot.Players.Add(Anna());
        byte[] datagram = snapshot.ToDatagram().Take(40).ToArray(); // cut inside the player

        Assert.Throws<ProtocolException>(() => SnapshotPacket.FromDatagram(datagram));
    }

    [Fact]
    public void Snapshot_FromAnotherVersion_IsRejected()
    {
        byte[] datagram = new SnapshotPacket { Sequence = 7 }.ToDatagram();
        datagram[1] = 5;

        Assert.Throws<ProtocolException>(() => SnapshotPacket.FromDatagram(datagram));
    }
}
