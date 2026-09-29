using FusionRpg.Core.Delve.Attrition;
using FusionRpg.Core.Dungeon.Tuning;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Attrition;

/// <summary>D2.18 (spec-delve-attrition.md §3) — `HungerCharge`: hunger per room from `hazardBand`,
/// no wall-clock term anywhere. `DungeonTuningHub` is configured for the whole assembly by
/// `Dungeon.DungeonHubTestBootstrap`'s module initializer.</summary>
public class HungerChargeTests
{
    static readonly DungeonTuning Tuning = DungeonTuningHub.Tuning;
    static readonly DifficultyRungTuning Hard = Tuning.Rungs["hard"]; // the identity row -- HungerMultMilli 1000

    // ---- a golden per hazard band, against the real shipped data ----

    [Theory]
    [InlineData("none", 0)]
    [InlineData("light", 40)]
    [InlineData("heavy", 90)]
    public void The_real_shipped_hazard_bands_carry_exactly_these_hungerPerMille_values(string band, long expectedPerMille)
    {
        Assert.Equal(expectedPerMille, Tuning.HazardBandHungerPerMille[band]);
    }

    [Fact]
    public void None_hazard_charges_zero_hunger_rest_and_boss_rooms()
    {
        var cost = HungerCharge.ForRoom("none", maxHunger: 1000, Tuning.HazardBandHungerPerMille, Hard);
        Assert.Equal(0, cost);
    }

    [Fact]
    public void Light_hazard_at_the_identity_rung_golden()
    {
        // 1000 * 40 * 1000 / 1_000_000 = 40
        var cost = HungerCharge.ForRoom("light", maxHunger: 1000, Tuning.HazardBandHungerPerMille, Hard);
        Assert.Equal(40, cost);
    }

    [Fact]
    public void Heavy_hazard_at_the_identity_rung_golden()
    {
        // 1000 * 90 * 1000 / 1_000_000 = 90
        var cost = HungerCharge.ForRoom("heavy", maxHunger: 1000, Tuning.HazardBandHungerPerMille, Hard);
        Assert.Equal(90, cost);
    }

    [Fact]
    public void A_rung_with_a_nonIdentity_hungerMultMilli_scales_the_charge_proportionally()
    {
        var doubledRung = Hard with { HungerMultMilli = 2000 };
        var cost = HungerCharge.ForRoom("heavy", maxHunger: 1000, Tuning.HazardBandHungerPerMille, doubledRung);
        Assert.Equal(180, cost); // 1000 * 90 * 2000 / 1_000_000
    }

    // ---- no overflow at large maxHunger ----

    [Fact]
    public void No_overflow_at_a_large_maxHunger()
    {
        var cost = HungerCharge.ForRoom("heavy", maxHunger: 1_000_000, Tuning.HazardBandHungerPerMille, Hard);
        Assert.Equal(90_000, cost);
    }

    // ---- the verify line's own headline: a cross-delve persistence property ----

    [Fact]
    public void Charges_compose_correctly_across_a_simulated_multi_delve_sequence()
    {
        // HungerCharge itself is pure and stateless (persistence is D2.23's own store write) -- what
        // IS this method's own property to prove is that repeated charges compose linearly, so a
        // caller carrying the ending value forward as the next delve's starting pool (S2-7's own
        // "hunger persists across delves") never double-charges or silently resets mid-chain.
        var maxHunger = 1000L;
        var pool = maxHunger;

        // Delve 1: two light rooms then extract (hunger NOT refilled at extraction -- only rest does that).
        pool -= HungerCharge.ForRoom("light", maxHunger, Tuning.HazardBandHungerPerMille, Hard);
        pool -= HungerCharge.ForRoom("light", maxHunger, Tuning.HazardBandHungerPerMille, Hard);
        Assert.Equal(920, pool); // 1000 - 40 - 40

        // Delve 2 starts exactly where delve 1 ended -- no reset to max, no wall clock refilling it.
        pool -= HungerCharge.ForRoom("heavy", maxHunger, Tuning.HazardBandHungerPerMille, Hard);
        Assert.Equal(830, pool); // 920 - 90
    }

    [Fact]
    public void A_rest_or_boss_room_never_charges_hunger_even_mid_chain()
    {
        var pool = 1000L;
        pool -= HungerCharge.ForRoom("light", pool, Tuning.HazardBandHungerPerMille, Hard);
        pool -= HungerCharge.ForRoom("none", pool, Tuning.HazardBandHungerPerMille, Hard); // a rest room mid-chain
        Assert.Equal(960, pool); // only the light room charged anything
    }

    // ---- refusals / validation ----

    [Fact]
    public void An_unknown_hazard_band_throws_by_name()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            HungerCharge.ForRoom("not-a-real-band", 1000, Tuning.HazardBandHungerPerMille, Hard));
        Assert.Contains("not-a-real-band", ex.Message);
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => HungerCharge.ForRoom(null!, 1000, Tuning.HazardBandHungerPerMille, Hard));
        Assert.Throws<ArgumentNullException>(() => HungerCharge.ForRoom("light", 1000, null!, Hard));
        Assert.Throws<ArgumentNullException>(() => HungerCharge.ForRoom("light", 1000, Tuning.HazardBandHungerPerMille, null!));
    }
}
