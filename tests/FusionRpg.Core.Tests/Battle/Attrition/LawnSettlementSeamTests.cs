using FusionRpg.Core.Battle.Attrition;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Attrition;

/// <summary>
/// `solid-remediation` 2026-09-17 (SR-17, CP4's *"deploying a unique actor is a cost in every mode"*).
///
/// <para>The lawn die path used to inline the delve's own <c>case Retire:</c> branch instead of asking
/// <see cref="MemberSettlementRules.Decide"/>. It now routes through the seam with
/// <see cref="AlwaysPermadeath"/>, and these tests pin the two things that made that safe to do:
/// the contract of the new ladder, and the fact that routing through the seam is byte-identical to the
/// branch it replaced.</para>
///
/// <para><b>What these deliberately do NOT assert:</b> that always-permadeath is correct. It is the
/// lawn's current, never-reviewed behaviour, and <c>LawnPermadeathLadder</c> exists to replace it. These
/// tests are written so that the replacement <b>fails them loudly</b> rather than passing quietly — which
/// is the point of pinning current behaviour you expect to change.</para>
/// </summary>
public class LawnSettlementSeamTests
{
    [Fact]
    public void AlwaysPermadeath_says_yes_and_NeverPermadeath_says_no()
    {
        // The pair is the point: each states a decision rather than defaulting to one.
        Assert.True(AlwaysPermadeath.Instance.PermadeathApplies());
        Assert.False(NeverPermadeath.Instance.PermadeathApplies());
    }

    /// <summary>The equivalence the lawn rewrite rests on: a dead specimen plus an always-permadeath
    /// ladder resolves to exactly <see cref="SettlementOutcome.Retire"/> — the same outcome the inlined
    /// branch hard-coded, so routing through the seam changed no behaviour.</summary>
    [Fact]
    public void A_dead_specimen_under_the_lawn_ladder_settles_to_Retire()
    {
        var settlement = MemberSettlementRules.Decide(
            downedOnce: true,
            ladder: AlwaysPermadeath.Instance,
            downedRecoveryDelves: 1,
            afflicted: false,
            extracted: false,
            bossKilled: false,
            routeAtLeastHalfCleared: false);

        Assert.Equal(SettlementOutcome.Retire, settlement.Outcome);
        Assert.Equal(0, settlement.RecoverDelves); // Retire never carries a recovery count
    }

    /// <summary>The falsifier for the test above: the SAME inputs with the tuned ladder's other answer
    /// settle to <see cref="SettlementOutcome.Recover"/>, not Retire. Without this, the previous test
    /// would pass even if <c>Decide</c> ignored the ladder entirely and always returned Retire.</summary>
    [Fact]
    public void The_ladder_is_what_decides_it_not_the_fact_of_dying()
    {
        var permanent = MemberSettlementRules.Decide(
            downedOnce: true, ladder: AlwaysPermadeath.Instance, downedRecoveryDelves: 3,
            afflicted: false, extracted: false, bossKilled: false, routeAtLeastHalfCleared: false);
        var survivable = MemberSettlementRules.Decide(
            downedOnce: true, ladder: NeverPermadeath.Instance, downedRecoveryDelves: 3,
            afflicted: false, extracted: false, bossKilled: false, routeAtLeastHalfCleared: false);

        Assert.Equal(SettlementOutcome.Retire, permanent.Outcome);
        Assert.Equal(SettlementOutcome.Recover, survivable.Outcome);
        Assert.NotEqual(permanent.Outcome, survivable.Outcome);
        Assert.Equal(3, survivable.RecoverDelves);
    }

    /// <summary>A specimen that never went down settles to Roster whatever the ladder says — so the
    /// lawn passing <c>downedOnce: true</c> is load-bearing, not decoration.</summary>
    [Fact]
    public void A_specimen_that_never_went_down_is_untouched_by_the_ladder()
    {
        foreach (var ladder in new IPermadeathLadder[] { AlwaysPermadeath.Instance, NeverPermadeath.Instance })
        {
            var settlement = MemberSettlementRules.Decide(
                downedOnce: false, ladder: ladder, downedRecoveryDelves: 2,
                afflicted: false, extracted: false, bossKilled: false, routeAtLeastHalfCleared: false);

            Assert.Equal(SettlementOutcome.Roster, settlement.Outcome);
            Assert.Equal(0, settlement.RecoverDelves);
        }
    }
}
