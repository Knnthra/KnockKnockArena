using KnockKnockArena.Networking;

namespace KnockKnockArena.Tests.M07;

/// <summary>
/// Module 7: the interpolation buffer, sampled at fixed server ticks. It holds one
/// sample per server tick - position and aim - and answers "where was this player,
/// and where did it aim, at render tick T?" by blending the two samples around T.
/// </summary>
public class InterpolationBufferTests
{
    private static InterpolationBuffer Walk()
    {
        // Running east at 6 m/s: 0.2 m per tick, the aim point 10 m ahead and turning north.
        InterpolationBuffer buffer = new();
        buffer.Add(10, 100.0f, 50f, 110.0f, 50f);
        buffer.Add(11, 100.2f, 50f, 110.2f, 52f);
        buffer.Add(12, 100.4f, 50f, 110.4f, 54f);
        return buffer;
    }

    [Fact]
    public void Empty_HasNothing()
    {
        Assert.False(new InterpolationBuffer().TrySample(10, out _, out _, out _, out _));
        Assert.False(new InterpolationBuffer().TryLatest(out _, out _, out _, out _));
    }

    [Fact]
    public void Halfway_BlendsPositionAndAim()
    {
        Assert.True(Walk().TrySample(10.5, out float x, out float z, out float aimX, out float aimZ));

        Assert.Equal(100.1f, x, 4);
        Assert.Equal(50f, z, 4);
        Assert.Equal(110.1f, aimX, 4);
        Assert.Equal(51f, aimZ, 4);
    }

    [Fact]
    public void AQuarterIntoTheSecondSegment()
    {
        Walk().TrySample(11.25, out float x, out _, out _, out float aimZ);

        Assert.Equal(100.25f, x, 4);
        Assert.Equal(52.5f, aimZ, 4);
    }

    [Fact]
    public void OnATick_IsThatSample()
    {
        Walk().TrySample(11, out float x, out _, out _, out float aimZ);

        Assert.Equal(100.2f, x, 4);
        Assert.Equal(52f, aimZ, 4);
    }

    [Fact]
    public void BeforeTheFirstSample_ClampsToIt()
    {
        Walk().TrySample(3, out float x, out _, out _, out float aimZ);

        Assert.Equal(100f, x);
        Assert.Equal(50f, aimZ);
    }

    [Fact]
    public void PastTheNewestSample_ClampsInsteadOfGuessing()
    {
        // No extrapolation: the figure stands still rather than overshooting.
        Walk().TrySample(15, out float x, out _, out _, out float aimZ);

        Assert.Equal(100.4f, x);
        Assert.Equal(54f, aimZ);
    }

    [Fact]
    public void TryLatest_IsTheNewestSample_Unblended()
    {
        Assert.True(Walk().TryLatest(out float x, out float z, out float aimX, out float aimZ));

        Assert.Equal(100.4f, x);
        Assert.Equal(50f, z);
        Assert.Equal(110.4f, aimX);
        Assert.Equal(54f, aimZ);
    }

    [Fact]
    public void AnOlderTick_IsIgnored()
    {
        // The sequence check normally stops old snapshots; the buffer does not trust
        // that - a sample for an earlier (or the same) tick changes nothing.
        InterpolationBuffer buffer = Walk();
        buffer.Add(11, 999f, 999f, 999f, 999f);
        buffer.Add(12, 999f, 999f, 999f, 999f);

        buffer.TrySample(11.5, out float x, out float z, out _, out _);
        Assert.Equal(100.3f, x, 4);
        Assert.Equal(50f, z, 4);

        // ... and the newest sample is still tick 12's.
        buffer.TryLatest(out x, out _, out _, out _);
        Assert.Equal(100.4f, x);
    }

    [Fact]
    public void KeyedOnServerTicks_NotOnArrival()
    {
        // Jitter makes packets arrive unevenly; the buffer never sees arrival times.
        // A gap in the ticks (a lost snapshot) is still blended by tick: halfway
        // between tick 12 and tick 14 is tick 13.
        InterpolationBuffer buffer = Walk();
        buffer.Add(14, 100.8f, 50f, 110.8f, 58f);

        buffer.TrySample(13, out float x, out _, out _, out float aimZ);
        Assert.Equal(100.6f, x, 4);
        Assert.Equal(56f, aimZ, 4);
    }

    [Fact]
    public void AJumpOver3Metres_IsATeleport_NotAGlide()
    {
        InterpolationBuffer buffer = Walk();
        buffer.Add(13, 180f, 90f, 190f, 90f); // respawn on the other side of the map

        buffer.TrySample(12.1, out float x, out float z, out float aimX, out _);
        Assert.Equal(180f, x);
        Assert.Equal(90f, z);
        Assert.Equal(190f, aimX);
    }

    [Fact]
    public void TurningOnTheSpot_BlendsTheAim()
    {
        InterpolationBuffer buffer = new();
        buffer.Add(20, 100f, 50f, 110f, 50f);
        buffer.Add(21, 100f, 50f, 100f, 60f);

        buffer.TrySample(20.5, out float x, out _, out float aimX, out float aimZ);
        Assert.Equal(100f, x);
        Assert.Equal(105f, aimX, 4);
        Assert.Equal(55f, aimZ, 4);
    }

    [Fact]
    public void KeepsAtMost64Samples()
    {
        InterpolationBuffer buffer = new();
        for (uint tick = 1; tick <= 100; tick++)
            buffer.Add(tick, tick, 0f, tick, 0f);

        // The oldest kept sample is tick 37: sampling earlier clamps to it.
        buffer.TrySample(1, out float x, out _, out _, out _);
        Assert.Equal(37f, x);
    }
}
