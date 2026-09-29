using FusionRpg.Core.Creatures;
using FusionRpg.Core.Delve.Wild;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Power;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Wild;

/// <summary>D4.8 (spec-wild-room.md §7) — `Cage`: the structural draw, occupant eligibility, the
/// one-band-toward-eager disposition shift, and the fight/threaten-less talk tree.</summary>
public class CageTests
{
    static CageTests()
    {
        var json = File.ReadAllText(Path.Combine(DungeonTestFiles.RegistryDir(), "disposition.v1.json"));
        DispositionCatalog.Configure(DispositionCatalog.Parse(json));
    }

    static readonly WildTalkEligibility AllEligible = new(
        CaptureOnly: false, NoFreeSlot: false, DeltaBandIsFarAbove: false,
        UnbankedMeetsSoulsFloor: true, SpiritPoolMeetsFloor: true,
        HoldsOfferableSupply: true, HasReleasableContractAboveFloor: true);

    // ---- IsCageRoom ----

    [Fact]
    public void A_roll_under_the_threshold_is_a_cage_room()
    {
        Assert.True(Cage.IsCageRoom(rolledMilli: 149, cageMilli: 150));
    }

    [Fact]
    public void A_roll_exactly_at_the_threshold_is_not_a_cage_room()
    {
        Assert.False(Cage.IsCageRoom(rolledMilli: 150, cageMilli: 150));
    }

    // ---- OccupantEligible ----

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void OccupantEligible_never_a_capture_only_species_never_the_top_rung(bool captureOnly, bool isTopRung, bool expected)
    {
        // A null rank is the SHIPPED state of every compiled-roster species, and the shipped floors sit
        // at the bottom rung — so these four expectations are simultaneously the pre-rank behaviour and
        // the pass-through proof: the rank floor narrows nothing here.
        Assert.Equal(expected, Cage.OccupantEligible(captureOnly, isTopRung, (CreatureRank?)null));
    }

    [Fact]
    public void A_raised_cage_floor_refuses_below_floor_and_rank_skipped_candidates()
    {
        WithFloorsAt(CreatureRank.Heirloom, () =>
        {
            Assert.False(Cage.OccupantEligible(false, false, CreatureRank.Chaff));
            Assert.False(Cage.OccupantEligible(false, false, null)); // skipped -> bottom rung -> refused
            Assert.True(Cage.OccupantEligible(false, false, CreatureRank.Heirloom));
            // The two structural rules still decide first, at any floor: rank can only narrow further.
            Assert.False(Cage.OccupantEligible(true, false, CreatureRank.Almanac));
            Assert.False(Cage.OccupantEligible(false, true, CreatureRank.Almanac));
        });
    }

    static readonly PowerTuning PricingTuning = PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, 0, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    [Fact]
    public void The_cages_price_stays_rarity_keyed_and_never_reads_rank()
    {
        // spec-species-rank.md §6's own rule, asserted rather than reviewed: "rank never prices".
        // `OfferPricing.Contract` takes rarity, thetaRoom and loyalty and nothing else, so a floor at the
        // TOP rung leaves the price for the same rarity byte-identical.
        var atBottom = OfferPricing.Contract(
            CreatureRarity.Fused, thetaRoom: 3, loyalty: 500, loyaltyMax: 1000, PricingTuning);

        WithFloorsAt(CreatureRank.Almanac, () =>
            Assert.Equal(atBottom, OfferPricing.Contract(
                CreatureRarity.Fused, thetaRoom: 3, loyalty: 500, loyaltyMax: 1000, PricingTuning)));
    }

    /// <summary>Configure every gate at <paramref name="floor"/>, run the body, then put the policy back
    /// to unconfigured (behaviourally the shipped bottom rung), so no later test inherits a raised gate.</summary>
    static void WithFloorsAt(CreatureRank floor, Action body)
    {
        CreatureRankFloors.Configure(new CreatureRankTuning(
            1, CreatureRarityLadder.All.Select(r => r.ToId()).ToList(), Array.Empty<CreatureRankCell>(),
            CreatureRankFloors.DeclaredGates.ToDictionary(g => g, _ => floor.ToId(), StringComparer.Ordinal)));
        try { body(); }
        finally { CreatureRankFloors.ResetToUnconfigured(); }
    }

    // ---- OccupantDispositionBase ----

    [Fact]
    public void A_hostile_occupant_shifts_one_band_toward_eager_to_wary()
    {
        Assert.Equal("wary", Cage.OccupantDispositionBase("hostile"));
    }

    [Fact]
    public void An_eager_occupant_stays_eager_the_rail_clamps_it_does_not_go_negative()
    {
        Assert.Equal("eager", Cage.OccupantDispositionBase("eager"));
    }

    // ---- Offered: section 2's tree without fight and threaten ----

    [Fact]
    public void Offered_never_contains_fight_or_threaten_at_step_1()
    {
        var offered = Cage.Offered(1, maxSteps: 2, AllEligible);
        Assert.DoesNotContain(WildVerb.Fight, offered);
        Assert.DoesNotContain(WildVerb.Threaten, offered);
    }

    [Fact]
    public void Offered_still_carries_flatter_and_every_eligible_offer_and_leave()
    {
        var offered = Cage.Offered(1, maxSteps: 2, AllEligible);
        Assert.Contains(WildVerb.Flatter, offered);
        Assert.Contains(WildVerb.OfferSouls, offered);
        Assert.Contains(WildVerb.OfferSpirit, offered);
        Assert.Contains(WildVerb.OfferSupply, offered);
        Assert.Contains(WildVerb.OfferContract, offered);
        Assert.Contains(WildVerb.Leave, offered);
    }

    [Fact]
    public void Offered_never_contains_fight_even_when_TalkTree_would_have_offered_it_at_step_2()
    {
        var offered = Cage.Offered(2, maxSteps: 2, AllEligible);
        Assert.DoesNotContain(WildVerb.Fight, offered);
        Assert.Contains(WildVerb.Leave, offered);
    }
}
