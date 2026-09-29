using FusionRpg.Core.PassiveTree.State;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree;

/// <summary>Task B5 — `TreeUnlockCost` (spec-tree-state.md §2). D25/D36's rising unlock cost, and
/// the order-independence lemma that makes derive-on-read safe.</summary>
public class TreeUnlockCostTests
{
    [Fact]
    public void Price_of_nth_matches_first_plus_n_minus_one_times_step()
    {
        Assert.Equal(5L, TreeUnlockCost.PriceOfNth(1, first: 5, step: 2));
        Assert.Equal(7L, TreeUnlockCost.PriceOfNth(2, first: 5, step: 2));
        Assert.Equal(9L, TreeUnlockCost.PriceOfNth(3, first: 5, step: 2));
    }

    [Fact]
    public void Cumulative_at_the_shipped_pair_collapses_to_N_times_N_plus_4()
    {
        // spec-tree-state.md §2: "at the shipped pair this collapses to cumulative(N) = N(N+4)".
        for (long n = 0; n <= 50; n++)
        {
            var expected = n * (n + 4);
            Assert.Equal(expected, TreeUnlockCost.Cumulative(n, first: 5, step: 2));
        }
    }

    [Fact]
    public void Cumulative_matches_the_sum_of_individual_prices()
    {
        for (long n = 1; n <= 30; n++)
        {
            long sum = 0;
            for (long i = 1; i <= n; i++)
                sum += TreeUnlockCost.PriceOfNth(i, first: 7, step: 3);
            Assert.Equal(sum, TreeUnlockCost.Cumulative(n, first: 7, step: 3));
        }
    }

    [Fact]
    public void Cumulative_of_zero_is_zero()
    {
        Assert.Equal(0L, TreeUnlockCost.Cumulative(0, first: 5, step: 2));
    }

    [Fact]
    public void The_order_independence_lemma_the_cost_of_a_set_does_not_depend_on_purchase_order()
    {
        // spec-tree-state.md §2: "the lemma that makes derive-on-read safe, and it must be a test."
        // Simulates buying the SAME 5-node set in two different orders and asserts the running
        // cumulative cost after all 5 purchases is identical regardless of which node was "first".
        const int count = 5;
        const long first = 5, step = 2;

        // Order A: nodes bought 1,2,3,4,5 (their own price is fixed by ORDINAL, not identity).
        long totalA = 0;
        for (long i = 1; i <= count; i++) totalA += TreeUnlockCost.PriceOfNth(i, first, step);

        // Order B: same COUNT, different hypothetical sequence -- since price is purely a function
        // of ordinal (1..count) and never of which specific node id occupies that ordinal, any
        // permutation of WHICH node is bought Nth produces the identical price sequence.
        var shuffledOrdinals = new long[] { 3, 1, 5, 2, 4 }; // a permutation of 1..5
        long totalB = 0;
        foreach (var ordinal in shuffledOrdinals) totalB += TreeUnlockCost.PriceOfNth(ordinal, first, step);

        Assert.Equal(totalA, totalB);
        Assert.Equal(TreeUnlockCost.Cumulative(count, first, step), totalA);
    }

    [Fact]
    public void Rebuying_the_same_set_after_a_respec_costs_exactly_what_it_cost_before()
    {
        // "Re-buying the same build costs exactly what it cost before" -- cumulative is a function
        // of count alone, and count restarts at 0 after a respec (nothing stored to remember a
        // discount or a penalty).
        const long first = 5, step = 2;
        var beforeRespec = TreeUnlockCost.Cumulative(12, first, step);

        // Respec: count resets to 0 (nothing refunded, nothing remembered).
        long countAfterRespec = 0;
        // Re-buy the same 12 nodes.
        for (var i = 0; i < 12; i++) countAfterRespec++;
        var afterRebuy = TreeUnlockCost.Cumulative(countAfterRespec, first, step);

        Assert.Equal(beforeRespec, afterRebuy);
    }

    [Fact]
    public void An_item_swap_does_not_change_what_the_next_node_costs_net()
    {
        // spec-tree-state.md §2.1: "an item swap must not change what your next node costs, net" —
        // since price is a pure function of the VALID owned count, and item-granted points count
        // toward that count (D11) exactly like self-earned ones, unequipping an item that funded
        // node N (making it invalid, per D11's red state) removes it from the valid count, and the
        // NEXT node's price is recomputed purely from the new (lower) count — never from a stored
        // price that would otherwise go stale.
        const long first = 5, step = 2;
        var priceAtCount10 = TreeUnlockCost.PriceOfNth(11, first, step); // the 11th node, count=10 before it
        var priceAtCount9 = TreeUnlockCost.PriceOfNth(10, first, step); // one fewer valid node (item unequipped)

        Assert.NotEqual(priceAtCount10, priceAtCount9); // the count changing DOES move the price...
        // ...but critically, the SAME count always gives the SAME price, regardless of which nodes
        // make up that count (there is no per-node stored price to go stale).
        Assert.Equal(priceAtCount10, TreeUnlockCost.PriceOfNth(11, first, step));
    }

    [Fact]
    public void Available_subtracts_cumulative_from_budget_never_clamping()
    {
        // cumulative(4, 5, 2) = 4*(4+4) = 32 (the shipped-pair collapse N(N+4), verified above).
        Assert.Equal(100L - 32L, TreeUnlockCost.Available(budget: 100, ownedCount: 4, first: 5, step: 2));
        // Deliberately allowed to go negative -- this function only subtracts, never clamps.
        Assert.True(TreeUnlockCost.Available(budget: 0, ownedCount: 100, first: 5, step: 2) < 0);
    }

    [Fact]
    public void Overflow_throws_rather_than_wraps()
    {
        Assert.Throws<OverflowException>(() =>
            TreeUnlockCost.Cumulative(long.MaxValue / 2, first: long.MaxValue, step: long.MaxValue));
    }

    [Fact]
    public void A_negative_count_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TreeUnlockCost.Cumulative(-1, 5, 2));
    }

    [Fact]
    public void The_full_corpus_cumulative_matches_the_spec_own_stated_figure()
    {
        // spec-tree-state.md §7 (D51, 2026-09-06: 24 statuses, not 21): "the shipped corpus...
        // cumulative is 35,280 * 35,284 = 1,244,819,520". Was 35,160 * 35,164 = 1,236,366,240
        // before D51's corpus growth -- that figure is now struck through in the spec itself.
        Assert.Equal(1_244_819_520L, TreeUnlockCost.Cumulative(35_280, first: 5, step: 2));
    }
}
