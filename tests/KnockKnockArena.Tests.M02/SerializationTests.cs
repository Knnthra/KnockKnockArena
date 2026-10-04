using KnockKnockArena.Shared.Protocol;
using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Tests.M02;

/// <summary>
/// Module 2: binary serialization. Your PlayerEntityState and ProtocolSerialization
/// must produce EXACTLY the bytes the answer key produces — the server and every
/// client read these bytes blind, field by field, so "almost the same" is broken.
///
/// The expected dumps below were written by the answer key. Two players are used:
/// the demo player (the one from Demo.M02Serialization), and one where every field
/// has its own value, so two swapped fields cannot hide behind equal values.
/// </summary>
public class SerializationTests
{
    // The demo player: standing still at (132, 132) with a Glock, 24 rounds.
    private static PlayerEntityState DemoPlayer() => new()
    {
        EntityId = 1,
        X = 132f,
        Z = 132f,
        AimX = 142f,
        AimZ = 132f,
        Health = 100,
        ActiveWeapon = WeaponId.Glock,
        OwnedWeapons = 0b0000_0011,
        Ammo = 24,
        Frags = 0,
        Deaths = 0,
        State = PlayerLifeState.Alive,
    };

    private const string DemoPlayerDump =
        "06-01-00-01-00-00-04-43-00-00-04-43-00-00-0E-43-00-00-04-43-64-01-03-18-00-" +
        "00-00-00-00-00-00-00-00-00-00-00-00-00-00-00";

    // Every field different from every other: a swap, a missing field or a wrong
    // size shows up as a different dump.
    private static PlayerEntityState DistinctPlayer() => new()
    {
        EntityId = 0x0207,
        X = 1.5f,
        Z = -2.25f,
        AimX = 10f,
        AimZ = -10f,
        Health = 55,
        ActiveWeapon = WeaponId.Ak47,
        OwnedWeapons = 0b0000_0111,
        Ammo = 300,
        Frags = 2,
        Deaths = 1,
        State = PlayerLifeState.Dead,
        DashTicksLeft = 4,
        DashCooldownTicks = 9,
        DashDirectionX = 0.5f,
        DashDirectionZ = -0.75f,
    };

    private const string DistinctPlayerDump =
        "06-07-02-01-00-00-C0-3F-00-00-10-C0-00-00-20-41-00-00-20-C1-37-02-07-2C-01-" +
        "02-00-01-00-01-04-09-00-00-00-3F-00-00-40-BF";

    private static byte[] Serialize(PlayerEntityState player) =>
        ProtocolSerialization.WritePayload(writer => player.WriteTo(writer));

    private static byte[] Bytes(string dump) =>
        dump.Split('-').Select(hex => Convert.ToByte(hex, 16)).ToArray();

    private static string Hex(byte[] bytes, int offset, int count) =>
        BitConverter.ToString(bytes, offset, count);

    // ------------------------------------------------------------------ the version

    [Fact]
    public void Version_IsSix()
    {
        Assert.Equal(6, ProtocolConstants.Version);
    }

    // ------------------------------------------------------------------ the dump

    [Fact]
    public void DemoPlayer_IsFortyBytes_VersionByteFirst()
    {
        byte[] payload = Serialize(DemoPlayer());

        Assert.Equal(40, payload.Length);
        Assert.Equal(1 + PlayerEntityState.SerializedSize, payload.Length);
        Assert.Equal(ProtocolConstants.Version, payload[0]);
    }

    [Fact]
    public void DemoPlayer_IsByteIdenticalToTheAnswerKey()
    {
        Assert.Equal(DemoPlayerDump, BitConverter.ToString(Serialize(DemoPlayer())));
    }

    [Fact]
    public void DistinctPlayer_IsByteIdenticalToTheAnswerKey()
    {
        Assert.Equal(DistinctPlayerDump, BitConverter.ToString(Serialize(DistinctPlayer())));
    }

    [Fact]
    public void SerializedSize_Is39()
    {
        Assert.Equal(39, PlayerEntityState.SerializedSize);
    }

