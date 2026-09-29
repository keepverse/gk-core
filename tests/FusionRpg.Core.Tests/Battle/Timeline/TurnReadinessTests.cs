using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// B9 (battle-timeline-todo.md, spec-readiness-model.md): the pure readiness function. Matches this
/// program's own declared verify filter (<c>--filter ~Readiness</c>), which previously matched zero
/// tests (nothing under this name existed before P0.5).
/// </summary>
public class TurnReadinessTests
{
    [Fact]
    public void TheAuditsI1RegressionLockMidFlightHasteRebaseArrivesAtTPlusSevenFifty()
    {
        // The spec's own worked example, reproduced exactly: an actor half-way through a 1000-tick
        // wait at the default rate (speed 100, haste 1000 = normal) who then gains haste 500 (twice
        // as fast) arrives at t+750, NOT t+1000 -- proving work is stored, not time.
        const long speed = 100;
        const long startHaste = 1000;

        var startRate = TurnReadiness.EffectiveRate(speed, startHaste);
        Assert.Equal(100, startRate); // haste 1000 (normal) leaves speed unchanged

        var totalWork = FindWorkForTicks(startRate, targetTicks: 1000);
        var totalTicksAtStart = TurnReadiness.TicksFor(totalWork, startRate);
        Assert.Equal(1000, totalTicksAtStart);

        // Half-way: 500 ticks elapsed at the original rate. Work accrues at rate/SpeedScale per tick.
        const long elapsed = 500;
        var workDone = elapsed * startRate / TurnReadiness.SpeedScale;
        var remainingWork = totalWork - workDone;

        // Haste changes mid-flight: 1000 -> 500 (twice as fast). Rebase from the tick it changed at.
        var newRate = TurnReadiness.EffectiveRate(speed, haste: 500);
        Assert.Equal(200, newRate); // doubled, matching "twice as fast"

        var nextReadyTick = TurnReadiness.NextReadyTick(nowTick: elapsed, remainingWork, newRate);

        Assert.Equal(750, nextReadyTick);
    }

    static long FindWorkForTicks(long rate, long targetTicks) =>
        // Inverse of TicksFor: work = ticks * rate / SpeedScale, for a rate/ticks pair with no rounding residue.
        targetTicks * rate / TurnReadiness.SpeedScale;

    [Theory]
    [InlineData(100, 100)]   // default rate: one full turn costs exactly SpeedScale ticks
    [InlineData(200, 50)]    // doubling speed halves the interval
    [InlineData(50, 200)]    // halving speed doubles the interval
    public void DoublingSpeedHalvesTheIntervalMonotonicity(long rate, long expectedTicks) =>
        Assert.Equal(expectedTicks, TurnReadiness.TicksPerFullTurn(rate));

    [Theory]
    [InlineData(1000, 100)]  // haste 1000 (normal) leaves the interval unchanged
    [InlineData(500, 50)]    // haste 500 (twice as fast) halves it
    [InlineData(2000, 200)]  // haste 2000 (half as fast) doubles it
    public void HasteScalesTheIntervalMonotonically(long haste, long expectedTicks)
    {
        var rate = TurnReadiness.EffectiveRate(speed: 100, haste);
        Assert.Equal(expectedTicks, TurnReadiness.TicksPerFullTurn(rate));
    }

    [Fact]
    public void AZeroCostTurnStillYieldsAtLeastOneTick()
    {
        // "A zero-tick readiness... is an infinite loop that never advances the clock" -- asserted
        // directly, because this invariant is the difference between a working clock and a hang.
        Assert.Equal(1, TurnReadiness.TicksFor(remainingWork: 0, rate: 1_000_000));
    }

    [Fact]
    public void RateMustBeClampedBeforeThisCallZeroThrowsRatherThanDividingByZero()
    {
        // spec boundary: "speed clamped before division" -- the CALLER clamps; this pure function
        // enforces the precondition loudly rather than silently producing a wrong answer.
        Assert.Throws<ArgumentOutOfRangeException>(() => TurnReadiness.TicksFor(100, rate: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TurnReadiness.EffectiveRate(100, haste: 0));
    }

    [Fact]
    public void NegativeRemainingWorkIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TurnReadiness.TicksFor(remainingWork: -1, rate: 100));

    [Fact]
    public void TheReadinessBoundaryIsLongTyped()
    {
        // Integer RANGE contract: tick counts and work units are magnitudes, so every parameter and
        // return type of the public readiness surface is long (never a narrower int). This is not a
        // floating-point ban — that project-wide ban was removed by the 2026-09-15 owner ruling.
        foreach (var method in typeof(TurnReadiness).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            foreach (var p in method.GetParameters())
                Assert.True(p.ParameterType == typeof(long), $"{method.Name}'s parameter '{p.Name}' is {p.ParameterType}, not long");
            if (method.ReturnType != typeof(void))
                Assert.Equal(typeof(long), method.ReturnType);
        }
    }
}
