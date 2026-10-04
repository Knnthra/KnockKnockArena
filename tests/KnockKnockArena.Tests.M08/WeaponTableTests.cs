using KnockKnockArena.Shared.Config;
using KnockKnockArena.Shared.Protocol.Udp;
using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Tests.M08;

/// <summary>
/// Module 8: the weapon table as data, and the spread model on top of it. The server
/// rolls every shot's spread inside the cone the model gives; the client mirrors the same
/// numbers in its crosshair - so both must read the same table the same way.
/// </summary>
[Collection("Statics")]
public class WeaponTableTests
{
    private static readonly string AnswerKeyTable = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "weapons.json"));

    public WeaponTableTests() => WeaponStats.Load(WeaponStats.ParseTable(AnswerKeyTable));

    [Fact]
    public void TheTable_HasOneEntryPerWeaponId()
    {
        Assert.Equal(4, WeaponStats.ParseTable(AnswerKeyTable).Length);
    }

    [Fact]
    public void TheGlock_IsReadFromTheFile()
    {
        WeaponStats glock = WeaponStats.For(WeaponId.Glock);

        Assert.False(glock.IsMelee);
        Assert.Equal(10, glock.Damage);
        Assert.Equal(0.35f, glock.CooldownSeconds);
        Assert.Equal(1.0f, glock.BaseSpreadDeg);
        Assert.Equal(0.8f, glock.SpreadPerShotDeg);
        Assert.Equal(4.0f, glock.MaxSpreadDeg);
        Assert.Equal(6.0f, glock.SpreadRecoveryDegPerSec);
    }

    [Fact]
    public void ThePunch_IsMelee()
    {
        WeaponStats punch = WeaponStats.For(WeaponId.Punch);

        Assert.True(punch.IsMelee);
        Assert.Equal(1.8f, punch.MeleeRangeMeters);
        Assert.Equal(90f, punch.MeleeArcDeg);
    }

    [Fact]
    public void EntriesOutOfOrder_AreRefused()
    {
        string swapped = AnswerKeyTable.Replace("\"id\": \"Glock\"", "\"id\": \"X\"")
                                       .Replace("\"id\": \"Ak47\"", "\"id\": \"Glock\"")
                                       .Replace("\"id\": \"X\"", "\"id\": \"Ak47\"");

        Assert.Throws<JsonException>(() => WeaponStats.ParseTable(swapped));
    }

    [Fact]
    public void AMissingWeapon_IsRefused()
    {
        string three = """{ "weapons": [ { "id": "Punch", "melee": true, "damage": 5, "cooldownSeconds": 0.6, "priority": 0 } ] }""";

        Assert.Throws<JsonException>(() => WeaponStats.ParseTable(three));
    }

    // ------------------------------------------------------------------ the spread model

    [Fact]
    public void Spread_StartsAtTheBase()
    {
        Assert.Equal(1.0f, SpreadModel.CurrentSpreadDeg(WeaponId.Glock, 0f), 4);
    }

    [Fact]
    public void EachShot_AddsBloom()
    {
        float bloom = SpreadModel.OnShot(WeaponId.Glock, 0f);
        Assert.Equal(0.8f, bloom, 4);
        Assert.Equal(1.8f, SpreadModel.CurrentSpreadDeg(WeaponId.Glock, bloom), 4);
    }

    [Fact]
    public void Bloom_IsCappedAtTheMaximumSpread()
    {
        float bloom = 0f;
        for (int i = 0; i < 10; i++)
            bloom = SpreadModel.OnShot(WeaponId.Glock, bloom);

        Assert.Equal(3.0f, bloom, 4); // max 4.0 minus base 1.0
        Assert.Equal(4.0f, SpreadModel.CurrentSpreadDeg(WeaponId.Glock, bloom), 4);
    }

    [Fact]
    public void Bloom_RecoversOverTime_NeverBelowZero()
    {
        Assert.Equal(0.6f, SpreadModel.Decay(WeaponId.Glock, 0.8f, 1f / 30f), 4); // 6 deg/s for one tick
        Assert.Equal(0f, SpreadModel.Decay(WeaponId.Glock, 0.8f, 1f));
    }
}
