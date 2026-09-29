using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Attrition;
using FusionRpg.Core.Battle.Attrition;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Attrition;

/// <summary>D2.21 (spec-delve-attrition.md §8) — `ExtractionSettlement.PartyStands`/`.IsWiped`: "a
/// party stands while one member has hp &gt; 0 and is not downed... every party of the raid has no
/// standing member" (verbatim).</summary>
public class ExtractionSettlementTests
{
    static DelveMemberState Member(long hp, bool downed = false) => new(
        InstanceId: Guid.NewGuid().ToString(),
        Pools: new Dictionary<string, long> { ["hp"] = hp, ["stamina"] = 0, ["hunger"] = 0, ["spirit"] = 0, ["qi"] = 0, ["poise"] = 0 },
        Statuses: Array.Empty<BattleStatusSpec>(), Shield: null, NerveStacks: 0, Downed: downed, DownedOnce: false);

    // ---- PartyStands ----

    [Fact]
    public void A_party_with_one_live_undowned_member_stands()
    {
        var party = new[] { Member(hp: 0, downed: true), Member(hp: 500) };
        Assert.True(ExtractionSettlement.PartyStands(party));
    }

    [Fact]
    public void A_party_where_every_member_is_at_zero_hp_does_not_stand()
    {
        var party = new[] { Member(hp: 0), Member(hp: 0) };
        Assert.False(ExtractionSettlement.PartyStands(party));
    }

    [Fact]
    public void Positive_hp_alone_is_not_enough_downed_still_does_not_stand()
    {
        // The spec's own two-part rule: hp > 0 AND not downed. A member revived to positive hp but
        // whose `Downed` flag has not yet cleared must not count as standing.
        var party = new[] { Member(hp: 500, downed: true) };
        Assert.False(ExtractionSettlement.PartyStands(party));
    }

    [Fact]
    public void An_empty_party_does_not_stand()
    {
        Assert.False(ExtractionSettlement.PartyStands(Array.Empty<DelveMemberState>()));
    }

    [Fact]
    public void PartyStands_null_throws()
    {
        Assert.Throws<ArgumentNullException>(() => ExtractionSettlement.PartyStands(null!));
    }

    [Fact]
    public void PartyStands_a_member_missing_the_hp_pool_throws_by_name()
    {
        var badMember = Member(hp: 500) with { Pools = new Dictionary<string, long> { ["stamina"] = 0 } };
        var ex = Assert.Throws<ArgumentException>(() => ExtractionSettlement.PartyStands(new[] { badMember }));
        Assert.Contains(badMember.InstanceId, ex.Message);
    }

    // ---- IsWiped ----

    [Fact]
    public void A_raid_is_wiped_when_every_party_has_no_standing_member()
    {
        var parties = new IReadOnlyList<DelveMemberState>[]
        {
            new[] { Member(hp: 0), Member(hp: 0, downed: true) },
            new[] { Member(hp: 0) },
        };
        Assert.True(ExtractionSettlement.IsWiped(parties));
    }

    [Fact]
    public void A_raid_is_not_wiped_while_even_one_party_still_stands()
    {
        var parties = new IReadOnlyList<DelveMemberState>[]
        {
            new[] { Member(hp: 0), Member(hp: 0, downed: true) }, // this party is wiped...
            new[] { Member(hp: 500) },                            // ...but this one still stands
        };
        Assert.False(ExtractionSettlement.IsWiped(parties));
    }

    [Fact]
    public void A_single_solo_party_wipes_on_its_own_state_alone()
    {
        Assert.True(ExtractionSettlement.IsWiped(new IReadOnlyList<DelveMemberState>[] { new[] { Member(hp: 0) } }));
        Assert.False(ExtractionSettlement.IsWiped(new IReadOnlyList<DelveMemberState>[] { new[] { Member(hp: 500) } }));
    }

    [Fact]
    public void IsWiped_null_throws()
    {
        Assert.Throws<ArgumentNullException>(() => ExtractionSettlement.IsWiped(null!));
    }

