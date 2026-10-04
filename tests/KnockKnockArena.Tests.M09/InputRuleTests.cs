using System.Net;
using KnockKnockArena.Server.Auth;
using KnockKnockArena.Server.Game;
using KnockKnockArena.Shared.Protocol.Udp;
using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Tests.M09;

/// <summary>
/// ArenaMap and the weapon table are static for the whole process, so the tests that
/// load them run in this one collection - one at a time.
/// </summary>
[CollectionDefinition("GameWorld", DisableParallelization = true)]
public class GameWorldCollection { }

/// <summary>
/// The server's input rule (Modul 6): one input is one tick's step, so the tick loop
/// never applies more inputs than ticks have passed (plus a small reserve), applies them
/// in InputIndex order, waits a few ticks for one that was overtaken in the network,
/// and drops stale inputs and inputs that do not fit in the small queue. Run against
/// the real GameWorld, one Tick at a time - no network, no timing.
/// </summary>
[Collection("GameWorld")]
public class InputRuleTests
{
    // 100 x 100 m, no walls: nothing but the rule decides where the player ends up.
    private const string TestMap = """
        {
          "name": "test",
          "bounds": { "minX": 0.0, "maxX": 100.0, "minZ": 0.0, "maxZ": 100.0 },
          "walls": [],
          "spawnPoints": [ { "x": 10.0, "z": 50.0 } ],
          "pickups": []
        }
        """;

    private readonly GameWorld _world;
    private readonly PlayerState _player;

    public InputRuleTests()
    {
        ArenaMap.Load(ArenaMapData.Parse(TestMap));
        // Tick decays spread bloom, which reads the weapon table; the numbers do not
        // matter here - nobody fires.
        WeaponStats.Load(new[]
        {
            new WeaponStats(true, 5, 0.6f, 0, 0f, 0f, 0f, 0f, 1.8f, 90f),
            new WeaponStats(false, 10, 0.35f, 1, 1f, 0.8f, 4f, 6f, 0f, 0f),
            new WeaponStats(false, 12, 0.15f, 2, 1.5f, 1.2f, 10f, 8f, 0f, 0f),
            new WeaponStats(false, 100, 1.2f, 3, 0f, 0f, 0f, 0f, 0f, 0f),
        });
        _world = new GameWorld();
        _world.SpawnOrRebind(new PlayerSession(1, "anna", new byte[16]), new IPEndPoint(IPAddress.Loopback, 50000),
            out _player);
    }

    /// <summary>Forward (W), aiming due east: 0.2 m east per applied input.</summary>
    private InputPacket Input(uint index, MovementBits movement = MovementBits.W) => new()
    {
        PlayerId = 1,
        InputIndex = index,
        Movement = movement,
        AimX = _player.X + 30f,
        AimZ = 50f,
    };

    private void Enqueue(InputPacket input) => Assert.True(_world.TryEnqueueInput(_player, input, out _));

    private void Tick() => _world.Tick(new(), new(), new(), new(), new(), new());

    [Fact]
    public void SixtyInputsASecond_MoveSixMetresInOneSecond_NotTwelve()
    {
        uint index = 0;
        for (int tick = 0; tick < 30; tick++)
        {
            // Two inputs per tick: a client sending 60 a second. What does not fit is dropped.
            _world.TryEnqueueInput(_player, Input(++index), out _);
            _world.TryEnqueueInput(_player, Input(++index), out _);
            Tick();
        }

        // 30 ticks = 30 steps of 0.2 m, however many inputs arrived.
        Assert.InRange(_player.X - 10f, 5.99f, 6.01f);
    }

    [Fact]
    public void ReorderedInputs_AreAppliedInIndexOrder()
    {
        // #2 overtakes #1 in the network. #1 starts a dash, #2 walks - the order matters:
        // dash then walk continues the dash (0.625 m + 0.625 m), walk then dash does not.
        Enqueue(Input(2));
        Enqueue(Input(1, MovementBits.W | MovementBits.Dash));
        Tick();
        Assert.Equal(1u, _player.LastProcessedInputIndex);
        Tick();
        Assert.Equal(2u, _player.LastProcessedInputIndex);

        float x = 10f, z = 50f;
        MovementSimulation.DashState dash = default;
        MovementSimulation.Step(ref x, ref z, ref dash, MovementBits.W | MovementBits.Dash, 40f, 50f, GameConstants.TickDeltaTime);
        MovementSimulation.Step(ref x, ref z, ref dash, MovementBits.W, 40f, 50f, GameConstants.TickDeltaTime);
        Assert.Equal(BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(_player.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(z), BitConverter.SingleToInt32Bits(_player.Z));
    }

    [Fact]
    public void AMissingInput_IsWaitedFor_AndCaughtUpWhenItArrives()
    {
        Enqueue(Input(1));
        Tick();
        Enqueue(Input(3));
        Tick();
        Assert.Equal(1u, _player.LastProcessedInputIndex); // waits for #2 instead of skipping it

        Enqueue(Input(2));
        Tick();
        Assert.Equal(3u, _player.LastProcessedInputIndex); // the saved place: #2 and #3 on one tick
        Assert.InRange(_player.X - 10f, 0.59f, 0.61f);     // three steps, none lost
    }

    [Fact]
    public void AMissingInputThatNeverArrives_IsSkippedAfterThreeTicks_AndIsStaleIfItComesLater()
    {
        Enqueue(Input(1));
        Tick();
        Enqueue(Input(3));
        Tick();
        Tick();
        Tick();
        Assert.Equal(1u, _player.LastProcessedInputIndex);
        Tick();
        Assert.Equal(3u, _player.LastProcessedInputIndex);

        Assert.False(_world.TryEnqueueInput(_player, Input(2), out string reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void ADuplicateInput_IsDropped()
    {
        Enqueue(Input(1));
        Assert.False(_world.TryEnqueueInput(_player, Input(1), out string waiting));
        Assert.False(string.IsNullOrWhiteSpace(waiting));
        Tick();
        Assert.False(_world.TryEnqueueInput(_player, Input(1), out string applied));
        Assert.False(string.IsNullOrWhiteSpace(applied));
    }

    [Fact]
    public void TheQueue_HoldsEightInputs()
    {
        for (uint i = 1; i <= 8; i++)
            Enqueue(Input(i));
        Assert.False(_world.TryEnqueueInput(_player, Input(9), out string reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void TicksWithoutInput_SaveAtMostFourPlaces()
    {
        for (int i = 0; i < 10; i++)
            Tick();
        for (uint i = 1; i <= 8; i++)
            Enqueue(Input(i));

        Tick();
        Assert.Equal(4u, _player.LastProcessedInputIndex); // ten empty ticks saved only four places
        Tick();
        Assert.Equal(5u, _player.LastProcessedInputIndex); // then one per tick
    }

    [Fact]
    public void ADeadPlayersInputs_AreStillAcknowledged()
    {
        _player.LifeState = PlayerLifeState.Dead;
        _player.DeathTick = _world.ServerTick;
        Enqueue(Input(1));
        Tick();

        Assert.Equal(1u, _player.LastProcessedInputIndex);
        Assert.Equal(10f, _player.X); // acknowledged, not moved
    }
}
