using FusionRpg.Core.Delve.Difficulty;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Difficulty;

/// <summary>
/// D1.21 (tail half) — "impossible" is a name, not a ceiling (spec-difficulty-ladder.md §3). The
/// only absolute bound is <see cref="PowerLadder.MaxIndex"/>, a computed property of the loaded
/// curve; the picker refuses an `n` before composing rather than saturating.
/// </summary>
public class TailLadderTests
{
    static readonly PowerTuning Power = PowerTuningHub.Tuning;
    static DungeonTuning Dungeon => DungeonTuningHub.Tuning;
    static readonly ParentWorldTerms World = new(WorldTier: 1, ZombossLevel: 0, RealmsAdvanced: 2);
    static readonly DomainThetaInputs RichEntrance = new(EntranceBand: 3, IsOnceEntry: false);

    [Fact]
    public void N_equals_1_on_the_frozen_rung_composes_one_more_band_than_the_rungs_own_row_zero()
    {
        var rung10RowZero = RoomThetaComposer.Compose(Power, Dungeon, RichEntrance, RungTable.Get("impossible"), row: 0, tailPlus: 0, isBoss: false, World);
        var tailStepOne = TailLadder.TryBand(Power, Dungeon, RichEntrance, n: 1, isBoss: false, World);

        Assert.True(tailStepOne.Offered);
        Assert.Equal(rung10RowZero.Band + Dungeon.DifficultyTail.BandStepPerPlus, tailStepOne.Band);
    }

    [Fact]
    public void Tail_plus_5_composes_band_11_theta_110_matching_the_tables_own_linear_rate()
    {
        // See RoomThetaComposerTests' twin of this case for why 110, not the spec table's stated
        // 125 -- every other row of the same table confirms an exact 5-Θ-per-band rate that only
        // this one cell breaks.
        var step = TailLadder.TryBand(Power, Dungeon, RichEntrance, n: 5, isBoss: false, World);
        Assert.True(step.Offered);
        Assert.Equal(11, step.Band);
        Assert.Equal(110, step.Theta);
    }

    [Fact]
    public void Label_renders_the_shipped_labelFormat_with_n_substituted()
    {
        Assert.Equal("abyss +3", TailLadder.Label(Dungeon, 3));
        Assert.Equal("abyss +1", TailLadder.Label(Dungeon, 1));
    }

    [Fact]
    public void An_n_at_the_MaxIndex_boundary_is_refused_before_composing_never_saturating()
    {
        var maxIndex = new PowerLadder(Power).MaxIndex;

        // TryBand.Offered is monotonically non-increasing in n (Theta grows strictly with n, since
        // bandStepPerPlus > 0) -- binary-search the exact flip point rather than hardcoding a
        // derived guess that would silently go stale if a tuning value ever changes.
        int lo = 1, hi = 1_000_000_000;
        Assert.False(TailLadder.TryBand(Power, Dungeon, RichEntrance, hi, isBoss: false, World).Offered);
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (TailLadder.TryBand(Power, Dungeon, RichEntrance, mid, isBoss: false, World).Offered) lo = mid + 1;
            else hi = mid;
        }

        var lastOffered = TailLadder.TryBand(Power, Dungeon, RichEntrance, lo - 1, isBoss: false, World);
        var firstRefused = TailLadder.TryBand(Power, Dungeon, RichEntrance, lo, isBoss: false, World);

        Assert.True(lastOffered.Offered);
        Assert.True(lastOffered.Theta <= maxIndex);
        Assert.False(firstRefused.Offered);
        Assert.Null(lastOffered.MaxIndexAtRefusal);
        Assert.Equal(maxIndex, firstRefused.MaxIndexAtRefusal);
    }

    [Fact]
    public void TryBand_never_throws_for_any_n_from_1_up_to_a_billion()
    {
        // The composer's checked cast is the backstop (§3); TryBand's own pre-check must never let
        // an OverflowException escape to a caller for ANY n in the legal range -- proven by sampling
        // across the whole span rather than a single point.
        foreach (var n in new[] { 1, 2, 100, 10_000, 1_000_000, 100_000_000, 1_000_000_000 })
        {
            var ex = Record.Exception(() => TailLadder.TryBand(Power, Dungeon, RichEntrance, n, isBoss: false, World));
            Assert.Null(ex);
        }
    }
}
