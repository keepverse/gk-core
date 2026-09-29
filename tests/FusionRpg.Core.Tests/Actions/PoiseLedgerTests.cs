using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Actions.Defence;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T25/T26 (action-todo.md, spec-defence-actions.md §3): `poise`'s three-part cost, exercised
/// directly against T15's real `ActorResourcePools`, not a mock. The mutual-guard TERMINATION proof
/// (the module's hardest acceptance line) lives in <see cref="PoiseTerminationTests"/>.
///
/// <para><b>battle-tempo `poise-unification` (2026-09-05):</b> this file now also carries every
/// property <c>Combat/Guard/PoiseRuntimeTests.cs</c> proved against the deleted private-pool
/// `PoiseRuntime` — migrated, not dropped (spec-poise-unification.md §6.1). Each migrated test names
/// the PoiseRuntimeTests fact it replaces. Properties already covered by this file or by
/// <see cref="PoiseTerminationTests"/>/<c>DefenceActionRiposteTests</c> before the migration are noted
/// rather than duplicated: unconditional flat commit, riposte scaling/negative-input, and
/// exhaustion-not-death were already proven against the shipped `PoiseLedger`/`Riposte` pair.</para>
/// </summary>
public class PoiseLedgerTests
{
    static ActorDerivedSnapshot PoiseSnapshot(double max, double regenPerTick)
    {
        var registry = DerivedStatRegistry.CreateDefault();
        var composer = new DerivedComposer(registry);
        return composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.ResourceMax("poise"), DerivedModifierOp.Flat, max, SourceId: "test"),
            new DerivedModifier(DerivedStatChannels.ResourceRegen("poise"), DerivedModifierOp.Flat, regenPerTick, SourceId: "test"),
        });
    }

    [Fact]
    public void TryCommitSpendsTheFlatAmountOnce()
    {
        var derived = PoiseSnapshot(max: 100, regenPerTick: 0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        var ok = PoiseLedger.TryCommit(pools, flatCommitAmount: 30, nowTick: 0, derived);

        Assert.True(ok);
        Assert.Equal(70, pools.Resolve(PoiseLedger.ResourceId, 0, derived));
    }

    /// <summary>Migrates PoiseRuntimeTests.Commit_repeatedRegardlessOfOutcome_keepsCostingTheFlatAmount
    /// — the "even when nothing lands" claim only means something if it holds across repeated commits,
    /// not just once.</summary>
    [Fact]
    public void RepeatedCommitsEachCostTheFlatAmountRegardlessOfOutcome()
    {
        var derived = PoiseSnapshot(max: 1000, regenPerTick: 0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        Assert.True(PoiseLedger.TryCommit(pools, flatCommitAmount: 50, nowTick: 0, derived));
        Assert.True(PoiseLedger.TryCommit(pools, flatCommitAmount: 50, nowTick: 0, derived));
        Assert.True(PoiseLedger.TryCommit(pools, flatCommitAmount: 50, nowTick: 0, derived));

        Assert.Equal(850, pools.Resolve(PoiseLedger.ResourceId, 0, derived));
    }

    [Fact]
    public void RaisingWithInsufficientPoiseIsRefusedByAffordabilityNotSilence()
    {
        var derived = PoiseSnapshot(max: 10, regenPerTick: 0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        var ok = PoiseLedger.TryCommit(pools, flatCommitAmount: 50, nowTick: 0, derived);

        Assert.False(ok);
        Assert.Equal(10, pools.Resolve(PoiseLedger.ResourceId, 0, derived)); // untouched, not partially spent
    }

    /// <summary>Migrates PoiseRuntimeTests.Commit_negativeCost_throws.</summary>
    [Fact]
    public void CommitWithNegativeCostThrows()
    {
        var derived = PoiseSnapshot(max: 100, regenPerTick: 0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => PoiseLedger.TryCommit(pools, flatCommitAmount: -1, nowTick: 0, derived));
    }

    [Fact]
    public void AbsorbDrainScalesWithWhatWasAbsorbedNotAFlatAmount()
    {
        Assert.Equal(50, PoiseLedger.AbsorbDrainAmount(absorbedAmount: 500, drainRatioMilli: 100)); // 10%
        Assert.Equal(500, PoiseLedger.AbsorbDrainAmount(absorbedAmount: 500, drainRatioMilli: 1000)); // 100%
        Assert.Equal(0, PoiseLedger.AbsorbDrainAmount(absorbedAmount: 0, drainRatioMilli: 1000));
    }

    [Fact]
    public void PayAbsorbDrainSpendsTheDrainedAmountWhenAffordable()
    {
        var derived = PoiseSnapshot(max: 100, regenPerTick: 0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        var drained = PoiseLedger.PayAbsorbDrain(pools, absorbedAmount: 400, drainRatioMilli: 250, nowTick: 0, derived); // 25% of 400 = 100

        Assert.Equal(100, drained);
        Assert.Equal(0, pools.Resolve(PoiseLedger.ResourceId, 0, derived));
    }

    /// <summary>Migrates PoiseRuntimeTests.Absorb_neverDrainsMoreThanThePoolHolds. This is the property
    /// the pre-unification `TryPayAbsorbDrain` (all-or-nothing via `TrySpend`) got WRONG relative to
    /// the deleted `PoiseRuntime.Absorb` and to `ShieldRuntime.Absorb`'s own cited precedent: with an
    /// insufficient pool, all-or-nothing would drain NOTHING for damage already stopped. Graceful
    /// draining — up to what remains, never a refusal — is the corrected contract this test pins.</summary>
    [Fact]
    public void PayAbsorbDrainNeverDrainsMoreThanThePoolHoldsAndNeverRefuses()
    {
        var derived = PoiseSnapshot(max: 100, regenPerTick: 0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        // ideal = 10,000 * 300 / 1000 = 3,000, far more than the 100-poise pool holds.
        var drained = PoiseLedger.PayAbsorbDrain(pools, absorbedAmount: 10_000, drainRatioMilli: 300, nowTick: 0, derived);

        Assert.Equal(100, drained); // capped at what was actually there, not refused to zero
        Assert.Equal(0, pools.Resolve(PoiseLedger.ResourceId, 0, derived));
        Assert.True(PoiseLedger.IsExhausted(pools, 0, derived));
    }

    /// <summary>Migrates PoiseRuntimeTests.Heavy_hits_break_the_guard_and_attrition_does_not, using the
    /// shipped `PoiseLedger`/`ActorResourcePools` pair (the original used the deleted private-pool
    /// `PoiseRuntime.Regen`, which `ActorResourcePools.Resolve`'s lazy accrual replaces).</summary>
    [Fact]
    public void LightAttritionDoesNotBreakTheGuardButASingleHeavyStopDoes()
    {
        const long maxPoise = 1000;
        const long regenPerTick = 80;
        const int absorbShareMilli = 300; // 30%

        // Attrition: one light absorb (drains 30), then one tick's regen (+80) -- net positive, must
        // not exhaust, and must not overflow past max either.
        var attritionDerived = PoiseSnapshot(maxPoise, regenPerTick);
        var attritionPools = ActorResourcePools.CreateFull(attritionDerived, atTick: 0);
        PoiseLedger.PayAbsorbDrain(attritionPools, absorbedAmount: 100, absorbShareMilli, nowTick: 0, attritionDerived); // drains 30
        Assert.False(PoiseLedger.IsExhausted(attritionPools, nowTick: 1, attritionDerived), "light, regen-outpaced pressure must not break the guard");
        Assert.Equal(maxPoise, attritionPools.Resolve(PoiseLedger.ResourceId, nowTick: 1, attritionDerived));

        // Heavy: a single stop whose ideal drain (3,000) far exceeds the full pool -- must break it
        // outright, even starting from max.
        var heavyDerived = PoiseSnapshot(maxPoise, regenPerTick: 0);
        var heavyPools = ActorResourcePools.CreateFull(heavyDerived, atTick: 0);
        PoiseLedger.PayAbsorbDrain(heavyPools, absorbedAmount: 10_000, absorbShareMilli, nowTick: 0, heavyDerived);
        Assert.True(PoiseLedger.IsExhausted(heavyPools, nowTick: 0, heavyDerived), "a single heavy stop must break the guard outright");
    }

    /// <summary>Migrates PoiseRuntimeTests.Poise_regen_never_exceeds_peer_pressure_..., the absorb-driven
    /// half of the `r < 1` property (the hold-tick half is <see cref="PoiseTerminationTests"/>'s
    /// existing, stronger proof — this is the one built on `PayAbsorbDrain` specifically, since that is
    /// a different code path with its own graceful-drain contract to prove under sustained pressure).</summary>
    [Fact]
    public void SustainedAbsorbPressureAboveRegenEventuallyExhaustsThePool()
    {
        const long maxPoise = 1000;
        const long regenPerTick = 80;
        const long drainPerRound = 120; // r = 80/120 = 0.667 < 1 -- deliberately chosen low.

        var derived = PoiseSnapshot(maxPoise, regenPerTick);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        var brokeWithinBudget = false;
        var roundsToBreak = 0;
        long tick = 0;
        for (var round = 0; round < 100 && !brokeWithinBudget; round++)
        {
            PoiseLedger.PayAbsorbDrain(pools, absorbedAmount: drainPerRound, drainRatioMilli: 1000, tick, derived); // 100% of drainPerRound
            roundsToBreak++;
            if (PoiseLedger.IsExhausted(pools, tick, derived)) { brokeWithinBudget = true; break; }
            tick++; // one tick's regen accrues lazily on the NEXT Resolve/TrySpend at the advanced tick
        }

        Assert.True(brokeWithinBudget, "sustained drain > regen (r < 1) must break the guard within 100 rounds");
        Assert.True(roundsToBreak > 1, "a single round should not be enough -- this proves SUSTAINED pressure, not one heavy hit");
    }

    /// <summary>spec-poise-unification.md §6.3 — "one pool, proven": a spend through `PoiseLedger`
    /// must be visible to BOTH `ActorResourcePools.Resolve` AND `SettleAll`. Under the pre-unification
    /// fork this was false by construction — `PoiseRuntime`'s private dictionary was invisible to
    /// `SettleAll`'s persistence snapshot entirely.</summary>
    [Fact]
    public void ASpendThroughPoiseLedgerIsVisibleToBothResolveAndSettleAll()
    {
        var derived = PoiseSnapshot(max: 100, regenPerTick: 0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        PoiseLedger.TryCommit(pools, flatCommitAmount: 40, nowTick: 0, derived);

        Assert.Equal(60, pools.Resolve(PoiseLedger.ResourceId, 0, derived));

        var settled = pools.SettleAll(nowTick: 0, derived);
        Assert.True(settled.ContainsKey(PoiseLedger.ResourceId));
        Assert.Equal(60, settled[PoiseLedger.ResourceId]);
    }

    [Fact]
    public void HoldTickIsJustAnOrdinaryPerTickSpend()
    {
        var derived = PoiseSnapshot(max: 100, regenPerTick: 0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        Assert.True(PoiseLedger.TryPayHoldTick(pools, perTickAmount: 10, nowTick: 0, derived));
        Assert.True(PoiseLedger.TryPayHoldTick(pools, perTickAmount: 10, nowTick: 1, derived));
        Assert.Equal(80, pools.Resolve(PoiseLedger.ResourceId, 1, derived));
    }

    [Fact]
    public void NegativeAbsorbedAmountOrRatioIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PoiseLedger.AbsorbDrainAmount(-1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => PoiseLedger.AbsorbDrainAmount(100, -1));
    }

    /// <summary>Migrates PoiseRuntimeTests.Poise_at_zero_applies_exhaustion_not_death's behavioural
    /// half via the shipped helper (the reflection half of that test — "PoiseRuntime declares nothing
    /// HP-shaped" — no longer applies: there is no PoiseRuntime type left to reflect over).</summary>
    [Fact]
    public void IsExhaustedTracksTheResolvedPoolNotAStoredFlag()
    {
        var derived = PoiseSnapshot(max: 50, regenPerTick: 0);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        Assert.False(PoiseLedger.IsExhausted(pools, 0, derived));

        PoiseLedger.TryCommit(pools, flatCommitAmount: 50, nowTick: 0, derived);

        Assert.Equal(0, pools.Resolve(PoiseLedger.ResourceId, 0, derived));
        Assert.True(PoiseLedger.IsExhausted(pools, 0, derived));
    }
}
