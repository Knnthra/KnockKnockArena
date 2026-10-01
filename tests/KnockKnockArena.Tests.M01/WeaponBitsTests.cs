using KnockKnockArena.Shared.Protocol.Udp;

namespace KnockKnockArena.Tests.M01;

/// <summary>
/// Module 1: the owned-weapons bitfield. One byte, bit N = weapon id N
/// (Punch 0, Glock 1, Ak47 2, RocketLauncher 3). Your WeaponBits must pass all of these.
///
/// Many tests run over EVERY byte value (0..255): a helper that only works for the
/// values you happened to try is exactly the bug these tests exist to catch.
/// </summary>
public class WeaponBitsTests
{
    public static IEnumerable<object[]> AllWeapons() =>
        Enum.GetValues<WeaponId>().Select(weapon => new object[] { weapon });

    private static IEnumerable<byte> AllBytes() => Enumerable.Range(0, 256).Select(value => (byte)value);

    private static byte Mask(WeaponId weapon) => (byte)(1 << (int)weapon);

    // ------------------------------------------------------------------ StartingWeapons

    [Fact]
    public void StartingWeapons_IsPunchAndGlock()
    {
        Assert.Equal(0b0000_0011, WeaponBits.StartingWeapons);
    }

    // ------------------------------------------------------------------ HasWeapon

    [Theory]
    [MemberData(nameof(AllWeapons))]
    public void HasWeapon_IsFalse_WhenNothingIsOwned(WeaponId weapon)
    {
        Assert.False(WeaponBits.HasWeapon(0, weapon));
    }

    [Theory]
    [MemberData(nameof(AllWeapons))]
    public void HasWeapon_IsTrue_WhenEverythingIsOwned(WeaponId weapon)
    {
        Assert.True(WeaponBits.HasWeapon(0b1111_1111, weapon));
    }

    [Fact]
    public void HasWeapon_ReadsTheStartingWeapons()
    {
        Assert.True(WeaponBits.HasWeapon(WeaponBits.StartingWeapons, WeaponId.Punch));
        Assert.True(WeaponBits.HasWeapon(WeaponBits.StartingWeapons, WeaponId.Glock));
        Assert.False(WeaponBits.HasWeapon(WeaponBits.StartingWeapons, WeaponId.Ak47));
        Assert.False(WeaponBits.HasWeapon(WeaponBits.StartingWeapons, WeaponId.RocketLauncher));
    }

    [Theory]
    [MemberData(nameof(AllWeapons))]
    public void HasWeapon_LooksAtItsOwnBitOnly_ForEveryByte(WeaponId weapon)
    {
        foreach (byte owned in AllBytes())
        {
            bool expected = (owned & Mask(weapon)) != 0;
            Assert.True(expected == WeaponBits.HasWeapon(owned, weapon),
                $"HasWeapon({ToBinary(owned)}, {weapon}) should be {expected}");
        }
    }

    // ------------------------------------------------------------------ AddWeapon

    [Theory]
    [MemberData(nameof(AllWeapons))]
    public void AddWeapon_SetsTheWeaponsBit(WeaponId weapon)
    {
        Assert.Equal(Mask(weapon), WeaponBits.AddWeapon(0, weapon));
    }

    [Fact]
    public void AddWeapon_Ak47ToTheStartingWeapons_Gives7()
    {
        Assert.Equal(0b0000_0111, WeaponBits.AddWeapon(WeaponBits.StartingWeapons, WeaponId.Ak47));
    }

    [Theory]
    [MemberData(nameof(AllWeapons))]
    public void AddWeapon_IsIdempotent_ForEveryByte(WeaponId weapon)
    {
        foreach (byte owned in AllBytes())
        {
            byte once = WeaponBits.AddWeapon(owned, weapon);
            byte twice = WeaponBits.AddWeapon(once, weapon);
            Assert.True(once == twice, $"adding {weapon} twice to {ToBinary(owned)} changed the byte again");
        }
    }

