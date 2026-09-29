using FusionRpg.Core.Delve.Attrition;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Attrition;

/// <summary>D2.19 (spec-delve-attrition.md §4) — `NerveLadder.StageFor`: the pure stack-to-stage
/// resolver, proven against the real shipped thresholds (`[1, 3, 5]`, `dungeon.v1.json`
/// `attrition.nerve.stageThresholds`) plus the boundary and exhaustion cases the spec names by
/// number.</summary>
public class NerveLadderTests
{
    static readonly IReadOnlyList<int> RealThresholds = new[] { 1, 3, 5 }; // the shipped starting shape

    // ---- the spec's own headline numbers: stacks 0/1/3/5 -> stage -1/0/1/2 ----

    [Theory]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(3, 1)]
    [InlineData(5, 2)]
    public void The_spec_named_stack_counts_resolve_to_exactly_these_stages(int stacks, int expectedStage)
    {
        Assert.Equal(expectedStage, NerveLadder.StageFor(stacks, spiritResolved: 1000, RealThresholds));
    }

    // ---- boundaries between the named points, so the "highest threshold <= stacks" rule is pinned, not inferred ----

    [Theory]
    [InlineData(2, 0)]  // still stage 0 -- below the next threshold (3)
    [InlineData(4, 1)]  // still stage 1 -- below the next threshold (5)
    [InlineData(100, 2)] // far past the top threshold -- clamps at the top stage, never overflows past it
    public void Stacks_between_named_thresholds_hold_the_lower_stage(int stacks, int expectedStage)
    {
        Assert.Equal(expectedStage, NerveLadder.StageFor(stacks, spiritResolved: 1000, RealThresholds));
    }

    // ---- spirit exhausted at 0 stacks -> 2 (the spec's own second headline number) ----

    [Fact]
    public void Spirit_exhausted_at_zero_stacks_is_the_top_stage_regardless_of_count()
    {
        Assert.Equal(2, NerveLadder.StageFor(stacks: 0, spiritResolved: 0, RealThresholds));
    }

    [Fact]
    public void Spirit_exhausted_overrides_even_a_stack_count_that_would_otherwise_resolve_lower()
    {
        // stacks=1 alone would be stage 0 (see the theory above) -- exhausted spirit still wins.
        Assert.Equal(2, NerveLadder.StageFor(stacks: 1, spiritResolved: -50, RealThresholds));
    }

    [Fact]
    public void Spirit_just_above_zero_is_not_exhausted_and_reads_the_stack_count_normally()
    {
        Assert.Equal(0, NerveLadder.StageFor(stacks: 1, spiritResolved: 1, RealThresholds));
    }

    // ---- pure function: no runtime, no catalog, nothing but the three inputs ----

    [Fact]
    public void Identical_inputs_always_resolve_identically()
    {
        var a = NerveLadder.StageFor(3, 500, RealThresholds);
        var b = NerveLadder.StageFor(3, 500, RealThresholds);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Null_thresholds_throws()
    {
        Assert.Throws<ArgumentNullException>(() => NerveLadder.StageFor(1, 100, null!));
    }

    [Fact]
    public void Empty_thresholds_never_resolves_above_none()
    {
        Assert.Equal(-1, NerveLadder.StageFor(stacks: 999, spiritResolved: 1000, thresholds: Array.Empty<int>()));
    }
}
