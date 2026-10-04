using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Tests.M09;

/// <summary>
/// Module 9: the rocket joins the snapshot as its own entity type. A rocket flies for a
/// second or two, so every client draws it from every snapshot - interpolated like a
/// player - and the entity list is players, then rockets, then pickups.
/// </summary>
public class SnapshotRocketTests
{
    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes);

    private static SnapshotPacket Header() => new() { Sequence = 7, ServerTick = 9, LastProcessedInputIndex = 5 };

    private static RocketEntityState Rocket() => new()
    {
        EntityId = 1000, X = 150f, Z = 132.5f, DirectionX = 1f, DirectionZ = 0f, OwnerId = 1,
    };

    [Fact]
    public void EntityType_Rocket_Is2()
    {
        Assert.Equal(2, (byte)EntityType.Rocket);
    }

    [Fact]
    public void OneRocket_Is15Plus20Bytes()
    {
        SnapshotPacket snapshot = Header();
        snapshot.Rockets.Add(Rocket());

        // header, count 01 | id E8 03 (1000) | type 02 | x 00 00 16 43 | z 00 80 04 43
        // | dirX 00 00 80 3F (1.0) | dirZ 00 00 00 00 | owner 01
        Assert.Equal("64-06-07-00-00-00-09-00-00-00-05-00-00-00-01-" +
                     "E8-03-02-00-00-16-43-00-80-04-43-00-00-80-3F-00-00-00-00-01",
            Hex(snapshot.ToDatagram()));
    }

    [Fact]
    public void Entities_ArePlayersThenRocketsThenPickups()
    {
        SnapshotPacket snapshot = Header();
        snapshot.Pickups.Add(new PickupEntityState { EntityId = 105, Type = PickupType.RocketLauncherWeapon, Active = true });
        snapshot.Rockets.Add(Rocket());
        snapshot.Players.Add(new PlayerEntityState { EntityId = 1, X = 132f, Z = 132f });

        byte[] datagram = snapshot.ToDatagram();

        Assert.Equal(15 + PlayerEntityState.SerializedSize + 20 + 5, datagram.Length);
        Assert.Equal(3, datagram[14]);                                    // one count for all three kinds
        Assert.Equal(1, datagram[17]);                                    // player first
        Assert.Equal(2, datagram[15 + PlayerEntityState.SerializedSize + 2]);      // then the rocket
        Assert.Equal(3, datagram[15 + PlayerEntityState.SerializedSize + 20 + 2]); // then the pickup
    }

    [Fact]
    public void Rockets_RoundTrip()
    {
        SnapshotPacket snapshot = Header();
        snapshot.Rockets.Add(Rocket());
        snapshot.Rockets.Add(new RocketEntityState { EntityId = 1001, X = 200.5f, Z = 190.25f, DirectionX = -0.6f, DirectionZ = 0.8f, OwnerId = 4 });
        snapshot.Players.Add(new PlayerEntityState { EntityId = 4, X = 196f, Z = 196f });

        SnapshotPacket back = SnapshotPacket.FromDatagram(snapshot.ToDatagram());

        Assert.Single(back.Players);
        Assert.Equal(2, back.Rockets.Count);
        Assert.Equal(1000, back.Rockets[0].EntityId);
        Assert.Equal(150f, back.Rockets[0].X);
        Assert.Equal(132.5f, back.Rockets[0].Z);
        Assert.Equal(1f, back.Rockets[0].DirectionX);
        Assert.Equal(1, back.Rockets[0].OwnerId);
        Assert.Equal(1001, back.Rockets[1].EntityId);
        Assert.Equal(-0.6f, back.Rockets[1].DirectionX);
        Assert.Equal(0.8f, back.Rockets[1].DirectionZ);
        Assert.Equal(4, back.Rockets[1].OwnerId);
    }

    [Fact]
    public void AnUnknownEntityType_IsStillRejected()
    {
        SnapshotPacket snapshot = Header();
        snapshot.Rockets.Add(Rocket());
        byte[] datagram = snapshot.ToDatagram();
        datagram[17] = 4; // the rocket's entity type byte

        Assert.Throws<ProtocolException>(() => SnapshotPacket.FromDatagram(datagram));
    }

    [Fact]
    public void ATruncatedRocket_IsMalformed()
    {
        SnapshotPacket snapshot = Header();
        snapshot.Rockets.Add(Rocket());
        byte[] datagram = snapshot.ToDatagram().Take(15 + 19).ToArray(); // the owner byte is missing

        Assert.Throws<ProtocolException>(() => SnapshotPacket.FromDatagram(datagram));
    }
}
