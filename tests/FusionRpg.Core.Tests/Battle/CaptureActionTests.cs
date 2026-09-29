using FusionRpg.Core.Delve.Wild;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Tests.Dungeon;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Capture;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>D4.6 (spec-wild-room.md §5) — `CaptureChance`/`CaptureAction`: a chance golden and the
/// A3 refusal reachable while absent (the todo's own two Verify lines).</summary>
public class CaptureActionTests
{
    static DungeonTuning RealTuning() =>
        DungeonTuningLoader.Parse(File.ReadAllText(DungeonTestFiles.DungeonTuningPath()), Registries());
    static DungeonRegistries Registries() => DungeonRegistryLoader.LoadAll(DungeonTestFiles.RegistryDir());

    // ---- HpBandOf ----

    [Theory]
    [InlineData(0, "low")]
    [InlineData(250, "low")]      // exactly at the low threshold -- inclusive
    [InlineData(251, "half")]
    [InlineData(500, "half")]     // exactly at the half threshold -- inclusive
    [InlineData(501, "high")]
    [InlineData(750, "high")]
    [InlineData(1000, "high")]    // above every threshold -- still the top band, never overflows
    public void HpBandOf_matches_the_real_shipped_thresholds(long hpMilli, string expectedBand)
    {
        var hpBandMilli = RealTuning().HpBandMilli;
        Assert.Equal(expectedBand, CaptureChance.HpBandOf(hpMilli, hpBandMilli));
    }

    // ---- DeltaBandOrdinal ----

    [Theory]
    [InlineData(-100, 0)] // far-below
    [InlineData(-41, 0)]
    [InlineData(-40, 1)]  // exactly at the first edge -- belongs to the band above it
    [InlineData(-16, 1)]  // below
    [InlineData(-15, 2)]  // exactly at the second edge -- even
    [InlineData(0, 2)]
    [InlineData(14, 2)]
    [InlineData(15, 3)]   // exactly at the third edge -- above
    [InlineData(39, 3)]
    [InlineData(40, 4)]   // exactly at the fourth edge -- far-above
    [InlineData(1000, 4)]
    public void DeltaBandOrdinal_matches_the_real_shipped_edges(int delta, int expectedOrdinal)
    {
        var edges = RealTuning().WildDeltaBands; // [-40, -15, 15, 40]
        Assert.Equal(expectedOrdinal, CaptureChance.DeltaBandOrdinal(delta, edges));
    }

    // ---- AdjustedDeltaBandOrdinal ----

    [Fact]
    public void AdjustedDeltaBandOrdinal_with_no_shift_and_no_attempts_is_the_identity()
    {
        var seal = RealTuning().Capture.SealTierShiftBands; // [0, -1, -2]
        Assert.Equal(2, CaptureChance.AdjustedDeltaBandOrdinal(2, sealTierIndex: 0, seal, attempts: 0, failStepBands: 1));
    }

    [Fact]
    public void A_higher_seal_tier_shifts_toward_far_below()
    {
        var seal = RealTuning().Capture.SealTierShiftBands; // t3 = -2
        Assert.Equal(0, CaptureChance.AdjustedDeltaBandOrdinal(2, sealTierIndex: 2, seal, attempts: 0, failStepBands: 1));
    }

    [Fact]
    public void Each_failed_attempt_shifts_toward_far_above_by_failStepBands()
    {
        var seal = RealTuning().Capture.SealTierShiftBands;
        var failStepBands = RealTuning().Capture.FailStepBands; // 1
        Assert.Equal(3, CaptureChance.AdjustedDeltaBandOrdinal(2, sealTierIndex: 0, seal, attempts: 1, failStepBands));
        Assert.Equal(4, CaptureChance.AdjustedDeltaBandOrdinal(2, sealTierIndex: 0, seal, attempts: 2, failStepBands));
    }

    [Fact]
    public void The_adjusted_ordinal_clamps_at_both_ends_the_index_rail_is_exempt()
    {
        var seal = RealTuning().Capture.SealTierShiftBands;
        Assert.Equal(4, CaptureChance.AdjustedDeltaBandOrdinal(4, sealTierIndex: 0, seal, attempts: 50, failStepBands: 1));
        Assert.Equal(0, CaptureChance.AdjustedDeltaBandOrdinal(0, sealTierIndex: 2, seal, attempts: 0, failStepBands: 1));
    }

