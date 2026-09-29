using FusionRpg.Core.Delve.Loot;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Loot;

/// <summary>D3.13 (spec-dungeon-loot.md §6, "Sinks and prices — one function, three callers"). Reuses
/// `SoulEarnPolicyTests.cs`'s own exact-value `TuningAt`/`Pin=20` fixture, so every price is a byte-exact
/// golden against the real `SoulSinkPolicy.Price`, not an approximate sanity check.</summary>
public class DelvePricesTests
{
    static PowerTuning TuningAt(long bMilli) => PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, bMilli, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    const int Pin = 20;
    static readonly PowerTuning Tuning = TuningAt(400);

    // ---- Merchant ----

    [Fact]
    public void Merchant_null_tuning_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DelvePrices.Merchant(1000, Pin, 0, 1000, null!, out _));
    }

    [Fact]
    public void Merchant_with_no_derived_price_refuses_price_undesigned()
    {
        var r = DelvePrices.Merchant(basePriceSouls: null, Pin, 0, 1000, Tuning, out var price);
        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, r.Reason);
        Assert.Contains(DelvePriceRules.PriceUndesigned, r.Detail);
        Assert.Equal(0, price);
    }

    [Fact]
    public void Merchant_with_zero_markup_matches_SoulSinkPolicy_Price_exactly()
    {
        // markupMilli 0, rung mult 1000 (identity): (1000+0) * 1000 / 1_000_000 = 1 -- no scaling at all.
        var r = DelvePrices.Merchant(basePriceSouls: 5000, Pin, merchantMarkupMilli: 0, rungMerchantMarkupMultMilli: 1000, Tuning, out var price);
        Assert.True(r.IsOk);
        Assert.Equal(SoulSinkPolicy.Price(5000, Pin, Tuning), price);
    }

    [Fact]
    public void Merchant_markup_scales_the_base_price_before_the_theta_read()
    {
        // +200‰ markup, identity rung mult: base 5000 -> marked-up base 6000 (5000*1200/1000).
        var r = DelvePrices.Merchant(basePriceSouls: 5000, Pin, merchantMarkupMilli: 200, rungMerchantMarkupMultMilli: 1000, Tuning, out var price);
        Assert.True(r.IsOk);
        Assert.Equal(SoulSinkPolicy.Price(6000, Pin, Tuning), price);
    }

    [Fact]
    public void Merchant_rung_multiplier_also_scales_the_base_price()
    {
        // no markup, rung mult 1500 (1.5x): base 5000 -> marked-up base 7500.
        var r = DelvePrices.Merchant(basePriceSouls: 5000, Pin, merchantMarkupMilli: 0, rungMerchantMarkupMultMilli: 1500, Tuning, out var price);
        Assert.True(r.IsOk);
        Assert.Equal(SoulSinkPolicy.Price(7500, Pin, Tuning), price);
    }

    [Fact]
    public void Merchant_price_genuinely_tracks_the_callers_own_base_price_no_hidden_literal()
    {
        // Proves the "no price literal exists" acceptance line as an outcome: the SAME markup/rung/Θ
        // with two different caller-supplied base prices must produce two different, proportional
        // results -- a hidden internal constant would make this fail (either flat or wrong ratio).
        DelvePrices.Merchant(1000, Pin, 100, 1000, Tuning, out var priceA);
        DelvePrices.Merchant(2000, Pin, 100, 1000, Tuning, out var priceB);
        Assert.NotEqual(priceA, priceB);
        Assert.Equal(priceA * 2, priceB); // linear in the base price at a fixed Θ
    }

    // ---- PullPrice ----

    [Fact]
    public void PullPrice_null_tuning_throws()
    {
        Assert.Throws<ArgumentNullException>(() => DelvePrices.PullPrice(1000, Pin, null!));
    }

    [Fact]
    public void PullPrice_matches_SoulSinkPolicy_Price_exactly()
    {
        Assert.Equal(SoulSinkPolicy.Price(1500, Pin, Tuning), DelvePrices.PullPrice(1500, Pin, Tuning));
    }

    // ---- OfferFloor ----

    [Fact]
    public void OfferFloor_null_tuning_throws()
    {
        Assert.Throws<ArgumentNullException>(() => DelvePrices.OfferFloor(1000, Pin, 1000, null!));
    }

    [Fact]
    public void OfferFloor_at_exactly_1000_milli_equals_the_pull_price()
    {
        // The tunable's own documented floor ("offerSoulsMilliOfPullPrice >= 1000") -- at exactly the
        // floor, the offer costs exactly the pull price, never less.
        var pull = DelvePrices.PullPrice(2000, Pin, Tuning);
        Assert.Equal(pull, DelvePrices.OfferFloor(2000, Pin, offerSoulsMilliOfPullPrice: 1000, Tuning));
    }

    [Fact]
    public void OfferFloor_above_1000_milli_costs_more_than_the_pull_price()
    {
        var pull = DelvePrices.PullPrice(2000, Pin, Tuning);
        var offer = DelvePrices.OfferFloor(2000, Pin, offerSoulsMilliOfPullPrice: 1500, Tuning);
        Assert.Equal(pull * 3 / 2, offer);
        Assert.True(offer > pull);
    }

    // ---- Server-owned wild/cage join resolution ----

    [Fact]
    public void DelveJoinResolution_is_deterministic_and_prices_only_from_server_facts()
    {
        var dungeon = DungeonTuningHub.Tuning;
        var power = PowerTuningHub.Tuning;
        var first = DelveWildJoinResolver.Resolve(
            seed: 7UL, row: 0, col: 0, archetypeId: "room.wild-test-001", thetaRoom: Pin,
            dungeon, power);
        var replay = DelveWildJoinResolver.Resolve(
            seed: 7UL, row: 0, col: 0, archetypeId: "room.wild-test-001", thetaRoom: Pin,
            dungeon, power);

        Assert.True(first.Ok, first.Reason);
        Assert.True(replay.Ok, replay.Reason);
        Assert.Equal(first.Resolution!.SpeciesId, replay.Resolution!.SpeciesId);
        Assert.Equal(first.Resolution.Spec.TraitIds, replay.Resolution.Spec.TraitIds);
        var banner = SummonBannerCatalog.TryGet(dungeon.AltarBannerId)
            ?? SummonBannerCatalog.TryGet(SummonBannerCatalog.StandardRift)!;
        Assert.Equal(
            DelvePrices.OfferFloor(banner.CostPerPull, Pin, dungeon.WildOfferSoulsMilliOfPullPrice, power),
            first.Resolution.Price);
    }

    [Fact]
    public void DelveJoinResolution_chooses_only_a_catalog_species_that_clears_the_cage_gate()
    {
        var resolved = DelveWildJoinResolver.Resolve(
            seed: 19UL, row: 2, col: 3, archetypeId: "room.wild-test-002", thetaRoom: Pin + 1,
            DungeonTuningHub.Tuning, PowerTuningHub.Tuning);

        Assert.True(resolved.Ok, resolved.Reason);
        var species = CreatureSpeciesCatalog.Get(resolved.Resolution!.SpeciesId);
        Assert.True(FusionRpg.Core.Delve.Wild.Cage.OccupantEligible(
            species.Acquisition == CreatureAcquisition.CaptureOnly,
            species.BaseRarity == CreatureRarity.Sunwoven,
            species.Rank));
        Assert.Equal("delve", resolved.Resolution.Spec.Origin);
    }

    [Fact]
    public void DelveJoinResolution_null_tuning_throws_instead_of_defaulting()
    {
        Assert.Throws<ArgumentNullException>(() => DelveWildJoinResolver.Resolve(
            1UL, 0, 0, "room.wild-test-001", Pin, null!, Tuning));
        Assert.Throws<ArgumentNullException>(() => DelveWildJoinResolver.Resolve(
            1UL, 0, 0, "room.wild-test-001", Pin, DungeonTuningHub.Tuning, null!));
    }

    // ---- RecoveryRitual ----

    [Fact]
    public void RecoveryRitual_null_tuning_throws()
    {
        Assert.Throws<ArgumentNullException>(() => DelvePrices.RecoveryRitual(1000, Pin, null!));
    }

    [Fact]
    public void RecoveryRitual_matches_SoulSinkPolicy_Price_exactly()
    {
        Assert.Equal(SoulSinkPolicy.Price(800, Pin, Tuning), DelvePrices.RecoveryRitual(800, Pin, Tuning));
    }

    [Fact]
    public void RecoveryRitual_reads_the_wounding_delves_own_theta_run_not_a_fixed_one()
    {
        // Two different theta_run readings for the identical base price must give two different
        // prices -- proving the Θ argument is genuinely read, not a constant folded away.
        var atPin = DelvePrices.RecoveryRitual(800, Pin, Tuning);
        var atHigherTheta = DelvePrices.RecoveryRitual(800, Pin + 100, Tuning);
        Assert.NotEqual(atPin, atHigherTheta);
    }

    // ---- Provisioning (D3.30) ----

    [Fact]
    public void Provisioning_null_tuning_throws()
    {
        Assert.Throws<ArgumentNullException>(() => DelvePrices.Provisioning(1000, Pin, 0, null!));
    }

    [Fact]
    public void Provisioning_at_bandDelta_zero_matches_SoulSinkPolicy_Price_at_theta_entrance_exactly()
    {
        // hard's own identity row: bandDelta 0 -> Wm*0/1000 = 0 -> theta stays exactly theta_entrance.
        Assert.Equal(SoulSinkPolicy.Price(1000, Pin, Tuning), DelvePrices.Provisioning(1000, Pin, bandDelta: 0, Tuning));
    }

    [Fact]
    public void A_price_test_at_two_rungs_a_positive_bandDelta_prices_higher_a_negative_one_lower()
    {
        // Tuning's own WmMilli = 5000 (5.0): impossible-shaped (+3) adds 15 to theta; very-easy-shaped
        // (-2) subtracts 10 -- both real, spec-difficulty-ladder.md's own §11.9 box deltas.
        var identity = DelvePrices.Provisioning(1000, Pin, bandDelta: 0, Tuning);
        var harderRung = DelvePrices.Provisioning(1000, Pin, bandDelta: 3, Tuning);
        var easierRung = DelvePrices.Provisioning(1000, Pin, bandDelta: -2, Tuning);

        Assert.Equal(SoulSinkPolicy.Price(1000, Pin + 15, Tuning), harderRung);
        Assert.Equal(SoulSinkPolicy.Price(1000, Pin - 10, Tuning), easierRung);
        Assert.True(harderRung > identity);
        Assert.True(easierRung < identity);
    }

    [Fact]
    public void Provisioning_reuses_the_real_shipped_WmMilli_weight_not_a_private_constant()
    {
        Assert.Equal(5000, Tuning.Weights.WmMilli); // the fixture's own value, matching power-scale.v1.json
        var withDoubleWm = TuningAt(400) with { Weights = TuningAt(400).Weights with { WmMilli = 10000 } };
        var atNormalWm = DelvePrices.Provisioning(1000, Pin, bandDelta: 2, Tuning);
        var atDoubleWm = DelvePrices.Provisioning(1000, Pin, bandDelta: 2, withDoubleWm);
        Assert.NotEqual(atNormalWm, atDoubleWm); // reading tuning.Weights.WmMilli genuinely, not a hardcoded 5000
    }

    [Fact]
    public void Provisioning_refuses_when_WmMilli_is_not_configured()
    {
        var noWm = TuningAt(400) with { Weights = TuningAt(400).Weights with { WmMilli = null } };
        Assert.Throws<ArgumentException>(() => DelvePrices.Provisioning(1000, Pin, 2, noWm));
    }
}
