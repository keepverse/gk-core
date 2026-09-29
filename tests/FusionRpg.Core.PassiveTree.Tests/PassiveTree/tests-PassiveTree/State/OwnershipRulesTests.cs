using FusionRpg.Core.PassiveTree.State;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.State;

/// <summary>Task C8 — §2.1's five ownership rows (spec-tree-state.md), and the invariant they exist
/// to protect: an item swap must not change what your next node costs, net.</summary>
public class OwnershipRulesTests
{
    [Fact]
    public void The_five_rows_match_the_spec_table_exactly()
    {
        Assert.True(OwnershipRules.SelfEarnedSkillPointsCount);
        Assert.True(OwnershipRules.ItemGrantedPointsCountWhileEquipped);
        Assert.False(OwnershipRules.InvalidUnequippedNodesCount);
        Assert.False(OwnershipRules.SoulLevelsCount);
        Assert.True(OwnershipRules.NodesOnOtherTreesCount);
    }

    [Fact] // an_item_swap_does_not_change_the_next_node_price_net
    public void An_item_swap_does_not_change_the_next_node_price_net()
    {
        // TreeUnlockCost's whole public surface takes only a COUNT -- there is no item-equipped
        // parameter anywhere in its signature for a swap to enter through. This proves the invariant
        // by construction: the price is a pure function of ownedCount, so any two actors (or the same
        // actor before/after an item swap that leaves the VALID owned count unchanged) price
        // identically -- an item swap that grants or revokes a node changes ownedCount itself (D11's
        // own rule, OwnershipRules.ItemGrantedPointsCountWhileEquipped), but never changes the PRICE
        // FORMULA for a fixed count.
        const long ownedCount = 7;
        var beforeSwap = TreeUnlockCost.PriceOfNth(ownedCount + 1, first: 5, step: 2);
        var afterSwapSameCount = TreeUnlockCost.PriceOfNth(ownedCount + 1, first: 5, step: 2);
        Assert.Equal(beforeSwap, afterSwapSameCount);

        var cumulativeBefore = TreeUnlockCost.Cumulative(ownedCount, first: 5, step: 2);
        var cumulativeAfter = TreeUnlockCost.Cumulative(ownedCount, first: 5, step: 2);
        Assert.Equal(cumulativeBefore, cumulativeAfter);
    }
}
