using FusionRpg.Core.Delve.Wild;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Wild;

/// <summary>D4.2 (spec-wild-room.md §2) — `TalkTree.Offered`/`StanceShift` and `WildAutopilot.Answer`:
/// one path test per verb (the todo's own Verify line) plus the maxSteps bound and the
/// never-opens-a-talk-it-cannot-finish autopilot property.</summary>
public class TalkTreeTests
{
    static readonly WildTalkEligibility AllEligible = new(
        CaptureOnly: false, NoFreeSlot: false, DeltaBandIsFarAbove: false,
        UnbankedMeetsSoulsFloor: true, SpiritPoolMeetsFloor: true,
        HoldsOfferableSupply: true, HasReleasableContractAboveFloor: true);

    static readonly WildTalkEligibility NoneEligible = new(
        CaptureOnly: false, NoFreeSlot: false, DeltaBandIsFarAbove: true,
        UnbankedMeetsSoulsFloor: false, SpiritPoolMeetsFloor: false,
        HoldsOfferableSupply: false, HasReleasableContractAboveFloor: false);

    // ---- Offered: one path test per verb ----

    [Fact]
    public void Flatter_is_offered_at_step_1()
    {
        Assert.Contains(WildVerb.Flatter, TalkTree.Offered(1, 2, AllEligible));
    }

    [Fact]
    public void Flatter_is_never_offered_at_step_2()
    {
        Assert.DoesNotContain(WildVerb.Flatter, TalkTree.Offered(2, 2, AllEligible));
    }

    [Fact]
    public void Threaten_is_offered_at_step_1_when_delta_band_is_not_far_above()
    {
        Assert.Contains(WildVerb.Threaten, TalkTree.Offered(1, 2, AllEligible));
    }

    [Fact]
    public void Threaten_is_refused_at_far_above()
    {
        var eligibility = AllEligible with { DeltaBandIsFarAbove = true };
        Assert.DoesNotContain(WildVerb.Threaten, TalkTree.Offered(1, 2, eligibility));
    }

    [Fact]
    public void OfferSouls_is_offered_only_when_unbanked_meets_the_floor()
    {
        Assert.Contains(WildVerb.OfferSouls, TalkTree.Offered(1, 2, AllEligible));
        Assert.DoesNotContain(WildVerb.OfferSouls, TalkTree.Offered(1, 2, NoneEligible));
    }

    [Fact]
    public void OfferSpirit_is_offered_only_when_the_spirit_pool_meets_the_floor()
    {
        Assert.Contains(WildVerb.OfferSpirit, TalkTree.Offered(1, 2, AllEligible));
        Assert.DoesNotContain(WildVerb.OfferSpirit, TalkTree.Offered(1, 2, NoneEligible));
    }

    [Fact]
    public void OfferSupply_is_offered_only_when_the_pack_holds_an_offerable_supply()
    {
        Assert.Contains(WildVerb.OfferSupply, TalkTree.Offered(1, 2, AllEligible));
        Assert.DoesNotContain(WildVerb.OfferSupply, TalkTree.Offered(1, 2, NoneEligible));
    }

    [Fact]
    public void OfferContract_is_offered_only_when_a_releasable_contract_clears_the_floor()
    {
        Assert.Contains(WildVerb.OfferContract, TalkTree.Offered(1, 2, AllEligible));
        Assert.DoesNotContain(WildVerb.OfferContract, TalkTree.Offered(1, 2, NoneEligible));
    }

    [Fact]
    public void Fight_is_always_offered()
    {
        Assert.Contains(WildVerb.Fight, TalkTree.Offered(1, 2, NoneEligible));
        Assert.Contains(WildVerb.Fight, TalkTree.Offered(2, 2, NoneEligible));
    }

    [Fact]
    public void Leave_is_always_offered()
    {
        Assert.Contains(WildVerb.Leave, TalkTree.Offered(1, 2, NoneEligible));
        Assert.Contains(WildVerb.Leave, TalkTree.Offered(2, 2, NoneEligible));
    }

    // ---- Capture-only / no-slot gate every offer:*, before their own individual eligibility ----

    [Fact]
    public void A_capture_only_pack_offers_no_offer_verb_even_when_every_other_condition_holds()
    {
        var eligibility = AllEligible with { CaptureOnly = true };
        var offered = TalkTree.Offered(1, 2, eligibility);
        Assert.DoesNotContain(WildVerb.OfferSouls, offered);
        Assert.DoesNotContain(WildVerb.OfferSpirit, offered);
        Assert.DoesNotContain(WildVerb.OfferSupply, offered);
        Assert.DoesNotContain(WildVerb.OfferContract, offered);
        // Capture-only still leaves flatter/threaten/fight/leave available -- only the bind path is closed.
        Assert.Contains(WildVerb.Flatter, offered);
        Assert.Contains(WildVerb.Fight, offered);
    }

    [Fact]
    public void No_free_contract_slot_refuses_every_offer_before_any_soul_moves()
    {
        var eligibility = AllEligible with { NoFreeSlot = true };
        var offered = TalkTree.Offered(1, 2, eligibility);
        Assert.DoesNotContain(WildVerb.OfferSouls, offered);
        Assert.DoesNotContain(WildVerb.OfferSpirit, offered);
        Assert.DoesNotContain(WildVerb.OfferSupply, offered);
        Assert.DoesNotContain(WildVerb.OfferContract, offered);
    }

