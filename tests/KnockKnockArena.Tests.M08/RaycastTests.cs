using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Tests.M08;

/// <summary>ArenaMap and WeaponStats are static - one map, one table for the whole
/// process - so the tests that load them run one at a time in this collection.</summary>
[CollectionDefinition("Statics", DisableParallelization = true)]
public class StaticsCollection { }

/// <summary>
/// Module 8: the ray test behind every hitscan shot. A ray from the shooter's centre, a
/// direction of length 1, and a box (a wall, or a player's 2.3 m square): does the ray
/// enter the box, and how far away?
/// </summary>
public class RaycastTests
{
    // A player's box 10 m east of the origin: 10-12 on X, -1..1 on Z.
    private static bool Ray(float ox, float oz, float dx, float dz, out float distance) =>
        Raycast.RayIntersectsAabb(ox, oz, dx, dz, 10f, -1f, 12f, 1f, out distance);

    [Fact]
    public void StraightAtTheBox_HitsTheNearFace()
    {
        Assert.True(Ray(0f, 0f, 1f, 0f, out float distance));
        Assert.Equal(10f, distance, 4);
    }

    [Fact]
    public void PastTheBox_Misses()
    {
        Assert.False(Ray(0f, 0f, 0f, 1f, out _));
        Assert.False(Ray(0f, 5f, 1f, 0f, out _)); // parallel, beside the box
    }

    [Fact]
    public void ABoxBehindTheShooter_IsNotHit()
    {
        Assert.False(Ray(20f, 0f, 1f, 0f, out _));
    }

    [Fact]
    public void FromInsideTheBox_TheDistanceIsZero()
    {
        Assert.True(Ray(11f, 0f, 1f, 0f, out float distance));
        Assert.Equal(0f, distance);
    }

    [Fact]
    public void ADiagonal_EntersThroughTheCorner()
    {
        // From (9, -2) at 45 degrees: enters the box at (10, -1), sqrt(2) away.
        Assert.True(Ray(9f, -2f, 0.70710678f, 0.70710678f, out float distance));
        Assert.Equal(MathF.Sqrt(2f), distance, 3);
    }

    [Fact]
    public void AGrazingRayJustBeside_Misses()
    {
        Assert.False(Ray(0f, 1.01f, 1f, 0f, out _));
    }
}

/// <summary>ArenaHitDistance: where a shot that hits nobody stops - the first wall, or
/// the arena's edge. On a test map: 100 x 100 m, one wall at 40-44.</summary>
[Collection("Statics")]
public class ArenaHitDistanceTests
{
    private const string TestMap = """
        {
          "name": "test",
          "bounds": { "minX": 0.0, "maxX": 100.0, "minZ": 0.0, "maxZ": 100.0 },
          "walls": [ { "minX": 40.0, "minZ": 40.0, "maxX": 44.0, "maxZ": 44.0 } ],
          "spawnPoints": [ { "x": 10.0, "z": 10.0 } ],
          "pickups": []
        }
        """;

    public ArenaHitDistanceTests() => ArenaMap.Load(ArenaMapData.Parse(TestMap));

    [Fact]
    public void NoWallInTheWay_StopsAtTheEdge()
    {
        Assert.Equal(90f, Raycast.ArenaHitDistance(10f, 10f, 1f, 0f), 4);
        Assert.Equal(10f, Raycast.ArenaHitDistance(10f, 10f, 0f, -1f), 4);
    }

    [Fact]
    public void AWallInTheWay_StopsAtTheWall()
    {
        Assert.Equal(30f, Raycast.ArenaHitDistance(10f, 42f, 1f, 0f), 4);
        Assert.Equal(46f, Raycast.ArenaHitDistance(90f, 42f, -1f, 0f), 4);
    }
}