    [Fact]
    public void WriteTo_WritesExactlySerializedSizeBytes()
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream))
            DistinctPlayer().WriteTo(writer);

        Assert.Equal(PlayerEntityState.SerializedSize, stream.ToArray().Length);
    }

    // ------------------------------------------------------------------ field by field

    // Offsets are into the payload (version byte at 0). Each field is checked on its
    // own, so a failure names the field that is wrong.
    [Theory]
    [InlineData("entityId", 1, 2, "07-02")]           // 0x0207, low byte first
    [InlineData("entityType", 3, 1, "01")]            // EntityType.Player
    [InlineData("x", 4, 4, "00-00-C0-3F")]            // 1.5f = 0x3FC00000
    [InlineData("z", 8, 4, "00-00-10-C0")]            // -2.25f = 0xC0100000
    [InlineData("aimX", 12, 4, "00-00-20-41")]        // 10f
    [InlineData("aimZ", 16, 4, "00-00-20-C1")]        // -10f
    [InlineData("health", 20, 1, "37")]               // 55
    [InlineData("activeWeapon", 21, 1, "02")]         // Ak47
    [InlineData("ownedWeapons", 22, 1, "07")]         // the M01 bitfield
    [InlineData("ammo", 23, 2, "2C-01")]              // 300 = 0x012C
    [InlineData("frags", 25, 2, "02-00")]
    [InlineData("deaths", 27, 2, "01-00")]
    [InlineData("state", 29, 1, "01")]                // Dead
    [InlineData("dashTicksLeft", 30, 1, "04")]
    [InlineData("dashCooldownTicks", 31, 1, "09")]
    [InlineData("dashDirX", 32, 4, "00-00-00-3F")]    // 0.5f
    [InlineData("dashDirZ", 36, 4, "00-00-40-BF")]    // -0.75f
    public void EveryField_SitsAtItsOffset_LittleEndian(string field, int offset, int size, string expected)
    {
        byte[] payload = Serialize(DistinctPlayer());

        Assert.True(payload.Length >= offset + size, $"payload is only {payload.Length} bytes, {field} needs {offset + size}");
        Assert.True(expected == Hex(payload, offset, size),
            $"{field} at offset {offset}: got {Hex(payload, offset, size)}, expected {expected}");
    }

    [Fact]
    public void Float132_IsLittleEndian_00_00_04_43()
    {
        // 132.0f is 0x43040000; little-endian puts the lowest byte first.
        Assert.Equal("00-00-04-43", Hex(Serialize(DemoPlayer()), 4, 4));
    }

    // ------------------------------------------------------------------ reading back

    [Fact]
    public void RoundTrip_GivesBackEveryField()
    {
        PlayerEntityState sent = DistinctPlayer();
        PlayerEntityState back = ProtocolSerialization.ReadPayload(Serialize(sent), PlayerEntityState.ReadFrom);

        Assert.Equal(sent.EntityId, back.EntityId);
        Assert.Equal(sent.X, back.X);
        Assert.Equal(sent.Z, back.Z);
        Assert.Equal(sent.AimX, back.AimX);
        Assert.Equal(sent.AimZ, back.AimZ);
        Assert.Equal(sent.Health, back.Health);
        Assert.Equal(sent.ActiveWeapon, back.ActiveWeapon);
        Assert.Equal(sent.OwnedWeapons, back.OwnedWeapons);
        Assert.Equal(sent.Ammo, back.Ammo);
        Assert.Equal(sent.Frags, back.Frags);
        Assert.Equal(sent.Deaths, back.Deaths);
        Assert.Equal(sent.State, back.State);
        Assert.Equal(sent.DashTicksLeft, back.DashTicksLeft);
        Assert.Equal(sent.DashCooldownTicks, back.DashCooldownTicks);
        Assert.Equal(sent.DashDirectionX, back.DashDirectionX);
        Assert.Equal(sent.DashDirectionZ, back.DashDirectionZ);
    }

    [Fact]
    public void ReadFrom_ReadsTheAnswerKeyDump()
    {
        // Reading must not depend on your own writer: parse the answer key's bytes.
        PlayerEntityState back = ProtocolSerialization.ReadPayload(Bytes(DistinctPlayerDump), PlayerEntityState.ReadFrom);

        Assert.Equal(0x0207, back.EntityId);
        Assert.Equal(1.5f, back.X);
        Assert.Equal(-2.25f, back.Z);
        Assert.Equal(300, back.Ammo);
        Assert.Equal(PlayerLifeState.Dead, back.State);
        Assert.Equal(-0.75f, back.DashDirectionZ);
    }

    [Fact]
    public void ReadData_ReadsWhatFollowsTheHeader()
    {
        // The snapshot (M06) reads id and type itself, then hands the rest to ReadData.
        byte[] payload = Bytes(DistinctPlayerDump);
        using BinaryReader reader = new(new MemoryStream(payload, 4, payload.Length - 4));

        PlayerEntityState back = PlayerEntityState.ReadData(reader, entityId: 0x0207);

        Assert.Equal(0x0207, back.EntityId);
        Assert.Equal(1.5f, back.X);
        Assert.Equal(-0.75f, back.DashDirectionZ);
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
    }

    [Fact]
    public void ReadFrom_RejectsAnEntityThatIsNotAPlayer()
    {
        byte[] payload = Bytes(DemoPlayerDump);
        payload[3] = (byte)EntityType.Rocket;

        Assert.Throws<ProtocolException>(
            () => ProtocolSerialization.ReadPayload(payload, PlayerEntityState.ReadFrom));
    }

    // ------------------------------------------------------------------ the version byte

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(255)]
    public void ReadPayload_RejectsAnotherVersion(byte version)
    {
        byte[] payload = Bytes(DemoPlayerDump);
        payload[0] = version;

        ProtocolException error = Assert.Throws<ProtocolException>(
            () => ProtocolSerialization.ReadPayload(payload, PlayerEntityState.ReadFrom));
        Assert.False(error.IsFatal);
    }

    [Fact]
    public void ReadPayload_ChecksTheVersionBeforeReadingAField()
    {
        // A wrong version must be refused even when the rest would not parse.
        bool readerCalled = false;
        Assert.Throws<ProtocolException>(
            () => ProtocolSerialization.ReadPayload(new byte[] { 5 }, reader => { readerCalled = true; return 0; }));

        Assert.False(readerCalled);
    }

    // ------------------------------------------------------------------ short payloads

    public static IEnumerable<object[]> EveryTruncation() =>
        Enumerable.Range(0, 40).Select(length => new object[] { length });

    [Theory]
    [MemberData(nameof(EveryTruncation))]
    public void ReadPayload_TurnsATruncatedPayloadIntoMalformedPayload(int length)
    {
        byte[] payload = Bytes(DemoPlayerDump).Take(length).ToArray();

        ProtocolException error = Assert.Throws<ProtocolException>(
            () => ProtocolSerialization.ReadPayload(payload, PlayerEntityState.ReadFrom));
        Assert.False(error.IsFatal);
    }

    // ------------------------------------------------------------------ strings

    [Theory]
    [InlineData("test", "04-00-74-65-73-74")]
    [InlineData("hæk", "04-00-68-C3-A6-6B")]     // æ is two bytes in UTF-8: the length counts bytes
    [InlineData("", "00-00")]
    public void WriteString_IsUshortByteLengthThenUtf8(string text, string expected)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream))
            ProtocolSerialization.WriteString(writer, text);

        Assert.Equal(expected, BitConverter.ToString(stream.ToArray()));
    }

    [Theory]
    [InlineData("test")]
    [InlineData("hæk")]
    [InlineData("KnockKnock 🔫")]
    [InlineData("")]
    public void String_RoundTrips(string text)
    {
        byte[] payload = ProtocolSerialization.WritePayload(writer => ProtocolSerialization.WriteString(writer, text));

        Assert.Equal(text, ProtocolSerialization.ReadPayload(payload, ProtocolSerialization.ReadString));
    }

    [Fact]
    public void WriteString_Allows1024Bytes()
    {
        using BinaryWriter writer = new(new MemoryStream());
        ProtocolSerialization.WriteString(writer, new string('a', ProtocolConstants.MaxStringLength));
    }

    [Fact]
    public void WriteString_Refuses1025Bytes()
    {
        using BinaryWriter writer = new(new MemoryStream());
        Assert.Throws<ProtocolException>(
            () => ProtocolSerialization.WriteString(writer, new string('a', 1025)));
    }

    [Fact]
    public void WriteString_CountsBytesNotCharacters()
    {
        // 513 × æ is 513 characters but 1026 bytes: too long.
        using BinaryWriter writer = new(new MemoryStream());
        Assert.Throws<ProtocolException>(
            () => ProtocolSerialization.WriteString(writer, new string('æ', 513)));
    }

    [Fact]
    public void ReadString_RefusesALengthAbove1024()
    {
        // Length 0x0401 = 1025, no text after it: refused on the length alone.
        using BinaryReader reader = new(new MemoryStream(new byte[] { 0x01, 0x04 }));
        Assert.Throws<ProtocolException>(() => ProtocolSerialization.ReadString(reader));
    }

    [Fact]
    public void ReadString_RefusesAStringShorterThanItsLength()
    {
        // Says 4 bytes, carries 2.
        using BinaryReader reader = new(new MemoryStream(new byte[] { 0x04, 0x00, 0x68, 0x65 }));
        Assert.Throws<ProtocolException>(() => ProtocolSerialization.ReadString(reader));
    }
}
