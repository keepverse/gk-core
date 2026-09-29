using FusionRpg.Core.Balance.Analytic;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>class-system-todo.md P4.6 — <see cref="StatusUptime.Expected"/> (the deterministic status
/// read via the shipped <c>ResistanceEvaluator</c>) and its two compositions,
/// <see cref="StatusUptime.ExpectedDotPerRound"/>/<see cref="StatusUptime.CcDisabledShare"/> — ported
/// from <c>gk-core/tools/CombatSim/StatusModel.cs</c>'s <c>StatusMath</c>, read in full this session.</summary>
public class StatusUptimeExpectedTests
{
    const string Wither = "wither"; // the same DoT status id tools/CombatSim/StatusModel.cs defaults to.

    static ActorDerivedSnapshot NeutralCombat => ActorDerivedSnapshot.StubNeutral();
    static CombatActorSnapshot Snap(ActorDerivedSnapshot s) => new(s, ActorElementTypes.Neutral);

    // ---- Expected ---------------------------------------------------------------------------------

    [Fact]
    public void Expected_nullOrEmptyStatusId_throws()
    {
        var s = Snap(NeutralCombat);
        Assert.Throws<ArgumentException>(() => StatusUptime.Expected("", 0.25, 3, 1.0, s, s, 100));
        Assert.Throws<ArgumentException>(() => StatusUptime.Expected(null!, 0.25, 3, 1.0, s, s, 100));
    }

    [Fact]
    public void Expected_nullSnapshots_reject()
    {
        var s = Snap(NeutralCombat);
        Assert.Throws<ArgumentNullException>(() => StatusUptime.Expected(Wither, 0.25, 3, 1.0, null!, s, 100));
        Assert.Throws<ArgumentNullException>(() => StatusUptime.Expected(Wither, 0.25, 3, 1.0, s, null!, 100));
    }

    [Fact]
    public void Expected_negativeOrNanBaseDamage_throws()
    {
        var s = Snap(NeutralCombat);
        Assert.Throws<ArgumentOutOfRangeException>(() => StatusUptime.Expected(Wither, 0.25, 3, 1.0, s, s, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StatusUptime.Expected(Wither, 0.25, 3, 1.0, s, s, double.NaN));
    }

    [Fact]
    public void Expected_zeroGrantChance_neverApplies()
    {
        var s = Snap(NeutralCombat);
        var r = StatusUptime.Expected(Wither, 0.25, 3, grantChance: 0.0, s, s, 100);
        Assert.Equal(0.0, r.PFinal);
        Assert.Equal(0.0, r.Magnitude);
        Assert.Equal(0.0, r.DurationRounds);
    }

    [Fact]
    public void Expected_realisticGrantChance_producesAWellFormedOutcome()
    {
        // Not a hand-derived number (ResistanceEvaluator's own delta/netFactor/sigmoid chain belongs to
        // that file, not re-derived here) -- just the structural contract Predictor depends on: a
        // bounded probability, and a non-negative magnitude/duration whenever it applied at all.
        var s = Snap(NeutralCombat);
        var r = StatusUptime.Expected(Wither, 0.25, 3, grantChance: 1.0, s, s, 100);
        Assert.InRange(r.PFinal, 0.0, 1.0);
        Assert.True(r.Magnitude >= 0.0);
        Assert.True(r.DurationRounds >= 0.0);
    }

    [Fact]
    public void Expected_isPure_sameInputsSameOutputs()
    {
        var s = Snap(NeutralCombat);
        var r1 = StatusUptime.Expected(Wither, 0.25, 3, 1.0, s, s, 100);
        var r2 = StatusUptime.Expected(Wither, 0.25, 3, 1.0, s, s, 100);
        Assert.Equal(r1, r2);
    }

    // ---- ExpectedDotPerRound / CcDisabledShare -----------------------------------------------------

    [Fact]
    public void ExpectedDotPerRound_handComputedComposition()
    {
        // Bypasses Expected() entirely -- constructs the StatusOutcome directly so this test checks
        // ONLY the composition wiring (Uptime(PFinal*pHit, dur) * Magnitude), reusing StatusUptimeTests'
        // own already-verified Uptime formula rather than re-deriving it: PFinal=0.4, pHit=0.5 ->
        // Uptime(0.2, 3) = 1-0.8^3 = 1-0.512 = 0.488; * magnitude 50 = 24.4.
        var outcome = new StatusUptime.StatusOutcome(PFinal: 0.4, Magnitude: 50, DurationRounds: 3);
        Assert.Equal(24.4, StatusUptime.ExpectedDotPerRound(outcome, pHit: 0.5), 9);
    }

    [Fact]
    public void ExpectedDotPerRound_zeroPFinal_isZero()
    {
        var outcome = new StatusUptime.StatusOutcome(0.0, 100, 5);
        Assert.Equal(0.0, StatusUptime.ExpectedDotPerRound(outcome, pHit: 1.0));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void ExpectedDotPerRound_pHitOutOfRange_throws(double pHit)
    {
        var outcome = new StatusUptime.StatusOutcome(0.4, 50, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => StatusUptime.ExpectedDotPerRound(outcome, pHit));
    }

    [Fact]
    public void CcDisabledShare_handComputedComposition_doesNotFoldInPHit()
    {
        // Uptime(0.4, 3) = 1-0.6^3 = 1-0.216 = 0.784 -- no pHit factor inside this one (the caller
        // applies its own pHit separately, unlike ExpectedDotPerRound; see StatusUptime.cs's own doc).
        var outcome = new StatusUptime.StatusOutcome(PFinal: 0.4, Magnitude: 999, DurationRounds: 3);
        Assert.Equal(0.784, StatusUptime.CcDisabledShare(outcome), 9);
    }
}
