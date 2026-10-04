using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Tests.M07;

/// <summary>
/// Module 7: pickups are entities in the snapshot. Five bytes each - id, type 3, the
/// pickup type and whether it is there to take. The position is NOT sent: it is in the
/// map both sides already have.
/// </summary>
public class SnapshotPickupTests
{
    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes);
    private static byte[] Bytes(string dump) => dump.Split('-').Select(h => Convert.ToByte(h, 16)).ToArray();

    private static SnapshotPacket Header() => new() { Sequence = 7, ServerTick = 9, LastProcessedInputIndex = 5 };

    [Fact]
    public void EntityType_Pickup_Is3()
    {
        Assert.Equal(3, (byte)EntityType.Pickup);
    }

    [Fact]
    public void OnePickup_Is15Plus5Bytes()
    {
        SnapshotPacket snapshot = Header();
        snapshot.Pickups.Add(new PickupEntityState { EntityId = 112, Type = PickupType.Ak47Weapon, Active = true });

        // header, count 01 | id 70 00 (112) | type 03 | Ak47Weapon 00 | active 01
        Assert.Equal("64-06-07-00-00-00-09-00-00-00-05-00-00-00-01-70-00-03-00-01", Hex(snapshot.ToDatagram()));
    }

    [Fact]
    public void ATakenPickup_IsActive00()
    {
        SnapshotPacket snapshot = Header();
        snapshot.Pickups.Add(new PickupEntityState { EntityId = 101, Type = PickupType.GlockAmmo, Active = false });

        Assert.EndsWith("01-65-00-03-02-00", Hex(snapshot.ToDatagram()));
    }

    [Fact]
    public void PlayersAndPickups_CountTogether()
    {
        SnapshotPacket snapshot = Header();
        snapshot.Players.Add(new PlayerEntityState { EntityId = 1, X = 132f, Z = 132f, Health = 100 });
        for (int i = 0; i < 13; i++)
            snapshot.Pickups.Add(new PickupEntityState { EntityId = (ushort)(100 + i), Type = PickupType.Health, Active = true });

        byte[] datagram = snapshot.ToDatagram();

        Assert.Equal(15 + PlayerEntityState.SerializedSize + 13 * 5, datagram.Length);
        Assert.Equal(14, datagram[14]); // one entity count for both kinds
    }

    [Fact]
    public void PlayersAndPickups_RoundTrip()
    {
        SnapshotPacket snapshot = Header();
        snapshot.Players.Add(new PlayerEntityState { EntityId = 1, X = 132f, Z = 134f, Health = 100 });
        snapshot.Players.Add(new PlayerEntityState { EntityId = 2, X = 140f, Z = 150f, Health = 100 });
        snapshot.Pickups.Add(new PickupEntityState { EntityId = 100, Type = PickupType.Ak47Weapon, Active = true });
        snapshot.Pickups.Add(new PickupEntityState { EntityId = 105, Type = PickupType.RocketLauncherWeapon, Active = false });
        snapshot.Pickups.Add(new PickupEntityState { EntityId = 112, Type = PickupType.Health, Active = true });

        SnapshotPacket back = SnapshotPacket.FromDatagram(snapshot.ToDatagram());

        Assert.Equal(2, back.Players.Count);
        Assert.Equal(150f, back.Players[1].Z);
        Assert.Equal(3, back.Pickups.Count);
        Assert.Equal(105, back.Pickups[1].EntityId);
        Assert.Equal(PickupType.RocketLauncherWeapon, back.Pickups[1].Type);
        Assert.False(back.Pickups[1].Active);
        Assert.True(back.Pickups[2].Active);
        Assert.Equal(PickupType.Health, back.Pickups[2].Type);
    }

    [Fact]
    public void APickupFirst_IsReadToo()
    {
        // The entity type decides what follows, not the order: a pickup before the
        // player is read just as well.
        byte[] player = new SnapshotPacket { Players = { new PlayerEntityState { EntityId = 1, X = 132f } } }
            .ToDatagram().Skip(15).ToArray();
        byte[] datagram = Bytes("64-06-07-00-00-00-09-00-00-00-05-00-00-00-02-64-00-03-05-01").Concat(player).ToArray();

        SnapshotPacket back = SnapshotPacket.FromDatagram(datagram);

        Assert.Single(back.Pickups);
        Assert.Single(back.Players);
        Assert.Equal(132f, back.Players[0].X);
    }

    [Fact]
    public void ATruncatedPickup_IsMalformed()
    {
        byte[] datagram = Bytes("64-06-07-00-00-00-09-00-00-00-05-00-00-00-01-70-00-03-00"); // no active byte

        Assert.Throws<ProtocolException>(() => SnapshotPacket.FromDatagram(datagram));
    }

    [Fact]
    public void AnUnknownEntityType_IsStillRejected()
    {
        byte[] datagram = Bytes("64-06-07-00-00-00-09-00-00-00-05-00-00-00-01-70-00-63-00-01");

        Assert.Throws<ProtocolException>(() => SnapshotPacket.FromDatagram(datagram));
    }
}
