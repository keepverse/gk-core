using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// **B23 / T7 — the PvZ observer** (spec-pvz-observer.md). A live lawn described in the kernel's own
/// vocabulary, so telemetry, VFX and the forecast speak one language across modes.
///
/// <para>It is an <b>adapter, not a scheduler</b> — the Unity game owns that clock, and this module
/// never schedules, never advances one, and holds no queue or per-actor machine.</para>
/// </summary>
public class PvzObserverProjectionTests
{
    [Theory]
    [InlineData(ObservedLawnFact.Spawned, TurnState.Charging)]
    [InlineData(ObservedLawnFact.Idle, TurnState.Ready)]
    [InlineData(ObservedLawnFact.Acting, TurnState.Resolving)]
    [InlineData(ObservedLawnFact.CoolingDown, TurnState.Recovering)]
    [InlineData(ObservedLawnFact.Died, TurnState.Dead)]
    [InlineData(ObservedLawnFact.Removed, TurnState.Withdrawn)]
    public void EveryObservedFactMapsToOneState(ObservedLawnFact fact, TurnState expected)
    {
        Assert.Equal(expected, PvzObserverProjection.Project(fact));
    }

    /// <summary>
    /// ⛔ **The honesty check.** `Committed` means "intent locked, wind-up running" — a turn-based
    /// concept. PvZ has no observable moment between deciding and resolving, so projecting it would
    /// invent a fact the lawn cannot supply. The vocabulary is shared; the coverage is not, and this
    /// test is what stops a future mapping from quietly pretending otherwise.
    /// </summary>
    [Fact]
    public void CommittedIsNeverProjected()
    {
        foreach (ObservedLawnFact fact in Enum.GetValues<ObservedLawnFact>())
            Assert.NotEqual(TurnState.Committed, PvzObserverProjection.Project(fact));
    }

    /// <summary>The observed lawn and the kernel must agree on what "gone" means, or a withdrawn
    /// entity would keep appearing in a projection that thinks it is still present.</summary>
    [Theory]
    [InlineData(ObservedLawnFact.Died)]
    [InlineData(ObservedLawnFact.Removed)]
    public void TerminalFactsProjectToTerminalStates(ObservedLawnFact fact)
    {
        Assert.True(TurnTransitions.IsTerminal(PvzObserverProjection.Project(fact)));
    }

    [Theory]
    [InlineData(ObservedLawnFact.Spawned)]
    [InlineData(ObservedLawnFact.Idle)]
    [InlineData(ObservedLawnFact.Acting)]
    [InlineData(ObservedLawnFact.CoolingDown)]
    public void NonTerminalFactsProjectToNonTerminalStates(ObservedLawnFact fact)
    {
        Assert.False(TurnTransitions.IsTerminal(PvzObserverProjection.Project(fact)));
    }

    /// <summary>The vocabulary is closed — an unmapped value throws rather than defaulting, so adding
    /// a lawn fact without deciding its meaning fails loudly.</summary>
    [Fact]
    public void AnUnknownFactIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PvzObserverProjection.Project((ObservedLawnFact)999));
    }

    /// <summary>
    /// ⭐ **Zero allocation on the observe path**, which is the acceptance that matters: this runs on
    /// the injector's hot path, and a per-observation allocation is the exact cost the 2026-08 perf
    /// audit had to remove once already.
    ///
    /// <para>Measured with the thread-local counter, with a liveness assertion so "zero" cannot be
    /// trivially true because the loop was optimised away.</para>
    /// </summary>
    [Fact]
    public void ObservingAllocatesNothing()
    {
        var facts = Enum.GetValues<ObservedLawnFact>();

        var warm = 0;
        for (var i = 0; i < 1000; i++) warm += (int)PvzObserverProjection.Project(facts[i % facts.Length]);
        Assert.True(warm > 0, "warm-up must actually run");

        var before = GC.GetAllocatedBytesForCurrentThread();
        var sink = 0;
        for (var i = 0; i < 100_000; i++) sink += (int)PvzObserverProjection.Project(facts[i % facts.Length]);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.True(sink > 0, "the measured loop must actually run");
        Assert.Equal(0, after - before);
    }

    /// <summary>We do not own the lawn's clock, so there is nothing to roll forward — only a present
    /// to describe. `Absent` already existed for exactly this (T8).</summary>
    [Fact]
    public void AForecastOverALiveLawnIsAbsent()
    {
        Assert.Equal(ForecastExactness.Absent, PvzObserverProjection.ForecastExactness);
    }
}