    [Theory]
    [MemberData(nameof(AllWeapons))]
    public void AddWeapon_LeavesEveryOtherBitAlone_ForEveryByte(WeaponId weapon)
    {
        foreach (byte owned in AllBytes())
        {
            byte expected = (byte)(owned | Mask(weapon));
            byte actual = WeaponBits.AddWeapon(owned, weapon);
            Assert.True(expected == actual,
                $"AddWeapon({ToBinary(owned)}, {weapon}) gave {ToBinary(actual)}, expected {ToBinary(expected)}");
        }
    }

    // ------------------------------------------------------------------ RemoveWeapon

    [Theory]
    [MemberData(nameof(AllWeapons))]
    public void RemoveWeapon_ClearsTheWeaponsBit(WeaponId weapon)
    {
        Assert.False(WeaponBits.HasWeapon(WeaponBits.RemoveWeapon(0b1111_1111, weapon), weapon));
    }

    [Theory]
    [MemberData(nameof(AllWeapons))]
    public void RemoveWeapon_OfAWeaponNotOwned_ChangesNothing(WeaponId weapon)
    {
        byte owned = (byte)(0b1111_1111 & ~Mask(weapon));
        Assert.Equal(owned, WeaponBits.RemoveWeapon(owned, weapon));
    }

    [Theory]
    [MemberData(nameof(AllWeapons))]
    public void RemoveWeapon_LeavesEveryOtherBitAlone_ForEveryByte(WeaponId weapon)
    {
        foreach (byte owned in AllBytes())
        {
            byte expected = (byte)(owned & ~Mask(weapon));
            byte actual = WeaponBits.RemoveWeapon(owned, weapon);
            Assert.True(expected == actual,
                $"RemoveWeapon({ToBinary(owned)}, {weapon}) gave {ToBinary(actual)}, expected {ToBinary(expected)} " +
                "(a toggle — XOR — fails here when the weapon was not owned)");
        }
    }

    [Theory]
    [MemberData(nameof(AllWeapons))]
    public void AddThenRemove_ReturnsToTheStart_WhenTheWeaponWasNotOwned(WeaponId weapon)
    {
        byte start = (byte)(WeaponBits.StartingWeapons & ~Mask(weapon));
        Assert.Equal(start, WeaponBits.RemoveWeapon(WeaponBits.AddWeapon(start, weapon), weapon));
    }

    // ------------------------------------------------------------------ ToBinaryString

    [Theory]
    [InlineData(0, "00000000")]
    [InlineData(3, "00000011")]
    [InlineData(7, "00000111")]
    [InlineData(15, "00001111")]
    [InlineData(128, "10000000")]
    [InlineData(255, "11111111")]
    public void ToBinaryString_WritesEightDigits_MostSignificantFirst(byte owned, string expected)
    {
        Assert.Equal(expected, WeaponBits.ToBinaryString(owned));
    }

    [Fact]
    public void ToBinaryString_IsAlwaysEightZerosAndOnes_ForEveryByte()
    {
        foreach (byte owned in AllBytes())
        {
            string text = WeaponBits.ToBinaryString(owned);
            Assert.True(text.Length == 8 && text.All(c => c is '0' or '1'),
                $"ToBinaryString({owned}) gave \"{text}\" — expected eight 0/1 digits");
            Assert.True(Convert.ToByte(text, 2) == owned, $"ToBinaryString({owned}) gave \"{text}\", which is not {owned}");
        }
    }

    // ------------------------------------------------------------------ one life, as in the demo

    [Fact]
    public void OneLife_SpawnPickUpDieRespawn()
    {
        byte owned = WeaponBits.StartingWeapons;
        owned = WeaponBits.AddWeapon(owned, WeaponId.Ak47);
        owned = WeaponBits.AddWeapon(owned, WeaponId.RocketLauncher);
        Assert.Equal("00001111", WeaponBits.ToBinaryString(owned));

        owned = WeaponBits.RemoveWeapon(owned, WeaponId.RocketLauncher);
        Assert.Equal("00000111", WeaponBits.ToBinaryString(owned));

        owned = WeaponBits.StartingWeapons; // death resets the whole field
        Assert.Equal("00000011", WeaponBits.ToBinaryString(owned));
    }

    private static string ToBinary(byte value) => Convert.ToString(value, 2).PadLeft(8, '0');
}
