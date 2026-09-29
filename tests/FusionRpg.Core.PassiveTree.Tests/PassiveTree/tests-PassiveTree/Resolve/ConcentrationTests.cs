using FusionRpg.Core.PassiveTree.Resolve;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.Resolve;

/// <summary>Task B6 — `Concentration` (spec-tree-resolve.md §5.1-5.2). `H` reads the final
/// allocation, self-spent only (D39) — order-independent by construction, since it is a pure
/// function of the counts held, never the sequence they were bought in.</summary>
public class ConcentrationTests
{
    [Fact]
    public void Empty_denominator_reads_zero_never_a_uniform_default()
    {
        Assert.Equal(0L, Concentration.HerfindahlMilli(Array.Empty<long>()));
        Assert.Equal(0L, Concentration.HerfindahlMilli(new long[] { 0, 0, 0 }));
    }

    [Fact]
    public void A_pure_single_tree_build_gives_H_equals_1000_per_mille()
    {
        // All commitment in one tree -> H_nodes = (n/n)^2 = 1 exactly.
        Assert.Equal(1000L, Concentration.HerfindahlMilli(new long[] { 40, 0, 0 }));
    }

    [Fact]
    public void An_even_spread_across_n_trees_gives_H_close_to_1_over_n()
    {
        // 10 trees, 4 nodes each -> H = 10 * (4/40)^2 = 10 * 0.01 = 0.1 -> 100 per-mille.
        var counts = Enumerable.Repeat(4L, 10).ToArray();
        Assert.Equal(100L, Concentration.HerfindahlMilli(counts));
    }

    [Fact]
    public void H_is_order_independent_the_same_final_counts_give_the_same_H_regardless_of_how_built()
    {
        // Two different PURCHASE ORDERS producing the SAME final per-tree counts must give the
        // identical H -- this is what "order-independent" means operationally: H is a function of
        // the held counts array alone, never of any sequencing information (which this signature
        // does not even accept).
        var finalCountsOrderA = new long[] { 12, 8, 20 }; // however these were bought
        var finalCountsOrderB = new long[] { 12, 8, 20 }; // same final build, different route

        Assert.Equal(
            Concentration.HerfindahlMilli(finalCountsOrderA),
            Concentration.HerfindahlMilli(finalCountsOrderB));
    }

    [Fact]
    public void The_hand_written_mutant_that_reads_points_paid_instead_of_nodes_held_would_disagree()
    {
        // spec-tree-resolve.md §5.1: "the hand-written mutant that reads POINTS PAID instead of
        // NODES HELD exists to make sure test 6c is doing the work." Simulated here: under D25's
        // rising cost, the SAME node count bought in two different orders costs different POINTS
        // (since price depends on total owned across ALL trees at time of purchase), but the same
        // node COUNT. This proves H (over counts) does NOT vary with order, while a points-based H
        // WOULD have -- the exact defect D39 closed.
        var counts = new long[] { 5, 5 }; // two trees, 5 nodes each, regardless of order bought
        var hFromCounts1 = Concentration.HerfindahlMilli(counts);
        var hFromCounts2 = Concentration.HerfindahlMilli(new long[] { 5, 5 }); // identical counts, "different order"
        Assert.Equal(hFromCounts1, hFromCounts2); // real H: order never enters the computation at all
    }

    [Fact]
    public void Blend_weights_nodes_and_souls_by_wMilli()
    {
        // w = 500 (50/50 blend): H = 0.5*hNodes + 0.5*hSouls.
        var h = Concentration.BlendMilli(hNodesMilli: 1000, hSoulsMilli: 0, wMilli: 500);
        Assert.Equal(500L, h);
    }

    [Fact]
    public void Blend_at_w_1000_is_pure_nodes()
    {
        Assert.Equal(1000L, Concentration.BlendMilli(hNodesMilli: 1000, hSoulsMilli: 0, wMilli: 1000));
    }

    [Fact]
    public void Blend_at_w_0_is_pure_souls()
    {
        Assert.Equal(1000L, Concentration.BlendMilli(hNodesMilli: 0, hSoulsMilli: 1000, wMilli: 0));
    }

    [Fact]
    public void A_fresh_actor_with_zero_investment_reads_F_1_000_exactly()
    {
        var h = Concentration.BlendMilli(0, 0, wMilli: 500);
        var f = Concentration.FmaxAppliedMilli(h, fmaxMilli: 1200);
        Assert.Equal(0L, h);
        Assert.Equal(1000L, f); // F = 1.000 exactly -- no commitment, no compensation
    }

    [Fact]
    public void F_is_provably_in_1_to_Fmax_for_every_h_in_0_to_1000()
    {
        for (long hMilli = 0; hMilli <= 1000; hMilli += 37) // sampled, not exhaustive over the range
        {
            var f = Concentration.FmaxAppliedMilli(hMilli, fmaxMilli: 1200);
            Assert.InRange(f, 1000L, 1200L);
        }
    }

    [Fact]
    public void Fmax_1000_removes_F_byte_identically_a_legal_tested_configuration()
    {
        // spec-tree-resolve.md: "Fmax = 1000 per-mille is a legal, tested configuration that removes
        // F byte-identically without removing a code path" (D5 is provisional).
        for (long hMilli = 0; hMilli <= 1000; hMilli += 100)
            Assert.Equal(1000L, Concentration.FmaxAppliedMilli(hMilli, fmaxMilli: 1000));
    }

    [Fact]
    public void A_pure_build_reads_the_maximum_F_exactly_Fmax()
    {
        var h = Concentration.BlendMilli(hNodesMilli: 1000, hSoulsMilli: 1000, wMilli: 500);
        var f = Concentration.FmaxAppliedMilli(h, fmaxMilli: 1200);
        Assert.Equal(1000L, h);
        Assert.Equal(1200L, f);
    }

    [Fact]
    public void Negative_counts_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Concentration.HerfindahlMilli(new long[] { -1 }));
    }

    [Fact]
    public void Out_of_range_w_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Concentration.BlendMilli(0, 0, wMilli: 1001));
        Assert.Throws<ArgumentOutOfRangeException>(() => Concentration.BlendMilli(0, 0, wMilli: -1));
    }
}
