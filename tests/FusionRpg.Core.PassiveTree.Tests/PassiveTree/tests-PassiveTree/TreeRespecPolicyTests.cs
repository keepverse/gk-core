using FusionRpg.Core.PassiveTree.State;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree;

/// <summary>Task C10 — `TreeRespecPolicy` (spec-tree-state.md §5, §5.1). The exact structural sibling
/// of `RespecPolicy.PriceOf` (Stats/Aptitudes/RespecPolicy.cs), proven against the same formula shape
/// with the tree's own tuning.</summary>
public class TreeRespecPolicyTests
{
    static PassiveTreeTuning Tuning(long basePrice, long escalationPermille) => new(
        SchemaVersion: 1, Version: 1,
        TierLadder: new TierLadderTuning(5),
        Budget: new BudgetTuning(1000, 500),
        TreeShareMilli: 1000, TreeBudgetMilli: 1000,
        Potency: new PotencyTuning(182, 1, new long[] { 46, 91, 137, 182 }),
        Mechanism: new MechanismTuning(0, 1000),
        Archetype: new ArchetypeTuning(6000),
        Exclusion: new ExclusionTuning(20),
        ArchetypeAssignment: "ordinal-round-robin",
        DesignTarget: new DesignTargetTuning(92),
        Concentration: new ConcentrationTuning(1200, 500),
        SoulTrack: new SoulTrackTuning(1000),
        UnlockCost: new UnlockCostTuning(5, 2),
        Respec: new RespecTuning(basePrice, escalationPermille),
        GateCounters: new GateCountersTuning(23, 23, 4, 4, 5000, null));

    [Fact]
    public void Zero_prior_respecs_costs_exactly_basePrice()
    {
        var price = TreeRespecPolicy.PriceOf(Tuning(50, 500), count: 0);
        Assert.Equal(50, price.Amount);
    }

    [Fact]
    public void Price_escalates_linearly_on_the_persisted_count()
    {
        var tuning = Tuning(50, 500); // 50% of base per prior respec
        Assert.Equal(50, TreeRespecPolicy.PriceOf(tuning, 0).Amount);
        Assert.Equal(75, TreeRespecPolicy.PriceOf(tuning, 1).Amount);  // 50 + 50*1*500/1000
        Assert.Equal(100, TreeRespecPolicy.PriceOf(tuning, 2).Amount); // 50 + 50*2*500/1000
        Assert.Equal(150, TreeRespecPolicy.PriceOf(tuning, 4).Amount); // 50 + 50*4*500/1000
    }

    [Fact]
    public void Never_refused_for_being_a_respec_any_nonnegative_count_prices()
    {
        // There is no "cannot respec" return here on purpose -- every nonnegative count prices.
        for (long count = 0; count < 1000; count += 137)
            Assert.True(TreeRespecPolicy.PriceOf(Tuning(50, 500), count).Amount > 0);
    }

    [Fact]
    public void A_negative_count_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TreeRespecPolicy.PriceOf(Tuning(50, 500), -1));
    }

    [Fact]
    public void Null_tuning_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => TreeRespecPolicy.PriceOf(null!, 0));
    }

    [Fact]
    public void A_runaway_count_throws_rather_than_wraps()
    {
        Assert.Throws<OverflowException>(() => TreeRespecPolicy.PriceOf(Tuning(long.MaxValue / 2, 1000), 1_000_000));
    }
}
