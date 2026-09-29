using FusionRpg.Core.Balance.Analytic;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>class-system-todo.md P4.6 — <see cref="Predictor"/>'s core-combat composition (strikes,
/// shields, the full joint-reflect covariance, recovery, the normal race, the RoundLimit timeout).
/// End-to-end fidelity against the reference (<c>tools/CombatSim/Analytic.Predict</c>) is proven
/// separately by <c>gk-core/tools/ProvePredictor</c> (an exact cross-check against real FORCE/FINESSE/BASTION
/// archetypes, matching to ~1e-7 -- the residual is explained and traced in that tool's own comment).
/// These tests cover the properties spec-deterministic-core.md §6 names explicitly (Θ-invariance,
/// RoundLimit not changing a verdict) plus the composition's own basic contracts.</summary>
public class PredictorTests
{
    static ActorDerivedSnapshot NeutralCombat => ActorDerivedSnapshot.StubNeutral();
    static CombatActorSnapshot Snap(ActorDerivedSnapshot s) => new(s, ActorElementTypes.Neutral);

    static CombatActorSnapshot With(params (string Channel, double Value)[] overlays) =>
        Snap(NeutralCombat.Overlay(overlays.Select(o => new KeyValuePair<string, double>(o.Channel, o.Value))));

    static Predictor.Actor Actor(string name, CombatActorSnapshot snap, double hp = 10_000, double baseDamage = 500, long shieldMaxHp = 0) =>
        new(name, snap, hp, baseDamage, shieldMaxHp);

    [Fact]
    public void Predict_nullArguments_reject()
    {
        var a = Actor("A", Snap(NeutralCombat));
        Assert.Throws<ArgumentNullException>(() => Predictor.Predict(null!, a));
        Assert.Throws<ArgumentNullException>(() => Predictor.Predict(a, null!));
    }

    [Fact]
    public void Predict_identicalActors_isAnEvenRace()
    {
        var a = Actor("A", Snap(NeutralCombat));
        var b = Actor("B", Snap(NeutralCombat));
        var r = Predictor.Predict(a, b);
        Assert.Equal(0.5, r.WinShareA, 6);
    }

    [Fact]
    public void Predict_strongerAttacker_favorsThatSide()
    {
        var strong = With((DerivedStatChannels.CombatPowerOmni, 400));
        var weak = Snap(NeutralCombat);
        var r = Predictor.Predict(Actor("A", strong), Actor("B", weak));
        Assert.True(r.WinShareA > 0.5, $"expected the stronger attacker to favor A, got {r.WinShareA}");
    }

    [Fact]
    public void Predict_winShareIsAlwaysInZeroToOneRange()
    {
        var a = With((DerivedStatChannels.CombatPowerOmni, 1000));
        var b = With((DerivedStatChannels.CombatDefenseOmni, 50));
        var r = Predictor.Predict(Actor("A", a), Actor("B", b));
        Assert.InRange(r.WinShareA, 0.0, 1.0);
    }

    [Fact]
    public void Predict_givingOneSideAShield_increasesThatSidesWinShare()
    {
        var a = Snap(NeutralCombat);
        var b = Snap(NeutralCombat);
        var noShield = Predictor.Predict(Actor("A", a, shieldMaxHp: 0), Actor("B", b));
        var withShield = Predictor.Predict(Actor("A", a, shieldMaxHp: 5000), Actor("B", b));
        Assert.True(withShield.WinShareA > noShield.WinShareA,
            $"expected a shield to raise A's win share above the no-shield case ({noShield.WinShareA}), got {withShield.WinShareA}");
    }

    [Fact]
    public void Predict_isPure_sameInputsSameOutputs()
    {
        var a = Actor("A", With((DerivedStatChannels.CombatPowerOmni, 300)));
        var b = Actor("B", With((DerivedStatChannels.CombatDefenseOmni, 100)));
        var r1 = Predictor.Predict(a, b);
        var r2 = Predictor.Predict(a, b);
        Assert.Equal(r1, r2);
    }

