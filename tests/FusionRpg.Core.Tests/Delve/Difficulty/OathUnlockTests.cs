using FusionRpg.Core.Delve.Difficulty;
using FusionRpg.Core.Dungeon.Tuning;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Difficulty;

/// <summary>
/// D1.21 (Oath half) — R4: unlocking is by clearing (spec-difficulty-ladder.md §4). Rungs
/// `1…domain.maxRungWithoutOath` (shipped: `abyss`, ordinal 8) are offered freely; every rung above
/// needs a clear at the one below; the tail follows the same rule. The Oath itself unlocks nothing.
/// </summary>
public class OathUnlockTests
{
    static DungeonTuning Dungeon => DungeonTuningHub.Tuning;
    static readonly HashSet<string> NoClears = new();
    static readonly HashSet<int> NoTailClears = new();

    [Fact]
    public void The_shipped_max_rung_without_oath_is_abyss()
    {
        Assert.Equal("abyss", Dungeon.Domain.MaxRungWithoutOath);
    }

    [Theory]
    [InlineData("very-easy")]
    [InlineData("hard")]
    [InlineData("abyss")]
    public void Every_rung_up_to_and_including_maxRungWithoutOath_is_offered_with_no_clears_at_all(string rungId)
    {
        Assert.True(OathUnlock.IsRungOffered(Dungeon, rungId, NoClears));
    }

    [Theory]
    [InlineData("hopeless")]
    [InlineData("impossible")]
    public void A_rung_above_maxRungWithoutOath_is_not_offered_with_no_clears(string rungId)
    {
        Assert.False(OathUnlock.IsRungOffered(Dungeon, rungId, NoClears));
    }

    [Fact]
    public void A_clear_at_rung_8_abyss_opens_rung_9_hopeless()
    {
        var clears = new HashSet<string> { "abyss" };
        Assert.True(OathUnlock.IsRungOffered(Dungeon, "hopeless", clears));
        Assert.False(OathUnlock.IsRungOffered(Dungeon, "impossible", clears)); // hopeless itself still uncleared
    }

    [Fact]
    public void A_clear_at_hopeless_then_opens_impossible()
    {
        var clears = new HashSet<string> { "abyss", "hopeless" };
        Assert.True(OathUnlock.IsRungOffered(Dungeon, "impossible", clears));
    }

    [Fact]
    public void An_oath_clear_at_very_easy_opens_nothing_it_was_already_freely_offered()
    {
        // very-easy sits below maxRungWithoutOath, so it is freely offered with or without any
        // clear at all -- proving "opens nothing" means every rung's offered-state is IDENTICAL
        // whether or not this clear is recorded.
        var withoutClear = RungTable.All().Select(r => OathUnlock.IsRungOffered(Dungeon, r.RungId, NoClears)).ToList();
        var withOathClear = RungTable.All().Select(r => OathUnlock.IsRungOffered(Dungeon, r.RungId, new HashSet<string> { "very-easy" })).ToList();
        Assert.Equal(withoutClear, withOathClear);
    }

    [Fact]
    public void RecordClear_writes_the_oath_flag_but_OathUnlock_never_reads_it_for_gating()
    {
        var oathClear = OathUnlock.RecordClear(rungId: "very-easy", tailN: null, oath: true);
        Assert.True(oathClear.Oath);
        Assert.Equal("very-easy", oathClear.RungId);
        // The clear record itself carries no offer-logic side effect -- IsRungOffered only ever
        // reads the clearedRungIds set, never a ClearRecord, so an Oath clear cannot unlock by
        // construction (there is no code path from ClearRecord.Oath into IsRungOffered).
    }

    [Fact]
    public void A_clear_at_rung_10_impossible_opens_tail_step_abyss_plus_1()
    {
        Assert.True(OathUnlock.IsTailStepOffered(n: 1, rung10Cleared: true, NoTailClears));
        Assert.False(OathUnlock.IsTailStepOffered(n: 1, rung10Cleared: false, NoTailClears));
    }

    [Fact]
    public void Tail_step_n_needs_a_clear_at_n_minus_1_not_at_rung_10_again()
    {
        var clearedStepOne = new HashSet<int> { 1 };
        Assert.True(OathUnlock.IsTailStepOffered(n: 2, rung10Cleared: true, clearedStepOne));
        Assert.False(OathUnlock.IsTailStepOffered(n: 2, rung10Cleared: true, NoTailClears));
        Assert.False(OathUnlock.IsTailStepOffered(n: 3, rung10Cleared: true, clearedStepOne)); // step 2 still uncleared
    }
}
