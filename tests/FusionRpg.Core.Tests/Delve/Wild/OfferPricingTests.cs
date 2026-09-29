using System.IO;
using FusionRpg.Core.Delve.Loot;
using FusionRpg.Core.Delve.Wild;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Wild;

/// <summary>D4.3 (spec-wild-room.md §3) — the four `offer:*` equivalents. Reuses
/// `DelvePricesTests.cs`'s own exact-value `TuningAt`/`Pin=20` fixture, so every price is a
/// byte-exact golden against the real underlying policy, not an approximate sanity check.
/// `ContractPolicy` is already configured for the whole assembly by `ContractTuningTestBootstrap`'s
/// own `[ModuleInitializer]` -- no per-test setup needed here.</summary>
public class OfferPricingTests
{
    static PowerTuning TuningAt(long bMilli) => PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, bMilli, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    const int Pin = 20;
    static readonly PowerTuning Tuning = TuningAt(400);
    const long CostPerPull = 100; // summoning.v1.json's own "standard" banner, BannerTuning.cs:5

    // ---- Souls ----

    [Fact]
    public void Souls_matches_DelvePrices_OfferFloor_exactly()
    {
        var expected = DelvePrices.OfferFloor(CostPerPull, Pin, offerSoulsMilliOfPullPrice: 1500, Tuning);
        Assert.Equal(expected, OfferPricing.Souls(CostPerPull, Pin, 1500, Tuning));
    }

    [Fact]
    public void Souls_at_two_theta_values_the_floor_test()
    {
        var atPin = OfferPricing.Souls(CostPerPull, Pin, 1500, Tuning);
        var atHigherTheta = OfferPricing.Souls(CostPerPull, Pin + 100, 1500, Tuning);
        Assert.NotEqual(atPin, atHigherTheta);
        Assert.True(atHigherTheta > atPin); // a deeper room's floor never prices lower
    }

    // ---- Spirit ----

    [Fact]
    public void Spirit_derives_from_Souls_not_a_private_recomputation()
    {
        var floor = OfferPricing.Souls(CostPerPull, Pin, 1500, Tuning);
        var expected = floor * 1000 / 2000;
        Assert.Equal(expected, OfferPricing.Spirit(CostPerPull, Pin, 1500, spiritPerSoulMilli: 2000, Tuning));
    }

    [Fact]
    public void Spirit_genuinely_reads_spiritPerSoulMilli_not_a_hardcoded_rate()
    {
        var at2000 = OfferPricing.Spirit(CostPerPull, Pin, 1500, spiritPerSoulMilli: 2000, Tuning);
        var at1000 = OfferPricing.Spirit(CostPerPull, Pin, 1500, spiritPerSoulMilli: 1000, Tuning);
        Assert.NotEqual(at2000, at1000);
        Assert.Equal(at2000 * 2, at1000); // half the rate costs double the spirit for the same floor
    }

    [Fact]
    public void Spirit_refuses_a_non_positive_rate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OfferPricing.Spirit(CostPerPull, Pin, 1500, 0, Tuning));
        Assert.Throws<ArgumentOutOfRangeException>(() => OfferPricing.Spirit(CostPerPull, Pin, 1500, -5, Tuning));
    }

    // ---- Supply ----

    [Fact]
    public void Supply_always_refuses_price_undesigned_in_v1()
    {
        var r = OfferPricing.Supply("bait");
        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, r.Reason);
        Assert.Contains(DelvePriceRules.PriceUndesigned, r.Detail);
        Assert.Contains("bait", r.Detail);
    }

    // ---- Contract ----

    [Fact]
    public void Contract_matches_RitualPrice_scaled_by_loyalty_over_loyaltyMax_exactly()
    {
        var basePrice = ContractPolicy.RitualPrice(CreatureRarity.Cultivated, Pin, Tuning);
        var expected = basePrice * 300 / 1000;
        Assert.Equal(expected, OfferPricing.Contract(CreatureRarity.Cultivated, Pin, loyalty: 300, loyaltyMax: 1000, Tuning));
    }

    [Fact]
    public void Contract_at_full_loyalty_equals_the_unscaled_ritual_price()
    {
        var basePrice = ContractPolicy.RitualPrice(CreatureRarity.Heirloom, Pin, Tuning);
        Assert.Equal(basePrice, OfferPricing.Contract(CreatureRarity.Heirloom, Pin, loyalty: 1000, loyaltyMax: 1000, Tuning));
    }

    [Fact]
    public void Contract_genuinely_reads_rarity_not_a_hardcoded_one()
    {
        var chaff = OfferPricing.Contract(CreatureRarity.Chaff, Pin, 1000, 1000, Tuning);
        var almanac = OfferPricing.Contract(CreatureRarity.Almanac, Pin, 1000, 1000, Tuning);
        Assert.NotEqual(chaff, almanac);
        Assert.True(almanac > chaff); // Almanac's own RitualPriceSouls (500) is above Chaff's (50)
    }

    [Fact]
    public void Contract_refuses_a_non_positive_loyaltyMax()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OfferPricing.Contract(CreatureRarity.Chaff, Pin, 300, 0, Tuning));
    }

    [Fact]
    public void Contract_uses_the_real_shipped_LoyaltyMax_as_its_own_upper_bound()
    {
        Assert.Equal(1000, ContractPolicy.LoyaltyMax); // the fixture's own value -- pinned so a drift is visible here
    }

    // ---- "no arithmetic on a price outside DelvePrices" -- a source-scan guard, not an inference ----

    [Fact]
    public void OfferPricing_source_calls_no_SoulSinkPolicy_Price_directly()
    {
        // Every price in this file routes through DelvePrices.OfferFloor or ContractPolicy.RitualPrice
        // -- both of which already wrap SoulSinkPolicy.Price themselves. A direct SoulSinkPolicy.Price(
        // call here would mean a private, unreviewed re-derivation of the same scale.
        var path = Path.Combine(DungeonTestFiles.RepoRoot(), "src", "FusionRpg.Core", "Delve", "Wild", "OfferPricing.cs");
        var source = File.ReadAllText(path);
        Assert.DoesNotContain("SoulSinkPolicy.Price(", source);
    }
}