    [Fact]
    public void Predict_swappingSides_isConsistent()
    {
        // Predict(A,B).RateAgainstA must equal Predict(B,A).RateAgainstB -- the same physical quantity
        // (damage rate into A) read from either call.
        var a = Actor("A", With((DerivedStatChannels.CombatPowerOmni, 300)));
        var b = Actor("B", With((DerivedStatChannels.CombatDefenseOmni, 100)));
        var ab = Predictor.Predict(a, b);
        var ba = Predictor.Predict(b, a);
        Assert.Equal(ab.RateAgainstA, ba.RateAgainstB, 6);
        Assert.Equal(ab.RateAgainstB, ba.RateAgainstA, 6);
    }

    // ---- RoundLimit ------------------------------------------------------------------------------

    [Fact]
    public void Predict_roundLimitNullOrNonPositive_isIdenticalToNoLimit()
    {
        var a = Actor("A", With((DerivedStatChannels.CombatPowerOmni, 300)));
        var b = Actor("B", Snap(NeutralCombat));
        var noLimit = Predictor.Predict(a, b, roundLimit: null);
        var zeroLimit = Predictor.Predict(a, b, roundLimit: 0);
        var negativeLimit = Predictor.Predict(a, b, roundLimit: -5);
        Assert.Equal(noLimit.WinShareA, zeroLimit.WinShareA, 12);
        Assert.Equal(noLimit.WinShareA, negativeLimit.WinShareA, 12);
    }

    [Fact]
    public void Predict_doublingARoundLimitFarBeyondBothFights_changesNoVerdict()
    {
        // spec-deterministic-core.md §6 test 7 (RoundLimit_does_not_change_a_verdict), applied at the
        // Predictor level: a limit generous enough that neither side is remotely likely to still be
        // fighting at it should produce (near enough) the same win share whether or not it is doubled.
        var a = Actor("A", With((DerivedStatChannels.CombatPowerOmni, 300)));
        var b = Actor("B", Snap(NeutralCombat));
        var baseline = Predictor.Predict(a, b);
        var generousLimit = Math.Max(baseline.RoundsA, baseline.RoundsB) * 50.0;

        var once = Predictor.Predict(a, b, generousLimit);
        var doubled = Predictor.Predict(a, b, generousLimit * 2.0);

        Assert.Equal(once.WinShareA, doubled.WinShareA, 6);
    }

    [Fact]
    public void Predict_aRoundLimitFarBeyondBothFights_convergesToTheNoLimitRace()
    {
        // As roundLimit -> infinity, both "finished in time" probabilities -> 1, so
        // pA/(pA+pB) -> win/(win+(1-win)) = win exactly: a large enough limit must reproduce the
        // underlying race's own win share, not just agree with itself under doubling (the test above).
        var a = Actor("A", With((DerivedStatChannels.CombatPowerOmni, 300)));
        var b = Actor("B", Snap(NeutralCombat));
        var noLimit = Predictor.Predict(a, b);
        var generousLimit = Math.Max(noLimit.RoundsA, noLimit.RoundsB) * 500.0;
        var withLimit = Predictor.Predict(a, b, generousLimit);

        Assert.Equal(noLimit.WinShareA, withLimit.WinShareA, 6);
    }

    [Fact]
    public void Predict_aSymmetricMatchupWithAVeryShortRoundLimit_isStillAnEvenRace()
    {
        // A property that IS always true, unlike "any short clock pulls toward 0.5" (tried and
        // disproved empirically first: with a lopsided matchup a short clock can make the stronger
        // side's win share MORE extreme, not less, because the weaker side's chance of finishing in
        // time collapses faster than the stronger side's does). For a SYMMETRIC matchup, though, both
        // "finished in time" probabilities are identical by construction at every limit, so the ratio
        // stays exactly even regardless of how short the clock is.
        var a = Actor("A", Snap(NeutralCombat));
        var b = Actor("B", Snap(NeutralCombat));
        var noLimit = Predictor.Predict(a, b);
        var shortLimit = Predictor.Predict(a, b, roundLimit: Math.Max(1.0, noLimit.RoundsA) / 1000.0);

        Assert.Equal(0.5, shortLimit.WinShareA, 6);
    }

