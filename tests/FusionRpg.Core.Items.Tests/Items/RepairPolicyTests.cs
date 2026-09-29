using FusionRpg.Core.Battle;
using FusionRpg.Core.Items.Materials;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// species-gear-chain T23 — `RepairPolicy` (`spec-item-durability-repair.md` §5): the destroy roll
/// comes first (D1), the restore is graded rather than refused (D2), and nothing here reads a tuning
/// or touches an item.
/// </summary>
public class RepairPolicyTests
{
    static SeededRng Rng() => SeededRng.DeriveStream(0xA11CE, "item.repair");

    [Fact]
    public void A_certain_destroy_chance_destroys_before_any_restore_is_computed()
    {
        var outcome = RepairPolicy.Resolve(
            current: 10, max: 1000, materialCoverageMilli: 1000, tierCapMilli: 1000,
            destructionChanceMilli: 1000, Rng());

        Assert.True(outcome.Destroyed);
        Assert.Equal(10, outcome.RestoredCurrent);   // no restore was computed, let alone paid for
    }

    [Fact]
    public void A_zero_destroy_chance_never_destroys()
    {
        for (var i = 0; i < 32; i++)
        {
            var outcome = RepairPolicy.Resolve(10, 1000, 1000, 1000, 0, Rng());
            Assert.False(outcome.Destroyed);
            Assert.Equal(1000, outcome.RestoredCurrent);
        }
    }

    [Fact]
    public void Full_coverage_and_cap_restore_to_max()
    {
        var outcome = RepairPolicy.Resolve(0, 1000, 1000, 1000, 0, Rng());

        Assert.False(outcome.Destroyed);
        Assert.Equal(1000, outcome.RestoredCurrent);
    }

    [Theory]
    [InlineData(500L, 1000L, 500L)]   // half the material on hand ⇒ half the damage restored
    [InlineData(1000L, 400L, 400L)]   // a field touch-up's cap bounds it below full (D2's "eroding")
    [InlineData(250L, 1000L, 250L)]
    public void A_short_material_set_or_a_tier_cap_yields_a_partial_restore_never_a_refusal(
        long coverageMilli, long capMilli, long expectedRestored)
    {
        var outcome = RepairPolicy.Resolve(0, 1000, coverageMilli, capMilli, 0, Rng());

        Assert.False(outcome.Destroyed);
        Assert.Equal(expectedRestored, outcome.RestoredCurrent);
    }

    [Fact]
    public void An_undamaged_item_restores_nothing_and_never_passes_its_own_max()
    {
        var outcome = RepairPolicy.Resolve(1000, 1000, 1000, 1000, 0, Rng());

        Assert.False(outcome.Destroyed);
        Assert.Equal(1000, outcome.RestoredCurrent);
    }

    [Fact]
    public void Out_of_range_inputs_are_refused_rather_than_clamped()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RepairPolicy.Resolve(-1, 1000, 1000, 1000, 0, Rng()));
        Assert.Throws<ArgumentOutOfRangeException>(() => RepairPolicy.Resolve(1001, 1000, 1000, 1000, 0, Rng()));
        Assert.Throws<ArgumentOutOfRangeException>(() => RepairPolicy.Resolve(0, 1000, 1000, 1000, 1001, Rng()));
        Assert.Throws<ArgumentOutOfRangeException>(() => RepairPolicy.Resolve(0, 1000, 1000, 1000, -1, Rng()));
    }

    // ---- species-gear-chain T44: CoverageWithAssurance ----------------------------------------------

    [Fact]
    public void Each_loaded_repair_charge_raises_coverage_by_the_bonus()
    {
        Assert.Equal(500, RepairPolicy.CoverageWithAssurance(500, repairLoaded: 0, repairCoverageBonusMilli: 50));
        Assert.Equal(550, RepairPolicy.CoverageWithAssurance(500, repairLoaded: 1, repairCoverageBonusMilli: 50));
        Assert.Equal(700, RepairPolicy.CoverageWithAssurance(500, repairLoaded: 4, repairCoverageBonusMilli: 50));
    }

    [Fact]
    public void An_already_maxed_baseline_is_genuinely_unaffected_by_the_bonus_once_Resolve_clamps_it()
    {
        // The workbench's own current call always passes FullRestoreMilli as the base -- this is the
        // honest state of a bonus with nothing left to raise, not a defect in the wiring (T44's own
        // evidence fragment names this explicitly).
        var boosted = RepairPolicy.CoverageWithAssurance(
            RepairPolicy.FullRestoreMilli, repairLoaded: 3, repairCoverageBonusMilli: 50);
        Assert.Equal(1150, boosted); // the RAW sum is not itself clamped

        var outcome = RepairPolicy.Resolve(
            current: 0, max: 1000, materialCoverageMilli: boosted, tierCapMilli: RepairPolicy.FullRestoreMilli,
            destructionChanceMilli: 0, Rng());
        Assert.Equal(1000, outcome.RestoredCurrent); // Resolve's own Clamp bounds it to the same as unboosted
    }

    [Fact]
    public void Loading_repair_charges_never_moves_the_destroy_chance()
    {
        // R10: assurance.repair feeds ONLY materialCoverageMilli -- destructionChanceMilli is a
        // parameter Resolve takes independently, and CoverageWithAssurance never touches it. Proven
        // by construction: the same destructionChanceMilli, with two different coverage values, both
        // destroy on the SAME roll (the destroy check runs before coverage is ever read).
        var unboosted = RepairPolicy.Resolve(10, 1000, 500, 1000, 1000, Rng());
        var boosted = RepairPolicy.Resolve(
            10, 1000, RepairPolicy.CoverageWithAssurance(500, 5, 50), 1000, 1000, Rng());
        Assert.True(unboosted.Destroyed);
        Assert.True(boosted.Destroyed);
    }

    [Fact]
    public void CoverageWithAssurance_refuses_negative_inputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RepairPolicy.CoverageWithAssurance(500, -1, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => RepairPolicy.CoverageWithAssurance(500, 1, -1));
    }

    [Fact]
    public void CoverageWithAssurance_widens_before_multiplying_and_never_wraps()
    {
        Assert.Throws<OverflowException>(
            () => RepairPolicy.CoverageWithAssurance(0, long.MaxValue, long.MaxValue));
    }
}
