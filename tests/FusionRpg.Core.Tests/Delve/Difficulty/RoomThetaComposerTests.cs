using FusionRpg.Core.Delve.Difficulty;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Difficulty;

/// <summary>
/// D1.18 — spec-difficulty-ladder.md §1's worked table, every row through the shipped
/// <see cref="PowerIndexComposer.ContentExplain"/> (no private arithmetic). Inputs match the spec's
/// own worked-numbers header exactly: a `rich` entrance (band 3), tier 1, two realms, zomboss 0,
/// `depth.rowsPerBandStep = 2`, `depth.bossBandDelta = 1` — all read from the real committed
/// `dungeon.v1.json` via <see cref="DungeonTuningHub"/>, never restated as a literal here.
/// </summary>
public class RoomThetaComposerTests
{
    static readonly PowerTuning Power = PowerTuningHub.Tuning;
    static DungeonTuning Dungeon => DungeonTuningHub.Tuning;
    static readonly ParentWorldTerms World = new(WorldTier: 1, ZombossLevel: 0, RealmsAdvanced: 2);
    static readonly DomainThetaInputs RichEntrance = new(EntranceBand: 3, IsOnceEntry: false);
    static readonly DomainThetaInputs RichOnceEntrance = new(EntranceBand: 3, IsOnceEntry: true);

    static RoomTheta Compose(DomainThetaInputs domain, string rungId, int row, int tailPlus = 0, bool isBoss = false) =>
        RoomThetaComposer.Compose(Power, Dungeon, domain, RungTable.Get(rungId), row, tailPlus, isBoss, World);

    [Fact]
    public void Row_zero_on_hard_composes_band_3_and_theta_70()
    {
        var result = Compose(RichEntrance, "hard", row: 0);
        Assert.Equal(3, result.Band);
        Assert.Equal(70, result.Theta);
    }

    [Fact]
    public void The_boss_room_of_an_eleven_row_hard_corridor_composes_band_9_and_theta_100()
    {
        var result = Compose(RichEntrance, "hard", row: 10, isBoss: true);
        Assert.Equal(9, result.Band);
        Assert.Equal(100, result.Theta);
    }

    [Fact]
    public void Row_zero_on_impossible_composes_band_6_and_theta_85()
    {
        var result = Compose(RichEntrance, "impossible", row: 0);
        Assert.Equal(6, result.Band);
        Assert.Equal(85, result.Theta);
    }

    [Fact]
    public void Once_entry_plus_7_on_hard_row_zero_composes_band_10_and_theta_105()
    {
        var result = Compose(RichOnceEntrance, "hard", row: 0);
        Assert.Equal(10, result.Band);
        Assert.Equal(105, result.Theta);
    }

    [Fact]
    public void Tail_abyss_plus_5_on_impossibles_rule_row_composes_band_11_and_theta_110()
    {
        // spec-difficulty-ladder.md §1's worked table states Θ 125 for this row, but every OTHER
        // row in that same table (bands 6,8,9,10,12,13 -> Θ 85,95,100,105,115,120) sits on an
        // exact rate of 5 Θ per band above the band-3/Θ-70 anchor; band 11 on that same line is
        // 110, not 125 -- a transcription slip in the doc, not a composer defect (corrected in the
        // spec alongside this test, 2026-09-05).
        var result = Compose(RichEntrance, "impossible", row: 0, tailPlus: 5);
        Assert.Equal(11, result.Band);
        Assert.Equal(110, result.Theta);
        Assert.Equal(5360, new PowerLadder(Power).Value(result.Theta)); // P(Θ) -- corrects the doc's stated 6,455
        Assert.Equal(7882, ContentScale.Milli(result.Theta, Power)); // contentScale ‰ -- corrects the doc's stated 9,492
    }

    // -----------------------------------------------------------------------------------------
    // The remaining rows of §1's table, not named in the mandatory golden list but shown in the
    // same worked table -- covered so the whole table is proven, not just the five call-outs.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Row_ten_last_corridor_on_hard_composes_band_8_and_theta_95()
    {
        var result = Compose(RichEntrance, "hard", row: 10);
        Assert.Equal(8, result.Band);
        Assert.Equal(95, result.Theta);
    }

    [Fact]
    public void The_boss_room_of_an_impossible_corridor_composes_band_12_and_theta_115()
    {
        var result = Compose(RichEntrance, "impossible", row: 10, isBoss: true);
        Assert.Equal(12, result.Band);
        Assert.Equal(115, result.Theta);
    }

    [Fact]
    public void Once_entry_plus_7_on_impossible_row_zero_composes_band_13_and_theta_120()
    {
        var result = Compose(RichOnceEntrance, "impossible", row: 0);
        Assert.Equal(13, result.Band);
        Assert.Equal(120, result.Theta);
    }

    // -----------------------------------------------------------------------------------------
    // Refuse-not-clamp (§6): a rung whose composed band falls below difficulty.minOfferedBand
    // throws RungNotOffered rather than reaching PowerIndexComposer.ClampNonNegative.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void A_band_that_would_floor_below_minOfferedBand_is_refused_not_clamped()
    {
        // very-easy has bandDelta -2; an entrance band of 1 composes 1 + 0 + -2 = -1, below the
        // floor (minOfferedBand 1) -- this must throw, never silently clamp to 0.
        var thinEntrance = new DomainThetaInputs(EntranceBand: 1, IsOnceEntry: false);
        var ex = Assert.Throws<RungNotOffered>(() => Compose(thinEntrance, "very-easy", row: 0));
        Assert.Equal("very-easy", ex.RungId);
        Assert.Equal(-1, ex.Band);
    }

    [Fact]
    public void A_band_exactly_at_the_floor_is_not_refused()
    {
        var thinEntrance = new DomainThetaInputs(EntranceBand: 2, IsOnceEntry: false);
        var result = Compose(thinEntrance, "easy", row: 0); // 2 + 0 + (-1) == minOfferedBand (1)
        Assert.Equal(1, result.Band);
    }

    [Fact]
    public void No_arithmetic_here_ever_reaches_ClampNonNegative()
    {
        // ContentExplain floors a negative AXIS (world tier, zomboss, realms) to 0 under its own
        // comment ("a missing progression row is absence, not corruption") -- band itself never
        // goes through that path because RoomThetaComposer refuses first. A refused compose call
        // throws before ContentContext is ever constructed, so there is no ClampNonNegative call
        // to observe from here -- this test documents that boundary via the refusal test above
        // rather than reaching into ContentExplain's internals.
        var thinEntrance = new DomainThetaInputs(EntranceBand: 1, IsOnceEntry: false);
        Assert.Throws<RungNotOffered>(() => Compose(thinEntrance, "very-easy", row: 0));
    }
}