    // ---- wild.talk.maxSteps bounds the tree ----

    [Fact]
    public void Past_maxSteps_nothing_is_offered_at_all()
    {
        Assert.Empty(TalkTree.Offered(2, maxSteps: 1, AllEligible));
    }

    [Fact]
    public void Step_below_one_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TalkTree.Offered(0, 2, AllEligible));
    }

    // ---- IsStance / IsOffer ----

    [Theory]
    [InlineData(WildVerb.Flatter, true)]
    [InlineData(WildVerb.Threaten, true)]
    [InlineData(WildVerb.OfferSouls, false)]
    [InlineData(WildVerb.Fight, false)]
    [InlineData(WildVerb.Leave, false)]
    public void IsStance_is_true_only_for_flatter_and_threaten(WildVerb verb, bool expected)
    {
        Assert.Equal(expected, TalkTree.IsStance(verb));
    }

    [Theory]
    [InlineData(WildVerb.OfferSouls, true)]
    [InlineData(WildVerb.OfferSpirit, true)]
    [InlineData(WildVerb.OfferSupply, true)]
    [InlineData(WildVerb.OfferContract, true)]
    [InlineData(WildVerb.Flatter, false)]
    [InlineData(WildVerb.Fight, false)]
    public void IsOffer_is_true_only_for_the_four_offer_verbs(WildVerb verb, bool expected)
    {
        Assert.Equal(expected, TalkTree.IsOffer(verb));
    }

    // ---- StanceShift ----

    [Fact]
    public void Flatter_under_the_coin_threshold_shifts_negative_one()
    {
        Assert.Equal(-1, TalkTree.StanceShift(WildVerb.Flatter, flatterCoinUnderThreshold: true, deltaBandAtOrBelowEven: false));
    }

    [Fact]
    public void Flatter_at_or_over_the_coin_threshold_shifts_positive_one()
    {
        Assert.Equal(1, TalkTree.StanceShift(WildVerb.Flatter, flatterCoinUnderThreshold: false, deltaBandAtOrBelowEven: false));
    }

    [Fact]
    public void Threaten_at_or_below_even_shifts_negative_one()
    {
        Assert.Equal(-1, TalkTree.StanceShift(WildVerb.Threaten, flatterCoinUnderThreshold: false, deltaBandAtOrBelowEven: true));
    }

    [Fact]
    public void Threaten_above_even_shifts_positive_one()
    {
        Assert.Equal(1, TalkTree.StanceShift(WildVerb.Threaten, flatterCoinUnderThreshold: false, deltaBandAtOrBelowEven: false));
    }

    [Theory]
    [InlineData(WildVerb.OfferSouls)]
    [InlineData(WildVerb.Fight)]
    [InlineData(WildVerb.Leave)]
    public void StanceShift_on_a_non_stance_verb_throws(WildVerb verb)
    {
        Assert.Throws<ArgumentException>(() => TalkTree.StanceShift(verb, false, false));
    }

    // ---- WildAutopilot: never opens a talk it cannot finish ----

    [Fact]
    public void Autopilot_fight_rule_always_answers_fight()
    {
        Assert.Equal(WildVerb.Fight, WildAutopilot.Answer(WildAutopilot.RuleFight, "hostile"));
        Assert.Equal(WildVerb.Fight, WildAutopilot.Answer(WildAutopilot.RuleFight, "eager"));
    }

    [Fact]
    public void Autopilot_leave_hostile_rule_leaves_only_at_hostile()
    {
        Assert.Equal(WildVerb.Leave, WildAutopilot.Answer(WildAutopilot.RuleLeaveHostile, "hostile"));
        Assert.Equal(WildVerb.Fight, WildAutopilot.Answer(WildAutopilot.RuleLeaveHostile, "wary"));
        Assert.Equal(WildVerb.Fight, WildAutopilot.Answer(WildAutopilot.RuleLeaveHostile, "open"));
        Assert.Equal(WildVerb.Fight, WildAutopilot.Answer(WildAutopilot.RuleLeaveHostile, "eager"));
    }

    [Fact]
    public void An_unknown_autopilot_rule_throws_never_defaults_to_fight()
    {
        Assert.Throws<ArgumentException>(() => WildAutopilot.Answer("charge-blindly", "hostile"));
    }

    public static IEnumerable<object[]> EveryRuleAndBand()
    {
        foreach (var rule in WildAutopilot.KnownRules)
            foreach (var band in new[] { "eager", "open", "wary", "hostile" })
                yield return new object[] { rule, band };
    }

    [Theory]
    [MemberData(nameof(EveryRuleAndBand))]
    public void Autopilot_never_answers_a_stance_move_it_always_resolves_in_one_step(string rule, string band)
    {
        // The whole of the acceptance line "an autopilot party never opens a talk it cannot finish":
        // there is no second step to fail to reach, because Answer's range never includes a stance
        // move over the full closed rule set x every disposition band.
        var answer = WildAutopilot.Answer(rule, band);
        Assert.False(TalkTree.IsStance(answer));
    }
}
