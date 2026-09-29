using FusionRpg.Core.Delve.Difficulty;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Difficulty;

/// <summary>
/// D1.20 — the picker's per-`(domain, player)` view (spec-difficulty-ladder.md §6). Refuse, never
/// clamp: a rung below the offered floor, or not yet unlocked, is omitted with a named reason; the
/// band is always a name, never the raw integer or a Θ.
/// </summary>
public class RungOfferTests
{
    static readonly PowerTuning Power = PowerTuningHub.Tuning;
    static DungeonTuning Dungeon => DungeonTuningHub.Tuning;
    static readonly ParentWorldTerms World = new(WorldTier: 1, ZombossLevel: 0, RealmsAdvanced: 2);

    static PlayerClears EverythingCleared() =>
        new(new HashSet<string>(RungTable.All().Select(r => r.RungId)), new HashSet<int> { 1, 2, 3, 4, 5 });

    static RungOfferRow Row(RungOfferSet set, string rungId) => set.Rungs.Single(r => r.RungId == rungId);

    [Fact]
    public void A_band_1_domain_offers_hard_and_up_only_when_fully_unlocked()
    {
        var domain = new DomainThetaInputs(EntranceBand: 1, IsOnceEntry: false);
        var set = RungOffer.For(Power, Dungeon, domain, World, EverythingCleared());

        foreach (var refused in new[] { "very-easy", "easy", "medium" })
        {
            var row = Row(set, refused);
            Assert.False(row.Offered);
            Assert.Equal(RungOfferRefusal.BandBelowFloor, row.Refusal);
            Assert.Null(row.Band);
            Assert.Null(row.BandName);
        }

        foreach (var offered in new[] { "hard", "very-hard", "nightmare", "hell", "abyss", "hopeless", "impossible" })
        {
            var row = Row(set, offered);
            Assert.True(row.Offered);
            Assert.Equal(RungOfferRefusal.None, row.Refusal);
            Assert.NotNull(row.Band);
            Assert.NotNull(row.BandName);
        }
    }

    [Fact]
    public void A_band_2_domain_offers_easy_and_up()
    {
        var domain = new DomainThetaInputs(EntranceBand: 2, IsOnceEntry: false);
        var set = RungOffer.For(Power, Dungeon, domain, World, EverythingCleared());

        Assert.Equal(RungOfferRefusal.BandBelowFloor, Row(set, "very-easy").Refusal);
        Assert.True(Row(set, "easy").Offered);
        Assert.True(Row(set, "impossible").Offered);
    }

    [Fact]
    public void A_band_3_domain_offers_the_whole_ladder()
    {
        var domain = new DomainThetaInputs(EntranceBand: 3, IsOnceEntry: false);
        var set = RungOffer.For(Power, Dungeon, domain, World, EverythingCleared());
        Assert.All(set.Rungs, r => Assert.True(r.Offered));
    }

    [Fact]
    public void No_offered_row_ever_carries_a_BandBelowFloor_refusal_there_is_no_clamp_path()
    {
        // The acceptance line verbatim: "a test asserts no clamp path exists" -- Offered and
        // Refusal are mutually exclusive by construction (RungOfferRow never sets both), proven
        // across every domain-band shape from 1 to 10.
        for (var band = 1; band <= 10; band++)
        {
            var domain = new DomainThetaInputs(EntranceBand: band, IsOnceEntry: false);
            var set = RungOffer.For(Power, Dungeon, domain, World, EverythingCleared());
            foreach (var row in set.Rungs)
            {
                if (row.Offered)
                {
                    Assert.Equal(RungOfferRefusal.None, row.Refusal);
                    Assert.NotNull(row.Band);
                }
                else
                {
                    Assert.NotEqual(RungOfferRefusal.None, row.Refusal);
                    Assert.Null(row.Band); // refused rows never carry a clamped/salvaged band value
                }
            }
        }
    }

