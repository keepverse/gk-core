using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Actions.Defence;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// `battle-tempo` `reaction-lane` RL2 (spec-reaction-lane.md §2.2a, decision 12): the counter's cost
/// and payoff — the spend IS the attack. `poise` mirrors `Actions.PoiseLedgerTests`' own snapshot
/// pattern exactly.
/// </summary>
public class ReactionCounterTests
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

    [Fact]
    public void ASuccessfulCounterCommitsThePoiseAndDealsExactlyRiposteDamage()
    {
        var derived = PoiseSnapshot(max: 1000, regenPerTick: 0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        var (committed, damage) = ReactionCounter.TryCounter(pools, poiseSpend: 400, riposteShareCapMilli: 300, nowTick: 0, derived);

        Assert.True(committed);
        Assert.Equal(120, damage); // 400 * 300 / 1000
        Assert.Equal(600, pools.Resolve(PoiseLedger.ResourceId, 0, derived)); // 1000 - 400
    }

    /// <summary>The declining case: an actor without enough `poise` cannot counter — all-or-nothing,
    /// pool untouched, zero damage. This is the resource judgement decision 10/12 exist to create.</summary>
    [Fact]
    public void AnUnaffordableCounterRefusesAndChangesNothing()
    {
        var derived = PoiseSnapshot(max: 100, regenPerTick: 0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        var (committed, damage) = ReactionCounter.TryCounter(pools, poiseSpend: 500, riposteShareCapMilli: 300, nowTick: 0, derived);

        Assert.False(committed);
        Assert.Equal(0, damage);
        Assert.Equal(100, pools.Resolve(PoiseLedger.ResourceId, 0, derived)); // untouched, not partially spent
    }

    /// <summary>Spending more competes with the next guard/absorb — the SAME pool, so a bigger
    /// counter now leaves less poise for whatever comes next.</summary>
    [Fact]
    public void ABiggerSpendDealsMoreDamageButLeavesLessPoiseForWhatComesNext()
    {
        var derived = PoiseSnapshot(max: 1000, regenPerTick: 0);
        var smallPools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var bigPools = ActorResourcePools.CreateFull(derived, atTick: 0);

        var (_, smallDamage) = ReactionCounter.TryCounter(smallPools, poiseSpend: 100, riposteShareCapMilli: 300, 0, derived);
        var (_, bigDamage) = ReactionCounter.TryCounter(bigPools, poiseSpend: 800, riposteShareCapMilli: 300, 0, derived);

        Assert.True(bigDamage > smallDamage);
        Assert.True(bigPools.Resolve(PoiseLedger.ResourceId, 0, derived) < smallPools.Resolve(PoiseLedger.ResourceId, 0, derived));
    }

    [Fact]
    public void ZeroSpendIsLegalAndDealsZeroDamage()
    {
        var derived = PoiseSnapshot(max: 100, regenPerTick: 0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        var (committed, damage) = ReactionCounter.TryCounter(pools, poiseSpend: 0, riposteShareCapMilli: 300, 0, derived);

        Assert.True(committed); // committing zero is a legal no-op commit, not a refusal
        Assert.Equal(0, damage);
        Assert.Equal(100, pools.Resolve(PoiseLedger.ResourceId, 0, derived));
    }

    /// <summary>Riposte's own uncapped-pool guarantee survives the combining function: an
    /// astronomically large spend (from a hydrated pool) converts proportionally, no private ceiling.</summary>
    [Fact]
    public void AnEnormousSpendConvertsProportionallyWithNoPrivateCeiling()
    {
        const long enormous = 2_000_000_000_000L;
        var derived = PoiseSnapshot(max: enormous, regenPerTick: 0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        var (committed, damage) = ReactionCounter.TryCounter(pools, poiseSpend: enormous, riposteShareCapMilli: 300, 0, derived);

        Assert.True(committed);
        Assert.Equal(enormous * 300 / 1000, damage);
    }

    [Fact]
    public void AnOutOfRangeShareThrows()
    {
        var derived = PoiseSnapshot(max: 1000, regenPerTick: 0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReactionCounter.TryCounter(pools, poiseSpend: 100, riposteShareCapMilli: 1001, 0, derived));
    }
}
