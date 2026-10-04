using KnockKnockArena.Server.Game;
using KnockKnockArena.Shared.Config;
using KnockKnockArena.Shared.Protocol.Udp;
using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Tests.M09;

/// <summary>
/// Module 9: data/pickups.json - how close a player's centre must get to each pickup
/// type. Types are matched by NAME, so an entry cannot land on the wrong type, and a
/// file with a mistake stops the server instead of running on half-read numbers.
/// </summary>
public class PickupTableTests
{
    private static string Table(string entries) => "{ \"pickups\": [ " + entries + " ] }";

    [Fact]
    public void TheAnswerKeysTable_IsTwoMetresForEveryType()
    {
        PickupTable table = PickupTable.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "pickups.json")));

        foreach (PickupType type in Enum.GetValues<PickupType>())
            Assert.Equal(2.0f, table.RadiusFor(type));
    }

    [Fact]
    public void OneType_GetsItsOwnRadius()
    {
        PickupTable table = PickupTable.Parse(Table("{ \"type\": \"Health\", \"radiusMeters\": 5.0 }"));

        Assert.Equal(5.0f, table.RadiusFor(PickupType.Health));
        Assert.Equal(GameConstants.PickupRadius, table.RadiusFor(PickupType.GlockAmmo)); // left out: the default
    }

    [Fact]
    public void Order_DoesNotMatter()
    {
        PickupTable table = PickupTable.Parse(Table(
            "{ \"type\": \"Health\", \"radiusMeters\": 3.0 }, { \"type\": \"Ak47Weapon\", \"radiusMeters\": 1.5 }"));

        Assert.Equal(1.5f, table.RadiusFor(PickupType.Ak47Weapon));
        Assert.Equal(3.0f, table.RadiusFor(PickupType.Health));
    }

    [Fact]
    public void Defaults_AreTheOldFixedRadius()
    {
        foreach (PickupType type in Enum.GetValues<PickupType>())
            Assert.Equal(GameConstants.PickupRadius, PickupTable.Defaults().RadiusFor(type));
    }

    [Fact]
    public void AnUnknownType_IsRejected()
    {
        Assert.Throws<JsonException>(() =>
            PickupTable.Parse(Table("{ \"type\": \"MegaHealth\", \"radiusMeters\": 2.0 }")));
    }

    [Fact]
    public void ATypeListedTwice_IsRejected()
    {
        Assert.Throws<JsonException>(() => PickupTable.Parse(Table(
            "{ \"type\": \"Health\", \"radiusMeters\": 2.0 }, { \"type\": \"Health\", \"radiusMeters\": 4.0 }")));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1.5")]
    [InlineData("20.5")]
    public void ARadiusOutsideZeroToTwenty_IsRejected(string radius)
    {
        Assert.Throws<JsonException>(() =>
            PickupTable.Parse(Table("{ \"type\": \"Health\", \"radiusMeters\": " + radius + " }")));
    }

    [Fact]
    public void TwentyMetres_IsTheLargestAllowed()
    {
        Assert.Equal(20f, PickupTable.Parse(Table("{ \"type\": \"Health\", \"radiusMeters\": 20 }")).RadiusFor(PickupType.Health));
    }
}