    [Fact]
    public void IsWiped_zero_parties_throws_rather_than_guessing()
    {
        Assert.Throws<ArgumentException>(() => ExtractionSettlement.IsWiped(Array.Empty<IReadOnlyList<DelveMemberState>>()));
    }
}

/// <summary>D2.22 (spec-delve-attrition.md §7, §9) — `ExtractionSettlement.Decide`: per-member
/// Retire/Recover(n)/Roster, and `won`, decided together and pure.</summary>
public class ExtractionSettlementDecideTests
{
    const int RecoveryDelves = 2; // risk.downedRecoveryDelves's own real starting shape

    // ---- Outcome: the §7 truth table over downedOnce x permadeathApplies ----

    [Fact]
    public void Never_downed_is_always_Roster_with_zero_recovery_regardless_of_the_rung()
    {
        var notPermadeath = MemberSettlementRules.Decide(downedOnce: false, ladder: NeverPermadeath.Instance, downedRecoveryDelves: RecoveryDelves, afflicted: false, extracted: true, bossKilled: true, routeAtLeastHalfCleared: false);
        var permadeath = MemberSettlementRules.Decide(downedOnce: false, ladder: AlwaysPermadeath, downedRecoveryDelves: RecoveryDelves, afflicted: false, extracted: true, bossKilled: true, routeAtLeastHalfCleared: false);

        Assert.Equal(SettlementOutcome.Roster, notPermadeath.Outcome);
        Assert.Equal(0, notPermadeath.RecoverDelves);
        Assert.Equal(SettlementOutcome.Roster, permadeath.Outcome);
        Assert.Equal(0, permadeath.RecoverDelves);
    }

    [Fact]
    public void DownedOnce_on_a_permadeath_rung_Retires_with_zero_recovery()
    {
        var result = MemberSettlementRules.Decide(downedOnce: true, ladder: AlwaysPermadeath, downedRecoveryDelves: RecoveryDelves, afflicted: false, extracted: true, bossKilled: true, routeAtLeastHalfCleared: false);

        Assert.Equal(SettlementOutcome.Retire, result.Outcome);
        Assert.Equal(0, result.RecoverDelves);
    }

    [Fact]
    public void DownedOnce_below_the_permadeath_gate_Recovers_for_the_tunable_count()
    {
        var result = MemberSettlementRules.Decide(downedOnce: true, ladder: NeverPermadeath.Instance, downedRecoveryDelves: RecoveryDelves, afflicted: false, extracted: true, bossKilled: true, routeAtLeastHalfCleared: false);

        Assert.Equal(SettlementOutcome.Recover, result.Outcome);
        Assert.Equal(RecoveryDelves, result.RecoverDelves);
    }

    [Fact]
    public void RevivedThenExtractedOnAPermadeathRungIsStillRetired()
    {
        // R3, verbatim: "the revive lets it finish the run, not escape the rule." Modeled here as
        // downedOnce=true regardless of the member's CURRENT (post-revive) hp/downed state -- Decide
        // never even takes a current-hp parameter, so there is no way for a revive to hide from it.
        var result = MemberSettlementRules.Decide(downedOnce: true, ladder: AlwaysPermadeath, downedRecoveryDelves: RecoveryDelves, afflicted: false, extracted: true, bossKilled: true, routeAtLeastHalfCleared: true);
        Assert.Equal(SettlementOutcome.Retire, result.Outcome);
    }

    // ---- Won: the §9 truth table, independent of Outcome ----

    [Theory]
    [InlineData(true, true, false, false, true)]   // extracted + boss killed + not afflicted -> won
    [InlineData(true, false, true, false, true)]   // extracted + half route + not afflicted -> won
    [InlineData(true, true, true, false, true)]    // both win conditions -> still won
    [InlineData(false, true, true, false, false)]  // not extracted (a wipe) -> never won, whatever else
    [InlineData(true, false, false, false, false)] // extracted but neither win condition -> not won
    [InlineData(true, true, false, true, false)]   // extracted + boss killed but afflicted -> not won
    [InlineData(true, false, true, true, false)]   // extracted + half route but afflicted -> not won
    public void Won_matches_the_spec_truth_table(bool extracted, bool bossKilled, bool routeHalf, bool afflicted, bool expectedWon)
    {
        var result = MemberSettlementRules.Decide(downedOnce: false, ladder: NeverPermadeath.Instance, downedRecoveryDelves: RecoveryDelves, afflicted: afflicted, extracted: extracted, bossKilled: bossKilled, routeAtLeastHalfCleared: routeHalf);
        Assert.Equal(expectedWon, result.Won);
    }