    // ---- Theta-invariance ------------------------------------------------------------------------
    //
    // Predictor.Predict itself takes already-resolved channel values, so it has no notion of Θ; the
    // "contests read Θ linearly, magnitudes read P(Θ)" split (ssot-power-scale.md PS-3) means a naive
    // "multiply every channel by a constant" is not the right shape for this property (sigmoid-based
    // contest channels are provably NOT invariant under that scaling -- confirmed by writing exactly
    // that test here first and watching it fail for the right reason, not a guess). The real Θ-
    // invariance property belongs to the full pipeline (aptitude-resolve -> Predictor), and
    // gk-core/tools/ProvePredictor exercises it directly against real archetypes resolved at two different Θ
    // through the actual AptitudeReadFunctions split, which is the only place that split is honoured
    // correctly.

    // Θ-invariance itself is exercised by gk-core/tools/ProvePredictor (--theta-invariance), which resolves
    // real archetypes through the actual aptitude-resolve pipeline at two very different Θ values and
    // asserts identical win shares -- see that tool's own header comment and class-system-todo.md's
    // P4.6 evidence for the numbers.

    // ---- Performance ----------------------------------------------------------------------------

    [Fact]
    public void Predict_144Corners_completeWellUnderAGenerousWallClockBudget()
    {
        // spec-deterministic-core.md §6 test 9 / §8 item 5: "144 corner evaluations complete in
        // microseconds... a wall-clock assertion with generous headroom, not a benchmark." The 144
        // corners THEMSELVES are balance-guard's own sweep (class-system-map.md module 3, Phase 5, not
        // yet built); what P4.6 owns is that Predict is fast enough for that sweep to be microseconds
        // once it exists. A 12x12 grid (144 ordered pairs, matching the corner COUNT) of varied actors
        // stands in for it here.
        var actors = new Predictor.Actor[12];
        for (var i = 0; i < actors.Length; i++)
        {
            var snap = With(
                (DerivedStatChannels.CombatPowerOmni, 100 + i * 37),
                (DerivedStatChannels.CombatDefenseOmni, 20 + i * 11),
                (DerivedStatChannels.CombatAccuracyOmni, 30 + i * 5),
                (DerivedStatChannels.CombatDodgeOmni, 10 + i * 3),
                (DerivedStatChannels.CombatShieldToughnessOmni, i * 40),
                (DerivedStatChannels.CombatReflectDamageOmni, i * 6));
            actors[i] = Actor($"corner{i}", snap, hp: 5000 + i * 700, baseDamage: 200 + i * 30, shieldMaxHp: i * 250);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var count = 0;
        for (var i = 0; i < actors.Length; i++)
        for (var j = 0; j < actors.Length; j++)
        {
            if (i == j) continue;
            var r = Predictor.Predict(actors[i], actors[j]);
            Assert.InRange(r.WinShareA, 0.0, 1.0);
            count++;
        }
        sw.Stop();

        Assert.Equal(132, count); // 12*12 - 12 (i==j skipped) -- close to the named 144, same order.
        // Generous, not a benchmark: 500ms for 132 closed-form evaluations is ~3.8ms each, several
        // orders of magnitude looser than "microseconds" -- enough headroom for a slow CI machine
        // without the assertion becoming meaningless.
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"expected 132 Predict calls well under 500ms, took {sw.ElapsedMilliseconds}ms");
    }