    [Fact]
    public void With_no_clears_only_rungs_up_to_maxRungWithoutOath_are_offered()
    {
        var domain = new DomainThetaInputs(EntranceBand: 3, IsOnceEntry: false);
        var set = RungOffer.For(Power, Dungeon, domain, World, PlayerClears.None);

        Assert.True(Row(set, "abyss").Offered); // ordinal 8 == maxRungWithoutOath, freely offered
        var hopeless = Row(set, "hopeless");
        Assert.False(hopeless.Offered);
        Assert.Equal(RungOfferRefusal.NotUnlockedYet, hopeless.Refusal);
    }

    [Fact]
    public void The_effective_band_name_is_returned_never_the_ordinal()
    {
        var domain = new DomainThetaInputs(EntranceBand: 3, IsOnceEntry: false);
        var set = RungOffer.For(Power, Dungeon, domain, World, EverythingCleared());
        var hard = Row(set, "hard");
        Assert.Equal(3, hard.Band); // the underlying integer, exposed for callers that need it (e.g. persistence)
        Assert.False(int.TryParse(hard.BandName, out _)); // but the display field is never just the number
        Assert.Equal("Deep", hard.BandName); // dangerBand members: shallow(1) mid(2) deep(3) abyssal(4)
    }

    [Fact]
    public void EffectiveBandName_uses_the_last_member_plus_overflow_past_the_lists_end()
    {
        Assert.Equal("Shallow", RungOffer.EffectiveBandName(Dungeon, 1));
        Assert.Equal("Middling", RungOffer.EffectiveBandName(Dungeon, 2));
        Assert.Equal("Deep", RungOffer.EffectiveBandName(Dungeon, 3));
        Assert.Equal("Abyssal", RungOffer.EffectiveBandName(Dungeon, 4));
        Assert.Equal("Abyssal +1", RungOffer.EffectiveBandName(Dungeon, 5));
        Assert.Equal("Abyssal +9", RungOffer.EffectiveBandName(Dungeon, 13));
    }

    [Fact]
    public void Once_entry_flags_surface_on_the_offer_set()
    {
        var once = new DomainThetaInputs(EntranceBand: 3, IsOnceEntry: true);
        var set = RungOffer.For(Power, Dungeon, once, World, EverythingCleared());
        Assert.True(set.IsOnceEntry);
        Assert.Equal(Dungeon.Domain.OnceEntry.SealOnWipe, set.OnceSealOnWipe);
        Assert.Equal(Dungeon.Domain.OnceEntry.FailKeepsBossLoot, set.OnceFailKeepsBossLoot);

        var notOnce = new DomainThetaInputs(EntranceBand: 3, IsOnceEntry: false);
        Assert.False(RungOffer.For(Power, Dungeon, notOnce, World, EverythingCleared()).IsOnceEntry);
    }

    [Fact]
    public void Tail_steps_stop_offering_at_the_first_unmet_unlock_condition()
    {
        var domain = new DomainThetaInputs(EntranceBand: 3, IsOnceEntry: false);
        var clears = new PlayerClears(new HashSet<string>(RungTable.All().Select(r => r.RungId)), new HashSet<int> { 1 });
        var set = RungOffer.For(Power, Dungeon, domain, World, clears);

        Assert.Equal(1, set.TailSteps[0].N);
        Assert.True(set.TailSteps[0].Offered);
        Assert.Equal(2, set.TailSteps[1].N);
        Assert.True(set.TailSteps[1].Offered); // step 1 cleared, so step 2 is offered
        Assert.Equal(3, set.TailSteps[2].N);
        Assert.False(set.TailSteps[2].Offered); // step 2 not yet cleared
        Assert.Equal(RungOfferRefusal.NotUnlockedYet, set.TailSteps[2].Refusal);
        Assert.Equal(3, set.TailSteps.Count); // stops right after the first refusal, never pads further
    }
}
