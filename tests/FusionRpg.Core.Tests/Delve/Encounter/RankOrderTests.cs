using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Encounter;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Encounter;

/// <summary>D2.3 (spec-encounter-generator.md §4) — `RankOrder`: the reach mask and the
/// `targetPreference` default pick are a read model over the emit order `Encounter.Build` (D2.2)
/// already fixes; the 2-D board is explicitly not adopted (`PositionOf` stays null).</summary>
public class RankOrderTests
{
    static BattleActorSetup Setup(string key, long maxHp, CombatantKind kind = CombatantKind.Animate) =>
        new() { Key = key, Side = "wave", MaxHp = maxHp, Kind = kind };

    // ---- ReachMask ----

    [Theory]
    [InlineData(EncounterReach.Melee, 5, new[] { 0 })]
    [InlineData(EncounterReach.Short, 5, new[] { 0, 1 })]
    [InlineData(EncounterReach.Long, 5, new[] { 0, 1, 2, 3, 4 })]
    [InlineData(EncounterReach.Siege, 5, new[] { 0, 1, 2, 3, 4 })]
    public void ReachMask_matches_the_spec_table(EncounterReach reach, int rankCount, int[] expected) =>
        Assert.Equal(expected.ToHashSet(), RankOrder.ReachMask(reach, rankCount));

    /// <summary>⛔ The verify line's own headline: reach never selects across a span it cannot reach —
    /// a `short` reach against a lone defender (rankCount 1) is rank 0 only, never a phantom rank 1
    /// that does not exist on that side.</summary>
    [Theory]
    [InlineData(EncounterReach.Short, 1)]
    [InlineData(EncounterReach.Long, 1)]
    [InlineData(EncounterReach.Long, 3)]
    public void ReachMask_never_names_a_rank_beyond_the_defenders_own_count(EncounterReach reach, int rankCount)
    {
        var mask = RankOrder.ReachMask(reach, rankCount);
        Assert.All(mask, r => Assert.InRange(r, 0, rankCount - 1));
    }