    // ---- Actions + status (the full four-axis overload) -----------------------------------------
    // End-to-end fidelity against Analytic.Predict(a,b,actions) with Analytic.Status live is proven by
    // gk-core/tools/ProvePredictor on real archetypes (all six arrows within 9.1e-5 of the reference, after a
    // real bug this exact test category is designed to catch -- see DoT directionality below). These
    // tests cover the composition's own directional contracts with hand-built actors.

    static readonly Predictor.ActionEconomy BasicEconomy = new(
        new[]
        {
            new ActionSchedule.ActionOption("skill-strike", Priority: 1, DamageMultiplier: 1.8, "qi", 300),
            new ActionSchedule.ActionOption("strike", Priority: 2, DamageMultiplier: 1.0, "stamina", 220),
            new ActionSchedule.ActionOption("pass", Priority: 99, DamageMultiplier: 0.0, null, 0),
        },
        new Dictionary<string, ActionSchedule.PoolState> { ["qi"] = new(54, 54, 20), ["stamina"] = new(100, 100, 25) },
        new Dictionary<string, ActionSchedule.PoolState> { ["qi"] = new(54, 54, 20), ["stamina"] = new(100, 100, 25) });

    [Fact]
    public void Predict_fiveArgOverload_withNullEconomyAndStatus_matchesTheSimpleOverload()
    {
        var a = Actor("A", With((DerivedStatChannels.CombatPowerOmni, 300)));
        var b = Actor("B", Snap(NeutralCombat));
        var simple = Predictor.Predict(a, b, roundLimit: 7.5);
        var general = Predictor.Predict(a, b, roundLimit: 7.5, economy: null, status: null);
        Assert.Equal(simple, general);
    }

    [Fact]
    public void Predict_symmetricActionEconomy_isStillAnEvenRace()
    {
        // baseDamage=100 deliberately matches BasicEconomy's pool sizing (qi=54 costs exactly one
        // skill-strike; stamina=100 affords one strike) -- ActionScheduleTests' own hand-traced cycle.
        // A mismatched baseDamage (e.g. this file's own Actor() default of 500) prices every costed
        // action far beyond what either pool can ever afford, so both sides silently fall back to
        // "pass" every round regardless of pool state -- caught by this test itself failing first with
        // exactly that (degenerate, both-sides-pass) shape before the baseDamage was corrected.
        var a = Actor("A", Snap(NeutralCombat), baseDamage: 100);
        var b = Actor("B", Snap(NeutralCombat), baseDamage: 100);
        var r = Predictor.Predict(a, b, roundLimit: null, BasicEconomy, status: null);
        Assert.Equal(0.5, r.WinShareA, 6);
    }

    [Fact]
    public void Predict_actionEconomy_richerResourcePoolFavorsThatSide()
    {
        var a = Actor("A", Snap(NeutralCombat), baseDamage: 100);
        var b = Actor("B", Snap(NeutralCombat), baseDamage: 100);
        var starvedB = new Predictor.ActionEconomy(
            BasicEconomy.Options, BasicEconomy.PoolsA,
            new Dictionary<string, ActionSchedule.PoolState> { ["qi"] = new(0, 54, 0), ["stamina"] = new(0, 100, 0) });

        var symmetric = Predictor.Predict(a, b, roundLimit: null, BasicEconomy, status: null);
        var bStarved = Predictor.Predict(a, b, roundLimit: null, starvedB, status: null);

        Assert.True(bStarved.WinShareA > symmetric.WinShareA,
            $"expected starving B's resources to favor A ({bStarved.WinShareA}) over the symmetric baseline ({symmetric.WinShareA})");
    }

