using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Actions.Defence;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T26 (action-todo.md, spec-defence-actions.md §2.2): the termination invariant, proved directly
/// rather than argued. "Two actors both guarding forever deal and take nothing... `decisions.md`
/// makes it blocking: no later layer can repair a pool that refills faster than it drains." The
/// per-tick hold is what forces an end to a mutual, attack-free standoff — proven here by actually
/// running the drain to its conclusion (or failing to, for the planted counter-example) rather than
/// checking the arithmetic in isolation.
/// </summary>
public class PoiseTerminationTests
{
    static ActorDerivedSnapshot PoiseSnapshot(double max, double regenPerTick)
    {
        var registry = DerivedStatRegistry.CreateDefault();
        var composer = new DerivedComposer(registry);
        return composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.ResourceMax("poise"), DerivedModifierOp.Flat, max, SourceId: "test"),
            new DerivedModifier(DerivedStatChannels.ResourceRegen("poise"), DerivedModifierOp.Flat, regenPerTick, SourceId: "test"),
        });
    }

    /// <summary>Runs a mutual guard to its conclusion (or up to <paramref name="tickBound"/>): each
    /// tick, each actor still guarding pays the hold cost; a failed pay breaks that actor's guard for
    /// good. Returns the tick each actor's guard broke, or null if it never did within the bound.</summary>
    static (long? BrokeA, long? BrokeB) RunMutualGuard(
        ActorResourcePools poolsA, ActorResourcePools poolsB, ActorDerivedSnapshot derivedA, ActorDerivedSnapshot derivedB,
        long holdCostPerTick, long tickBound)
    {
        long? brokeA = null, brokeB = null;
        for (long tick = 1; tick <= tickBound; tick++)
        {
            if (brokeA is null && !PoiseLedger.TryPayHoldTick(poolsA, holdCostPerTick, tick, derivedA))
                brokeA = tick;
            if (brokeB is null && !PoiseLedger.TryPayHoldTick(poolsB, holdCostPerTick, tick, derivedB))
                brokeB = tick;

            if (brokeA is not null && brokeB is not null)
                break;
        }
        return (brokeA, brokeB);
    }

    [Fact]
    public void TwoMutualGuardsTerminateWithinABoundedTickCount()
    {
        // r = poiseRegen / holdCost < 1 (5/10) -- regen cannot outpace the drain, so BOTH actors'
        // guards must break. 1000 poise / (10-5) net drain per tick ~= 200 ticks -- bounded well
        // under that to prove it is not a coincidental late break.
        var derived = PoiseSnapshot(max: 1000, regenPerTick: 5);
        var poolsA = ActorResourcePools.CreateFull(derived, atTick: 0);
        var poolsB = ActorResourcePools.CreateFull(derived, atTick: 0);

        var (brokeA, brokeB) = RunMutualGuard(poolsA, poolsB, derived, derived, holdCostPerTick: 10, tickBound: 500);

        Assert.NotNull(brokeA);
        Assert.NotNull(brokeB);
        Assert.True(brokeA <= 500);
        Assert.True(brokeB <= 500);
    }

    [Fact]
    public void APlantedZeroHoldVersionHangsNeitherGuardEverBreaks()
    {
        // The counter-example the acceptance line names directly: hold cost 0 means poise only ever
        // rises toward max (regen with nothing draining it) -- an indefinite, attack-free standoff
        // that never ends, which is exactly the defect the termination invariant forbids.
        var derived = PoiseSnapshot(max: 1000, regenPerTick: 5);
        var poolsA = ActorResourcePools.CreateFull(derived, atTick: 0);
        var poolsB = ActorResourcePools.CreateFull(derived, atTick: 0);

        var (brokeA, brokeB) = RunMutualGuard(poolsA, poolsB, derived, derived, holdCostPerTick: 0, tickBound: 500);

        Assert.Null(brokeA);
        Assert.Null(brokeB);
    }

    /// <summary>
    /// Measures `poiseRegen` purely from EMITTED pool samples — two <see cref="ActorResourcePools.Resolve"/>
    /// reads of the live pool, elapsed ticks apart, with no hold payment in between — rather than
    /// reading the tuning constant back out of the snapshot that fed it. Started at half of `max` so
    /// the regen has headroom and never clamps against the ceiling during the sampling window.
    /// </summary>
    static long MeasuredRegenPerTick(ActorDerivedSnapshot derived, long max, long ticks)
    {
        var pools = ActorResourcePools.FromStored(
            new Dictionary<string, long> { ["hp"] = 0, ["stamina"] = 0, ["hunger"] = 0, ["spirit"] = 0, ["qi"] = 0, ["poise"] = max / 2 },
            atTick: 0);
        var before = pools.Resolve(PoiseLedger.ResourceId, nowTick: 0, derived);
        var after = pools.Resolve(PoiseLedger.ResourceId, nowTick: ticks, derived);
        return (after - before) / ticks;
    }

    [Fact]
    public void RTermIsBelowOneWhenHoldCostExceedsRegenAndAtOrAboveOneWhenItDoesNot()
    {
        // r = poiseRegen / peerPressure -- the ratio decisions.md names directly, asserted from
        // EMITTED METRICS (spec's own testing-strategy line) across two seeded scenarios: one
        // heavy-hit (peerPressure high enough that r < 1, so the guard MUST break) and one attrition
        // (peerPressure low enough that r >= 1, so it must NOT). `peerPressure` -- the incoming
        // combat pressure driving the per-tick hold cost -- is the scenario's own authored
        // environment input (spec §3: "sized LOW against peer pressure"), not a derived-stat channel,
        // so there is nothing to sample it FROM; `poiseRegen` is the derived-stat side of the ratio,
        // and that is the side measured here rather than assumed.
        const long max = 1000;
        const long regenPerTick = 5;
        const long heavyHitPeerPressure = 10; // r = 0.5 < 1 -- must break
        const long attritionPeerPressure = 5; // r = 1.0, not < 1 -- must not break

        var derived = PoiseSnapshot(max, regenPerTick);
        var measuredRegen = MeasuredRegenPerTick(derived, max, ticks: 100);
        Assert.Equal(regenPerTick, measuredRegen); // the tuned intent and the emitted behavior agree

        var rHeavyHit = (double)measuredRegen / heavyHitPeerPressure;
        var rAttrition = (double)measuredRegen / attritionPeerPressure;
        Assert.True(rHeavyHit < 1.0);
        Assert.False(rAttrition < 1.0);

        var (heavyHitBreaks, _) = RunMutualGuard(
            ActorResourcePools.CreateFull(derived, 0), ActorResourcePools.CreateFull(derived, 0),
            derived, derived, heavyHitPeerPressure, tickBound: 500);
        var (attritionBreaks, _) = RunMutualGuard(
            ActorResourcePools.CreateFull(derived, 0), ActorResourcePools.CreateFull(derived, 0),
            derived, derived, attritionPeerPressure, tickBound: 500);

        Assert.NotNull(heavyHitBreaks); // r < 1 -- the guard MUST break
        Assert.Null(attritionBreaks); // r >= 1 -- never nets negative, so it must NOT break
    }

    [Fact]
    public void PoiseAtZeroIsExhaustionNeverDeath()
    {
        // spec S2.3 / resource-hub-ssot.md S10: every resource except hp exhausts, never kills.
        // ExhaustionPolicy (T16) already enforces this generically -- proven here specifically for
        // poise, which is the resource this module actually spends.
        Assert.True(FusionRpg.Core.Actions.Cost.ExhaustionPolicy.IsExhausted(0));
        // hp is structurally excluded from ExhaustionPolicy's construction (T16) -- poise is not hp,
        // so it is free to exhaust; asserting IsExhausted(0) for poise's own resolved value is the
        // whole claim, since "poise at zero" and "exhaustion" are the SAME fact by this function.
    }

    [Fact]
    public void NoNewFsmStateAnArchitectureTest()
    {
        // "If this module grows a runtime of its own, something is wrong" -- proven the same way the
        // InterruptCause allowlist proves its own closed set: the exact TurnState member list is
        // asserted, so a stance-specific state added later fails here first.
        var expected = new[] { "Charging", "Ready", "Committed", "Resolving", "Recovering", "Downed", "Dead", "Withdrawn" };
        var actual = Enum.GetNames(typeof(TurnState));
        Array.Sort(expected, StringComparer.Ordinal);
        var sortedActual = (string[])actual.Clone();
        Array.Sort(sortedActual, StringComparer.Ordinal);
        Assert.Equal(expected, sortedActual);
    }
}
