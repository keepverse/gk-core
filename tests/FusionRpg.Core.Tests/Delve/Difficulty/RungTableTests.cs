using FusionRpg.Core.Delve.Difficulty;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Difficulty;

/// <summary>
/// D1.19 — <see cref="RungTable"/>'s façade and <see cref="RungValidator"/>'s own guard-test
/// surface. R8's twin-rejection is already proven at load time
/// (<c>DungeonTuningTests.Two_adjacent_rungs_identical_on_every_reward_column_reject</c>) — this
/// file covers what is unique to reading through the catalog/hub: ordinal contiguity, the
/// `hard`-is-identity invariant, and the façade members `encounter-generator`/`delve-attrition`
/// actually call.
/// </summary>
public class RungTableTests
{
    [Fact]
    public void All_returns_the_ten_rungs_in_ordinal_order_with_decision_sevens_deltas()
    {
        var all = RungTable.All();
        // The count is fully implied by the element-wise equality below -- never pinned separately
        // (population-pin SE3.3, 2026-09-19).
        Assert.Equal(
            new[] { "very-easy", "easy", "medium", "hard", "very-hard", "nightmare", "hell", "abyss", "hopeless", "impossible" },
            all.Select(r => r.RungId).ToArray());
        Assert.Equal(
            new[] { -2, -1, -1, 0, 0, 1, 1, 2, 2, 3 },
            all.Select(r => r.Def.BandDelta).ToArray());
    }

    [Fact]
    public void Get_of_an_unknown_rung_id_throws()
    {
        Assert.Throws<ArgumentException>(() => RungTable.Get("mythic"));
    }

    [Fact]
    public void OrdinalOf_matches_the_registrys_own_ordinal()
    {
        Assert.Equal(1, RungTable.OrdinalOf("very-easy"));
        Assert.Equal(4, RungTable.OrdinalOf("hard"));
        Assert.Equal(10, RungTable.OrdinalOf("impossible"));
    }

    [Fact]
    public void NextRungId_steps_one_ordinal_up_and_is_null_past_impossible()
    {
        Assert.Equal("easy", RungTable.NextRungId("very-easy"));
        Assert.Equal("hell", RungTable.NextRungId("nightmare"));
        Assert.Null(RungTable.NextRungId("impossible")); // the tail is TailLadder's, not a rung
    }

    [Fact]
    public void The_shipped_table_passes_contiguous_ordinal_validation()
    {
        var ex = Record.Exception(RungValidator.ValidateContiguousOrdinals);
        Assert.Null(ex);
    }

    [Fact]
    public void The_shipped_table_passes_hard_is_identity_validation()
    {
        var ex = Record.Exception(RungValidator.ValidateHardIsIdentity);
        Assert.Null(ex);
    }

    [Fact]
    public void Hard_is_the_identity_row_read_through_the_table_facade()
    {
        var hard = RungTable.Get("hard");
        Assert.Equal(0, hard.BandDelta);
        Assert.Equal(1000, hard.EliteWeightMultMilli);
        Assert.Equal(1000, hard.RestWeightMultMilli);
        Assert.Equal(1000, hard.RestHealMultMilli);
        Assert.Equal(1000, hard.HungerMultMilli);
        Assert.Equal(1000, hard.SpiritDrainMultMilli);
        Assert.Equal(1000, hard.MerchantMarkupMultMilli);
        Assert.Equal(0, hard.EnemyCountDeltaFight);
        Assert.Equal(0, hard.EnemyCountDeltaElite);
    }
}
