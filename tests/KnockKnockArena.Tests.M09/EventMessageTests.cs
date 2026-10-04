using System.Text;
using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Messages;

namespace KnockKnockArena.Tests.M09;

/// <summary>
/// Module 9: the three events that go on TCP, byte for byte. They must arrive - blood
/// where the shot landed, a line in the kill feed, an explosion - so they travel on the
/// reliable channel, framed like every other TCP message. The payload starts with the
/// protocol version.
/// </summary>
public class EventMessageTests
{
    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes);

    [Fact]
    public void MessageTypes_Are105_107_108()
    {
        Assert.Equal(105, (byte)MessageType.PlayerDied);
        Assert.Equal(107, (byte)MessageType.RocketExploded);
        Assert.Equal(108, (byte)MessageType.PlayerDamaged);
    }

    // version 06 | victim 02 | x 00 00 16 43 (150) | z 00 80 04 43 (132.5) | attacker 01 | Glock 01 | damage 0A
    private const string DamagedDump = "06-02-00-00-16-43-00-80-04-43-01-01-0A";

    [Fact]
    public void PlayerDamaged_Is13Bytes()
    {
        byte[] payload = new PlayerDamaged { VictimId = 2, X = 150f, Z = 132.5f, AttackerId = 1, WeaponId = 1, Damage = 10 }.ToPayload();

        Assert.Equal(13, payload.Length);
        Assert.Equal(DamagedDump, Hex(payload));
    }

    [Fact]
    public void PlayerDamaged_RoundTrips()
    {
        PlayerDamaged back = PlayerDamaged.FromPayload(new PlayerDamaged
        {
            VictimId = 7, X = 196.25f, Z = 188.5f, AttackerId = 7, WeaponId = 3, Damage = 38,
        }.ToPayload());

        Assert.Equal(7, back.VictimId);
        Assert.Equal(196.25f, back.X);
        Assert.Equal(188.5f, back.Z);
        Assert.Equal(7, back.AttackerId); // your own rocket: attacker = victim
        Assert.Equal(3, back.WeaponId);
        Assert.Equal(38, back.Damage);
    }

    [Fact]
    public void PlayerDamaged_Truncated_IsMalformed()
    {
        byte[] payload = new PlayerDamaged { VictimId = 2, X = 150f, Z = 132.5f, AttackerId = 1, WeaponId = 1, Damage = 10 }.ToPayload();

        Assert.Throws<ProtocolException>(() => PlayerDamaged.FromPayload(payload.Take(12).ToArray()));
    }

    // version 06 | killer 01 | "bo" 02 00 62 6F | victim 02 | "anna" 04 00 61 6E 6E 61 | Glock 01
    private const string DiedDump = "06-01-02-00-62-6F-02-04-00-61-6E-6E-61-01";

    [Fact]
    public void PlayerDied_Is8BytesPlusBothNames()
    {
        byte[] payload = new PlayerDied { KillerId = 1, KillerUsername = "bo", VictimId = 2, VictimUsername = "anna", WeaponId = 1 }.ToPayload();

        Assert.Equal(8 + 2 + 4, payload.Length);
        Assert.Equal(DiedDump, Hex(payload));
    }

    [Fact]
    public void PlayerDied_NameLengthIsInBytes_NotCharacters()
    {
        byte[] payload = new PlayerDied { KillerId = 3, KillerUsername = "Åse", VictimId = 3, VictimUsername = "Åse", WeaponId = 3 }.ToPayload();

        // "Åse" is 3 characters but 4 UTF-8 bytes (Å = C3 85)
        Assert.Equal(4, Encoding.UTF8.GetByteCount("Åse"));
        Assert.Equal("06-03-04-00-C3-85-73-65-03-04-00-C3-85-73-65-03", Hex(payload));
    }

    [Fact]
    public void PlayerDied_RoundTrips()
    {
        PlayerDied back = PlayerDied.FromPayload(new PlayerDied
        {
            KillerId = 4, KillerUsername = "carl", VictimId = 9, VictimUsername = "Åse", WeaponId = 0,
        }.ToPayload());

        Assert.Equal(4, back.KillerId);
        Assert.Equal("carl", back.KillerUsername);
        Assert.Equal(9, back.VictimId);
        Assert.Equal("Åse", back.VictimUsername);
        Assert.Equal(0, back.WeaponId);
    }

    // version 06 | rocket E8 03 (1000) | x 00 80 44 43 (196.5) | z 00 00 3E 43 (190) | owner 02
    private const string ExplodedDump = "06-E8-03-00-80-44-43-00-00-3E-43-02";

    [Fact]
    public void RocketExploded_Is12Bytes()
    {
        byte[] payload = new RocketExploded { RocketEntityId = 1000, X = 196.5f, Z = 190f, OwnerId = 2 }.ToPayload();

        Assert.Equal(12, payload.Length);
        Assert.Equal(ExplodedDump, Hex(payload));
    }

    [Fact]
    public void RocketExploded_RoundTrips()
    {
        RocketExploded back = RocketExploded.FromPayload(new RocketExploded { RocketEntityId = 65535, X = 126f, Z = 270f, OwnerId = 12 }.ToPayload());

        Assert.Equal(65535, back.RocketEntityId);
        Assert.Equal(126f, back.X);
        Assert.Equal(270f, back.Z);
        Assert.Equal(12, back.OwnerId);
    }
}
