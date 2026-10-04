using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Tests.M08;

/// <summary>
/// Module 8: ShotsFired, byte for byte. Every shot the server resolved in a tick - hit or
/// miss - goes to everyone in one datagram, so the others can draw it: who fired, with
/// what, where the shot ended, and whether it ended in a player.
/// </summary>
public class ShotsFiredTests
{
    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes);
    private static byte[] Bytes(string dump) => dump.Split('-').Select(h => Convert.ToByte(h, 16)).ToArray();

    private static ShotsFiredPacket OneShot() => new()
    {
        ServerTick = 0x1234,
        Shots = { new ShotRecord { ShooterId = 2, Weapon = WeaponId.Glock, EndX = 150f, EndZ = 132.5f, HitPlayer = true } },
    };

    // 65 06 | tick 34 12 00 00 | count 01 | shooter 02 | Glock 01 | endX 00 00 16 43 | endZ 00 80 04 43 | hit 01
    private const string OneShotDump = "65-06-34-12-00-00-01-02-01-00-00-16-43-00-80-04-43-01";

    [Fact]
    public void UdpPacketType_ShotsFired_Is101()
    {
        Assert.Equal(101, (byte)UdpPacketType.ShotsFired);
    }

    [Fact]
    public void OneShot_Is7Plus11Bytes()
    {
        byte[] datagram = OneShot().ToDatagram();

        Assert.Equal(18, datagram.Length);
        Assert.Equal(OneShotDump, Hex(datagram));
    }

    [Fact]
    public void NoShots_IsTheHeaderAndACountOfZero()
    {
        Assert.Equal("65-06-09-00-00-00-00", Hex(new ShotsFiredPacket { ServerTick = 9 }.ToDatagram()));
    }

    [Fact]
    public void ShotsRoundTrip_HitAndMiss()
    {
        ShotsFiredPacket packet = OneShot();
        packet.Shots.Add(new ShotRecord { ShooterId = 5, Weapon = WeaponId.Ak47, EndX = 126f, EndZ = 200.25f, HitPlayer = false });

        ShotsFiredPacket back = ShotsFiredPacket.FromDatagram(packet.ToDatagram());

        Assert.Equal(0x1234u, back.ServerTick);
        Assert.Equal(2, back.Shots.Count);
        Assert.Equal(2, back.Shots[0].ShooterId);
        Assert.Equal(WeaponId.Glock, back.Shots[0].Weapon);
        Assert.Equal(150f, back.Shots[0].EndX);
        Assert.Equal(132.5f, back.Shots[0].EndZ);
        Assert.True(back.Shots[0].HitPlayer);
        Assert.Equal(WeaponId.Ak47, back.Shots[1].Weapon);
        Assert.Equal(200.25f, back.Shots[1].EndZ);
        Assert.False(back.Shots[1].HitPlayer);
    }

    [Fact]
    public void AnUnknownWeapon_IsRejected()
    {
        byte[] datagram = Bytes(OneShotDump);
        datagram[8] = 7; // the weapon byte

        Assert.Throws<ProtocolException>(() => ShotsFiredPacket.FromDatagram(datagram));
    }

    [Fact]
    public void ATruncatedShot_IsMalformed()
    {
        byte[] datagram = Bytes(OneShotDump).Take(14).ToArray(); // cut inside endZ

        Assert.Throws<ProtocolException>(() => ShotsFiredPacket.FromDatagram(datagram));
    }

    [Fact]
    public void AnotherVersion_IsRejected()
    {
        byte[] datagram = Bytes(OneShotDump);
        datagram[1] = 5;

        Assert.Throws<ProtocolException>(() => ShotsFiredPacket.FromDatagram(datagram));
    }
}
