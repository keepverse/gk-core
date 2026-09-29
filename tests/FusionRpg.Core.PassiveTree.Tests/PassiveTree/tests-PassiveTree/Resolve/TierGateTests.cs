using FusionRpg.Core.PassiveTree.Resolve;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.Resolve;

/// <summary>Task B6 — `TierGate` (spec-tree-resolve.md §3).</summary>
public class TierGateTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(5, 1)]
    [InlineData(14, 1)]
    [InlineData(15, 2)]
    [InlineData(29, 2)]
    [InlineData(30, 3)]
    [InlineData(274, 9)]
    [InlineData(275, 10)]
    [InlineData(1_000_000, 10)] // never above the authored depth -- no node exists to buy
    public void Tier_reached_matches_the_req_table_exactly(long aptitudePoints, int expectedTier)
    {
        // req(t) = 5,15,30,50,75,105,140,180,225,275 -- the exact spec table (also B1's own table).
        Assert.Equal(expectedTier, TierGate.Reached(aptitudePoints, authoredTierCount: 10, reqScalePoints: 5));
    }

    [Fact]
    public void Tier_gate_reads_the_catalog_depth_not_a_literal()
    {
        // The named verification line in the task itself: a tree authored to only 3 tiers deep
        // never reaches tier 4 regardless of how many aptitude points the actor holds -- the loop
        // bound comes from the CALLER's own authoredTierCount, never a hardcoded 10.
        Assert.Equal(3, TierGate.Reached(aptitudePoints: 1_000_000, authoredTierCount: 3, reqScalePoints: 5));
    }

    [Fact]
    public void Zero_authored_tiers_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TierGate.Reached(100, 0, 5));
    }
}