    [Fact]
    public void ReachMask_rejects_a_non_positive_rankCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RankOrder.ReachMask(EncounterReach.Melee, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RankOrder.ReachMask(EncounterReach.Melee, -1));
    }

    // ---- DefaultPick ----

    [Fact]
    public void Frontline_picks_the_lowest_masked_rank()
    {
        var ranks = new[] { Setup("a", 100), Setup("b", 100), Setup("c", 100) };
        var pick = RankOrder.DefaultPick(TargetPreference.Frontline, new HashSet<int> { 0, 1, 2 }, ranks);
        Assert.Equal(0, pick);
    }

    [Fact]
    public void Backline_picks_the_highest_masked_rank()
    {
        var ranks = new[] { Setup("a", 100), Setup("b", 100), Setup("c", 100) };
        var pick = RankOrder.DefaultPick(TargetPreference.Backline, new HashSet<int> { 0, 1, 2 }, ranks);
        Assert.Equal(2, pick);
    }

    [Fact]
    public void Backline_within_a_narrower_mask_stays_within_it()
    {
        var ranks = new[] { Setup("a", 100), Setup("b", 100), Setup("c", 100) };
        var pick = RankOrder.DefaultPick(TargetPreference.Backline, new HashSet<int> { 0, 1 }, ranks); // short reach
        Assert.Equal(1, pick); // not 2 -- out of this mask
    }

    [Fact]
    public void Swarm_picks_the_smallest_MaxHp_in_the_mask()
    {
        var ranks = new[] { Setup("tank", 500), Setup("squishy", 50), Setup("mid", 200) };
        var pick = RankOrder.DefaultPick(TargetPreference.Swarm, new HashSet<int> { 0, 1, 2 }, ranks);
        Assert.Equal(1, pick);
    }

    [Fact]
    public void Elite_picks_the_largest_MaxHp_in_the_mask()
    {
        var ranks = new[] { Setup("tank", 500), Setup("squishy", 50), Setup("mid", 200) };
        var pick = RankOrder.DefaultPick(TargetPreference.Elite, new HashSet<int> { 0, 1, 2 }, ranks);
        Assert.Equal(0, pick);
    }

    [Fact]
    public void Structure_picks_the_first_structure_in_the_mask()
    {
        var ranks = new[] { Setup("a", 100), Setup("wall", 300, CombatantKind.Structure), Setup("b", 100) };
        var pick = RankOrder.DefaultPick(TargetPreference.Structure, new HashSet<int> { 0, 1, 2 }, ranks);
        Assert.Equal(1, pick);
    }

    [Fact]
    public void Structure_falls_back_to_frontline_when_no_structure_is_in_the_mask()
    {
        var ranks = new[] { Setup("a", 100), Setup("b", 100) };
        var pick = RankOrder.DefaultPick(TargetPreference.Structure, new HashSet<int> { 0, 1 }, ranks);
        Assert.Equal(0, pick);
    }

    [Fact]
    public void Indiscriminate_draws_uniformly_on_the_supplied_stream_and_stays_in_range()
    {
        var ranks = new[] { Setup("a", 100), Setup("b", 100), Setup("c", 100) };
        for (ulong seed = 0; seed < 50; seed++)
        {
            var pick = RankOrder.DefaultPick(TargetPreference.Indiscriminate, new HashSet<int> { 0, 1, 2 }, ranks,
                SeededRng.DeriveStream(seed, "indiscriminate-test"));
            Assert.InRange(pick, 0, 2);
        }
    }

    [Fact]
    public void Indiscriminate_is_deterministic_same_stream_same_pick()
    {
        var ranks = new[] { Setup("a", 100), Setup("b", 100), Setup("c", 100) };
        var mask = new HashSet<int> { 0, 1, 2 };
        var a = RankOrder.DefaultPick(TargetPreference.Indiscriminate, mask, ranks, SeededRng.DeriveStream(7, "s"));
        var b = RankOrder.DefaultPick(TargetPreference.Indiscriminate, mask, ranks, SeededRng.DeriveStream(7, "s"));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Indiscriminate_without_a_stream_throws()
    {
        var ranks = new[] { Setup("a", 100) };
        Assert.Throws<ArgumentNullException>(() =>
            RankOrder.DefaultPick(TargetPreference.Indiscriminate, new HashSet<int> { 0 }, ranks, stream: null));
    }

    [Fact]
    public void An_empty_mask_throws()
    {
        var ranks = new[] { Setup("a", 100) };
        Assert.Throws<ArgumentException>(() => RankOrder.DefaultPick(TargetPreference.Frontline, new HashSet<int>(), ranks));
    }

    [Fact]
    public void A_mask_entirely_outside_the_defenders_own_rank_count_throws()
    {
        var ranks = new[] { Setup("a", 100) };
        Assert.Throws<ArgumentException>(() => RankOrder.DefaultPick(TargetPreference.Frontline, new HashSet<int> { 5, 6 }, ranks));
    }

    [Fact]
    public void Null_arguments_throw()
    {
        var ranks = new[] { Setup("a", 100) };
        Assert.Throws<ArgumentNullException>(() => RankOrder.DefaultPick(TargetPreference.Frontline, null!, ranks));
        Assert.Throws<ArgumentNullException>(() => RankOrder.DefaultPick(TargetPreference.Frontline, new HashSet<int> { 0 }, null!));
    }

    // ---- WithRankSpan ----

    [Fact]
    public void WithRankSpan_sets_exactly_that_field_and_nothing_else()
    {
        var boss = Setup("wave:0", 5000);
        var spanned = RankOrder.WithRankSpan(boss, 2);

        Assert.Equal(2, spanned.RankSpan);
        Assert.Equal(boss with { RankSpan = 2 }, spanned); // every other field untouched
    }

    [Fact]
    public void WithRankSpan_rejects_a_span_below_1()
    {
        var boss = Setup("wave:0", 5000);
        Assert.Throws<ArgumentOutOfRangeException>(() => RankOrder.WithRankSpan(boss, 0));
    }

    [Fact]
    public void WithRankSpan_a_null_setup_throws()
    {
        Assert.Throws<ArgumentNullException>(() => RankOrder.WithRankSpan(null!, 1));
    }
}