    [Fact]
    public void Won_is_independent_of_downedOnce_and_the_permadeath_gate()
    {
        // A member can be Retired/Recovering and still have "won" the delve for loyalty purposes --
        // these are two separate axes, never coupled.
        var retiredButWon = MemberSettlementRules.Decide(downedOnce: true, ladder: AlwaysPermadeath, downedRecoveryDelves: RecoveryDelves, afflicted: false, extracted: true, bossKilled: true, routeAtLeastHalfCleared: false);
        Assert.True(retiredButWon.Won);
    }

    // ---- validation ----

    [Fact]
    public void A_nonPositive_recovery_count_throws_only_when_it_would_actually_be_used()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MemberSettlementRules.Decide(downedOnce: true, ladder: NeverPermadeath.Instance, downedRecoveryDelves: 0, afflicted: false, extracted: true, bossKilled: true, routeAtLeastHalfCleared: false));

        // Roster and Retire never read the count -- a caller that has not resolved the tunable for a
        // member who will not Recover must not be punished for it.
        var rosterOutcome = MemberSettlementRules.Decide(downedOnce: false, ladder: NeverPermadeath.Instance, downedRecoveryDelves: 0, afflicted: false, extracted: true, bossKilled: true, routeAtLeastHalfCleared: false);
        var retireOutcome = MemberSettlementRules.Decide(downedOnce: true, ladder: AlwaysPermadeath, downedRecoveryDelves: 0, afflicted: false, extracted: true, bossKilled: true, routeAtLeastHalfCleared: false);
        Assert.Equal(SettlementOutcome.Roster, rosterOutcome.Outcome);
        Assert.Equal(SettlementOutcome.Retire, retireOutcome.Outcome);
    }

    // ---- the verify line's own headline: a loyalty double-apply test ----

    [Fact]
    public void Deciding_the_same_inputs_twice_never_produces_a_different_answer()
    {
        // Decide is pure: a caller that (by accident, a retry, or a race) invokes it twice for the
        // same member gets the IDENTICAL settlement both times -- so "loyalty is applied once" can
        // never be undermined by Decide itself returning a different `Won` on a second call. The
        // actual once-per-delve ENFORCEMENT is the store call site's own job (D2.23); this is the
        // half of that guarantee this pure function can make on its own.
        var first = MemberSettlementRules.Decide(downedOnce: true, ladder: NeverPermadeath.Instance, downedRecoveryDelves: RecoveryDelves, afflicted: true, extracted: true, bossKilled: true, routeAtLeastHalfCleared: true);
        var second = MemberSettlementRules.Decide(downedOnce: true, ladder: NeverPermadeath.Instance, downedRecoveryDelves: RecoveryDelves, afflicted: true, extracted: true, bossKilled: true, routeAtLeastHalfCleared: true);

        Assert.Equal(first, second); // MemberSettlement is a record -- structural equality
    }

    // ---- solid-remediation T4.8 (D10): the promotion's own contract --------------------------

    [Fact]
    public void The_settlement_decision_is_engine_vocabulary_not_a_delve_concept()
    {
        // D10: whether a downed member is rostered, recovering or retired is a question every mode
        // asks. While it lived in the delve, every other mode either had no answer or would grow a
        // second one — which is the defect, not a missing mechanism (injury was already an ActorHub
        // subsystem). Promotion, not construction.
        Assert.Equal("FusionRpg.Core.Battle.Attrition", typeof(MemberSettlementRules).Namespace);
        Assert.Equal("FusionRpg.Core.Battle.Attrition", typeof(SettlementOutcome).Namespace);
        Assert.Equal("FusionRpg.Core.Battle.Attrition", typeof(MemberSettlement).Namespace);
    }

    [Fact]
    public void The_engine_rule_takes_no_delve_type_which_is_what_keeps_the_ladder_the_delves()
    {
        // The acceptance says the delve consumes this "with its difficulty ladder unchanged". What
        // makes that possible is that `permadeathApplies` arrives already resolved: the rule never
        // touches difficulty-ladder's own types, so a mode with a different ladder — or none —
        // answers the same question with its own input. Asserted structurally, because a single
        // delve-typed parameter would silently re-couple them.
        var parameters = typeof(MemberSettlementRules)
            .GetMethod(nameof(MemberSettlementRules.Decide))!
            .GetParameters();

        Assert.All(parameters, p =>
            Assert.DoesNotContain("Delve", p.ParameterType.FullName ?? "", StringComparison.Ordinal));
    }

    [Fact]
    public void The_party_and_wipe_checks_deliberately_stayed_in_the_delve()
    {
        // The other direction of the same rule. These read DelveMemberState — a delve party shape —
        // so promoting them would drag a mode's vocabulary INTO the engine, which is D10's own defect
        // pointing backwards.
        Assert.Equal("FusionRpg.Core.Delve.Attrition", typeof(ExtractionSettlement).Namespace);
    }

    // ---- solid-remediation T4.9: the ladder is an input, and its absence refuses ---------------

    /// <summary>A mode whose ladder always retires — the counterpart to NeverPermadeath, so a test
    /// states its ladder as plainly as production must.</summary>
    sealed class AlwaysPermadeathLadder : IPermadeathLadder
    {
        public bool PermadeathApplies() => true;
    }

    static readonly IPermadeathLadder AlwaysPermadeath = new AlwaysPermadeathLadder();

    [Fact]
    public void A_mode_with_no_ladder_refuses_rather_than_never_triggering()
    {
        // The whole point of T4.9. A bool had a default that meant "no permanent death ever", and it
        // was the value a mode got by not thinking about the question — indistinguishable at every
        // call site from a mode that had decided. Null now refuses instead.
        var ex = Assert.Throws<ArgumentNullException>(() => MemberSettlementRules.Decide(
            downedOnce: true, ladder: null!, downedRecoveryDelves: RecoveryDelves,
            afflicted: false, extracted: true, bossKilled: true, routeAtLeastHalfCleared: false));

        Assert.Contains("NeverPermadeath", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mode_with_genuinely_no_permadeath_says_so_and_still_recovers()
    {
        // The same outcome the old `false` produced, but now a decision a reader can see. The member
        // still recovers rather than retiring — the behaviour did not change, only its provenance.
        var result = MemberSettlementRules.Decide(
            downedOnce: true, ladder: NeverPermadeath.Instance, downedRecoveryDelves: RecoveryDelves,
            afflicted: false, extracted: true, bossKilled: true, routeAtLeastHalfCleared: false);

        Assert.Equal(SettlementOutcome.Recover, result.Outcome);
        Assert.Equal(RecoveryDelves, result.RecoverDelves);
    }

    [Fact]
    public void The_ladder_is_asked_only_when_the_member_was_actually_downed()
    {
        // A member who never went down is Roster regardless of the ladder, so the ladder must not be
        // consulted at all — proven by a ladder that throws if asked. Cheap to get wrong, and getting
        // it wrong would make a mode's ladder run on every settled member.
        var result = MemberSettlementRules.Decide(
            downedOnce: false, ladder: new ThrowingLadder(), downedRecoveryDelves: RecoveryDelves,
            afflicted: false, extracted: true, bossKilled: true, routeAtLeastHalfCleared: false);

        Assert.Equal(SettlementOutcome.Roster, result.Outcome);
    }

    sealed class ThrowingLadder : IPermadeathLadder
    {
        public bool PermadeathApplies() =>
            throw new InvalidOperationException("the ladder must not be consulted for a member who never went down");
    }
}