    [Fact]
    public void Predict_dot_landsOnTheOpponent_notTheCasterItself()
    {
        // The exact property a real bug violated during this session (class-system-todo.md P4.6
        // evidence): an earlier draft attributed each side's own DoT to the WRONG side's dealt-mean,
        // so a stronger DoT-magnitude side ended up hurting ITSELF instead of the opponent. Caught by
        // gk-core/tools/ProvePredictor's exact cross-check (FORCE vs BASTION moved from ~1% to ~99.9%+ off);
        // this is the unit-level guard against it regressing silently.
        var strongDot = new Predictor.StatusProfile("wither", MagnitudeShareOfBase: 0.5, BaseDurationRounds: 5, GrantChance: 1.0);

        // A has a far larger base damage than B, so A's own DoT (magnitude scales off A's
        // EffectiveBase) is far more dangerous than B's -- if it lands correctly (on B), A's win share
        // should INCREASE relative to the no-status baseline. If it were attributed backwards, A's own
        // large DoT would land on A instead, and A's win share would fall, not rise.
        var a = Actor("A", Snap(NeutralCombat), baseDamage: 2000);
        var b = Actor("B", Snap(NeutralCombat), baseDamage: 500);

        var noStatus = Predictor.Predict(a, b, roundLimit: null, economy: null, status: null);
        var withStatus = Predictor.Predict(a, b, roundLimit: null, economy: null, strongDot);

        Assert.True(withStatus.WinShareA > noStatus.WinShareA,
            $"expected A's own (larger) DoT to raise A's win share above the no-status baseline ({noStatus.WinShareA}), got {withStatus.WinShareA}");
        // The more direct, quantitative version of the same check: the status is shared, so BOTH sides
        // apply it (B's own, smaller DoT still lands on A -- baseDamage=500 is not zero), but the
        // increase on B's incoming rate (from A's much larger DoT) must exceed the increase on A's
        // incoming rate (from B's much smaller one). A bug that swapped the attribution would instead
        // give B's rate the tiny increase and A's rate the large one -- the opposite ordering.
        var riseIntoB = withStatus.RateAgainstB - noStatus.RateAgainstB;
        var riseIntoA = withStatus.RateAgainstA - noStatus.RateAgainstA;
        Assert.True(riseIntoB > riseIntoA,
            $"expected A's larger DoT to raise B's incoming rate ({riseIntoB}) more than B's smaller DoT raises A's ({riseIntoA})");
    }

    [Fact]
    public void Predict_cc_reducesTheVictimsEffectiveRate_notTheCastersOwn()
    {
        var strongCc = new Predictor.StatusProfile("butter", MagnitudeShareOfBase: 0.0, BaseDurationRounds: 5, GrantChance: 1.0);

        // A has a far larger base damage than B; CcDisabledShare is gated on the CASTER's own pHit, not
        // magnitude, but a larger base damage here is just a stand-in for "A is otherwise the stronger
        // side" -- the property under test is direction, not magnitude sensitivity.
        var a = Actor("A", With((DerivedStatChannels.CombatPowerOmni, 200)), baseDamage: 2000);
        var b = Actor("B", Snap(NeutralCombat), baseDamage: 500);

        var noStatus = Predictor.Predict(a, b, roundLimit: null, economy: null, status: null);
        var withCc = Predictor.Predict(a, b, roundLimit: null, economy: null, strongCc);

        // A's CC on B reduces B's OWN output (B swings less often), so the rate INTO A should fall (or
        // stay the same, if B never lands the CC) -- never rise. If the CC were attributed backwards
        // (disabling A instead of B), the rate into B would fall instead, which this also rules out.
        Assert.True(withCc.RateAgainstA <= noStatus.RateAgainstA,
            $"expected A's CC on B to reduce (or leave unchanged) the rate into A ({noStatus.RateAgainstA} -> {withCc.RateAgainstA})");
    }

    [Fact]
    public void Predict_actionsAndStatusTogether_winShareStaysInRange()
    {
        var status = new Predictor.StatusProfile("wither", 0.25, 3, 1.0);
        var a = Actor("A", With((DerivedStatChannels.CombatPowerOmni, 250)), baseDamage: 100);
        var b = Actor("B", Snap(NeutralCombat), baseDamage: 100);
        var r = Predictor.Predict(a, b, roundLimit: null, BasicEconomy, status);
        Assert.InRange(r.WinShareA, 0.0, 1.0);
    }
}