    [Fact]
    public void An_out_of_range_seal_tier_index_throws()
    {
        var seal = RealTuning().Capture.SealTierShiftBands;
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptureChance.AdjustedDeltaBandOrdinal(2, sealTierIndex: 3, seal, 0, 1));
    }

    [Fact]
    public void Negative_attempts_throws()
    {
        var seal = RealTuning().Capture.SealTierShiftBands;
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptureChance.AdjustedDeltaBandOrdinal(2, 0, seal, attempts: -1, failStepBands: 1));
    }

    // ---- Compute: the chance golden, against the real shipped table ----

    [Fact]
    public void Compute_matches_the_real_shipped_chanceMilli_table_a_chance_golden()
    {
        var capture = RealTuning().Capture;
        // dungeon.v1.json's own low/far-below row: 850. lone's own statusBonusMilli: 0.
        var chance = CaptureChance.Compute("low", "far-below", "lone",
            capture.ChanceMilliByHpBandThenDeltaBand, capture.StatusBonusMilliByCountBand);
        Assert.Equal(850, chance);
    }

    [Fact]
    public void Compute_adds_the_status_bonus_on_top_of_the_base_chance()
    {
        var capture = RealTuning().Capture;
        // high/far-above row: 20; "many" bonus: 150 -> 170.
        var chance = CaptureChance.Compute("high", "far-above", "many",
            capture.ChanceMilliByHpBandThenDeltaBand, capture.StatusBonusMilliByCountBand);
        Assert.Equal(170, chance);
    }

    [Fact]
    public void Compute_genuinely_reads_countBand_not_a_hardcoded_bonus()
    {
        var capture = RealTuning().Capture;
        var lone = CaptureChance.Compute("low", "even", "lone", capture.ChanceMilliByHpBandThenDeltaBand, capture.StatusBonusMilliByCountBand);
        var many = CaptureChance.Compute("low", "even", "many", capture.ChanceMilliByHpBandThenDeltaBand, capture.StatusBonusMilliByCountBand);
        Assert.NotEqual(lone, many);
        Assert.True(many > lone);
    }

    [Fact]
    public void Compute_throws_on_an_unknown_band_at_any_of_the_three_axes()
    {
        var capture = RealTuning().Capture;
        Assert.Throws<ArgumentException>(() => CaptureChance.Compute("nope", "even", "lone", capture.ChanceMilliByHpBandThenDeltaBand, capture.StatusBonusMilliByCountBand));
        Assert.Throws<ArgumentException>(() => CaptureChance.Compute("low", "nope", "lone", capture.ChanceMilliByHpBandThenDeltaBand, capture.StatusBonusMilliByCountBand));
        Assert.Throws<ArgumentException>(() => CaptureChance.Compute("low", "even", "nope", capture.ChanceMilliByHpBandThenDeltaBand, capture.StatusBonusMilliByCountBand));
    }

    // ---- CaptureAction.TryGate: the refusal is reachable while A3 is absent ----

    [Fact]
    public void TryGate_refuses_capture_not_landed_today()
    {
        var ok = CaptureAction.TryGate(out var refusalId);
        Assert.False(ok);
        Assert.Equal(CaptureRefusal.NotLanded, refusalId);
    }

    // ---- CaptureAction.Resolve ----

    [Fact]
    public void Resolve_is_a_strict_less_than_a_roll_exactly_at_the_chance_fails()
    {
        Assert.False(CaptureAction.Resolve(chanceMilli: 500, rolledMilli: 500));
        Assert.True(CaptureAction.Resolve(chanceMilli: 500, rolledMilli: 499));
        Assert.False(CaptureAction.Resolve(chanceMilli: 500, rolledMilli: 501));
    }

    // ---- solid-remediation T4.7 (D12): the relocation's own contract -------------------------

    [Fact]
    public void Capture_lives_in_the_engine_not_in_the_delve()
    {
        // The owner's ruling, asserted mechanically: "any feature that changes battle mechanism is an
        // extension of battle engine and must be built on the top of battle engine." Before T4.7 the
        // delve owned this mechanism while the ENGINE already owned its randomness — BattleRunState
        // derives the "capture" stream — which is the split that made the ownership wrong rather than
        // merely oddly-filed.
        Assert.Equal("FusionRpg.Core.Battle.Capture", typeof(CaptureAction).Namespace);
        Assert.Equal("FusionRpg.Core.Battle.Capture", typeof(CaptureChance).Namespace);
        Assert.Equal("FusionRpg.Core.Battle.Capture", typeof(CaptureRefusal).Namespace);

        Assert.DoesNotContain("Delve", typeof(CaptureAction).Namespace!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_relocation_changed_nothing_about_reachability_or_the_refusal()
    {
        // The three clauses T4.7 must NOT move. Relocating a mechanism and completing a feature are
        // different work with different owners; this test is what keeps the move honest, because a
        // "relocation" that quietly made capture reachable would be a feature pass wearing a
        // refactor's name.
        Assert.False(CrossProgramLandedFlags.ItemCostRowLanded);

        var ok = CaptureAction.TryGate(out var refusalId);
        Assert.False(ok);
        Assert.Equal("capture.not-landed", refusalId);
    }
}
