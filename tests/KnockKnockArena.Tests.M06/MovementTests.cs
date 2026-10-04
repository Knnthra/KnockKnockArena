using KnockKnockArena.Shared.Protocol.Udp;
using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Tests.M06;

/// <summary>
/// ArenaMap is one static map for the whole process, so every test that loads a map
/// runs in this one collection - one at a time, never in parallel with each other.
/// </summary>
[CollectionDefinition("ArenaMap", DisableParallelization = true)]
public class ArenaMapCollection { }

/// <summary>
/// Module 6: the movement rule, on a small test map (100 x 100 m, one 4 x 4 m wall at
/// 40-44). These tests check what the rule DOES; the digest test checks the bits.
/// </summary>
[Collection("ArenaMap")]
public class MovementTests
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

    public MovementTests() => ArenaMap.Load(ArenaMapData.Parse(TestMap));

    /// <summary>Runs the rule for a number of ticks with a fixed input; the aim point
    /// is given RELATIVE to the player, as a direction the cursor keeps.</summary>
    private static (float X, float Z) Run(float x, float z, MovementBits movement, float aimDirX, float aimDirZ,
        int ticks, ref MovementSimulation.DashState dash)
    {
        for (int i = 0; i < ticks; i++)
            MovementSimulation.Step(ref x, ref z, ref dash, movement, x + aimDirX * 10f, z + aimDirZ * 10f,
                GameConstants.TickDeltaTime);
        return (x, z);
    }

    private static (float X, float Z) Run(float x, float z, MovementBits movement, float aimDirX, float aimDirZ, int ticks)
    {
        MovementSimulation.DashState dash = default;
        return Run(x, z, movement, aimDirX, aimDirZ, ticks, ref dash);
    }

    [Theory]
    [InlineData(MovementBits.W, 6f)]
    [InlineData(MovementBits.W | MovementBits.Walk, 3f)]
    [InlineData(MovementBits.W | MovementBits.Sprint, 9f)]
    [InlineData(MovementBits.W | MovementBits.Walk | MovementBits.Sprint, 3f)] // walk wins
    public void OneSecondForward_CoversTheSpeed(MovementBits movement, float metres)
    {
        (float x, float z) = Run(10f, 20f, movement, 1f, 0f, GameConstants.TickRate);

        Assert.Equal(10f + metres, x, 3);
        Assert.Equal(20f, z, 3);
    }

    [Fact]
    public void SprintBackwards_IsARun()
    {
        // Sprint only counts while the net movement is forward.
        (float x, _) = Run(50f, 20f, MovementBits.S | MovementBits.Sprint, 1f, 0f, GameConstants.TickRate);
        Assert.Equal(50f - 6f, x, 3);
    }

    [Fact]
    public void Diagonal_IsNotFaster()
    {
        (float x, float z) = Run(10f, 20f, MovementBits.W | MovementBits.D, 1f, 0f, GameConstants.TickRate);
        float distance = MathF.Sqrt((x - 10f) * (x - 10f) + (z - 20f) * (z - 20f));
        Assert.Equal(6f, distance, 3);
    }

    [Fact]
    public void Movement_IsCharacterRelative()
    {
        // Facing +Z (the aim is north): D strafes to the right, which is +X.
        (float x, float z) = Run(20f, 20f, MovementBits.D, 0f, 1f, GameConstants.TickRate);
        Assert.Equal(26f, x, 3);
        Assert.Equal(20f, z, 3);
    }

    [Fact]
    public void AimOnThePlayer_FallsBackToWorldAxes()
    {
        // Inside the dead zone the facing is undefined: W is simply +Z.
        float x = 20f, z = 20f;
        MovementSimulation.DashState dash = default;
        for (int i = 0; i < GameConstants.TickRate; i++)
            MovementSimulation.Step(ref x, ref z, ref dash, MovementBits.W, x, z, GameConstants.TickDeltaTime);

        Assert.Equal(20f, x, 3);
        Assert.Equal(26f, z, 3);
    }

    [Fact]
    public void Dash_Covers10MetresIn16Ticks_ThenCoolsDown()
    {
        MovementSimulation.DashState dash = default;
        (float x, _) = Run(10f, 20f, MovementBits.W | MovementBits.Dash, 1f, 0f, 1, ref dash);
        (x, _) = Run(x, 20f, MovementBits.None, 1f, 0f, GameConstants.DashDurationTicks - 1, ref dash);

        Assert.Equal(20f, x, 3);
        Assert.Equal(0, dash.TicksLeft);
        // The cooldown counts from the dash START: set on tick 1, down once per tick after.
        Assert.Equal(GameConstants.DashCooldownTicks - (GameConstants.DashDurationTicks - 1), dash.CooldownTicks);

        // A second dash during the cooldown is ignored: this is a normal run step.
        (float after, _) = Run(x, 20f, MovementBits.W | MovementBits.Dash, 1f, 0f, 1, ref dash);
        Assert.Equal(x + 0.2f, after, 3);
    }

    [Fact]
    public void Bounds_ClampWithThePlayersHalfExtent()
    {
        (float x, float z) = Run(2f, 50f, MovementBits.W, -1f, 0f, GameConstants.TickRate);
        Assert.Equal(ArenaMap.PlayerHalfExtent, x);
        Assert.Equal(50f, z, 3);
    }

    [Theory]
    [InlineData(38f, 42f, 1f, 0f, 40f - ArenaMap.PlayerHalfExtent, 42f)]   // walking east into the west face
    [InlineData(46f, 42f, -1f, 0f, 44f + ArenaMap.PlayerHalfExtent, 42f)]  // west into the east face
    [InlineData(42f, 38f, 0f, 1f, 42f, 40f - ArenaMap.PlayerHalfExtent)]   // north into the south face
    [InlineData(42f, 46f, 0f, -1f, 42f, 44f + ArenaMap.PlayerHalfExtent)]  // south into the north face
    public void Walls_PushOutAlongTheShortestAxis(float startX, float startZ, float dirX, float dirZ,
        float stopX, float stopZ)
    {
        (float x, float z) = Run(startX, startZ, MovementBits.W, dirX, dirZ, GameConstants.TickRate);
        Assert.Equal(stopX, x, 4);
        Assert.Equal(stopZ, z, 4);
    }

    [Fact]
    public void Walls_TieBreakIsWestEastSouthNorth()
    {
        // Dead centre of the wall: all four pushes are equal, and west wins.
        float x = 42f, z = 42f;
        MovementSimulation.ResolveCollisions(ref x, ref z);
        Assert.Equal(40f - ArenaMap.PlayerHalfExtent, x);
        Assert.Equal(42f, z);
    }

    [Fact]
    public void Walls_RunAlongAWallWithoutSticking()
    {
        // Pushed into the west face while moving north-east: the X stays on the face,
        // the Z keeps moving.
        (float x, float z) = Run(38f, 38f, MovementBits.W, 0.70710678f, 0.70710678f, 20);
        Assert.Equal(40f - ArenaMap.PlayerHalfExtent, x, 4);
        Assert.True(z > 40f, $"z = {z}");
    }

    [Fact]
    public void Collision_RunsWithoutInput()
    {
        // A player placed inside a wall (a respawn, a future knockback) is resolved too.
        float x = 40.5f, z = 42f;
        MovementSimulation.DashState dash = default;
        MovementSimulation.Step(ref x, ref z, ref dash, MovementBits.None, x, z, GameConstants.TickDeltaTime);
        Assert.Equal(40f - ArenaMap.PlayerHalfExtent, x);
    }
}
