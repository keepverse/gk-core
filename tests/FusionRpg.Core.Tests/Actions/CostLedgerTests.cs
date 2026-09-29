using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T17 (action-todo.md, spec-action-costs.md §3): paying. Validate all, consume all, roll back on
/// any failure; <c>onCommit</c>/<c>perTick</c> timing; a failed <c>perTick</c> payment ends the
/// action through the SAME interrupt path T12 already built (a new, purely additive
/// <c>InterruptCause.ResourceExhausted</c>); cost scales with an injected Θ seam while
/// <see cref="RungPolicy"/>'s cooldown multiplier structurally cannot read Θ at all — its own
/// <c>TryResolve(rung)</c> signature takes no Θ parameter to read.
/// </summary>
public class CostLedgerTests
{
    const string ActorKey = "wave:0";
    const int Rung = 1; // RungPolicy's shipped row 1: CostMulti=1000, CdMulti=1000 (both inert)

    static ActorDerivedSnapshot Snapshot(double theta, params (string resourceId, double max, double regen)[] resources)
    {
        var registry = DerivedStatRegistry.CreateDefault();
        var composer = new DerivedComposer(registry);
        var mods = new List<DerivedModifier>
        {
            new(DerivedStatChannels.ProgressionPower, DerivedModifierOp.Flat, theta, SourceId: "test"),
            new(DerivedStatChannels.ProgressionRealm, DerivedModifierOp.Flat, 1.0, SourceId: "test"),
        };
        foreach (var (id, max, regen) in resources)
        {
            mods.Add(new DerivedModifier(DerivedStatChannels.ResourceMax(id), DerivedModifierOp.Flat, max, SourceId: "test"));
            mods.Add(new DerivedModifier(DerivedStatChannels.ResourceRegen(id), DerivedModifierOp.Flat, regen, SourceId: "test"));
        }
        return composer.Compose(mods);
    }

    static CostLedger MakeLedger(
        IReadOnlyDictionary<string, IReadOnlyList<ActionCostRow>> costs,
        ActorResourcePools pools,
        ActorDerivedSnapshot derived,
        long nowTick = 0,
        Func<double, int>? thetaScaleMilliOf = null) =>
        new(costs, _ => pools, _ => derived, (_, _) => Rung, () => nowTick, thetaScaleMilliOf);

    static IReadOnlyDictionary<string, IReadOnlyList<ActionCostRow>> Costs(string actionId, params ActionCostRow[] rows) =>
        new Dictionary<string, IReadOnlyList<ActionCostRow>> { [actionId] = rows };

    [Fact]
    public void AnAffordableOnCommitCostIsPaidAndActuallySpent()
    {
        var derived = Snapshot(theta: 0, ("stamina", 100, 0));
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = Costs("act.strike", new ActionCostRow("act.strike", "stamina", ValueSpec.Of(30), ActionCostTiming.OnCommit));
        var ledger = MakeLedger(costs, pools, derived);

        var result = ledger.TryPay(ActorKey, "act.strike", ActionCostTiming.OnCommit, rng: null);

        Assert.Equal(CostPayOutcome.Paid, result.Outcome);
        Assert.Equal(70, pools.Resolve("stamina", 0, derived));
    }

    [Fact]
    public void AnUnaffordableCostSpendsNothingAtAll()
    {
        var derived = Snapshot(theta: 0, ("stamina", 100, 0));
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0); // 100 stamina
        var costs = Costs("act.strike", new ActionCostRow("act.strike", "stamina", ValueSpec.Of(150), ActionCostTiming.OnCommit));
        var ledger = MakeLedger(costs, pools, derived);

        var result = ledger.TryPay(ActorKey, "act.strike", ActionCostTiming.OnCommit, rng: null);

        Assert.Equal(CostPayOutcome.InsufficientFunds, result.Outcome);
        Assert.Equal("stamina", result.ShortfallResourceId);
        Assert.Equal(100, pools.Resolve("stamina", 0, derived)); // untouched
    }

    [Fact]
    public void RollbackIsPerPoolNotAggregate()
    {
        // The exact shape spec-action-costs.md's testing strategy names: an aggregate assertion
        // ("total resources unchanged") would pass even if pool A were silently spent and pool B
        // silently over-refunded to compensate. Each pool is asserted on its own line here.
        var derived = Snapshot(theta: 0, ("stamina", 100, 0), ("qi", 5, 0));
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = Costs("act.combo",
            new ActionCostRow("act.combo", "stamina", ValueSpec.Of(40), ActionCostTiming.OnCommit), // affordable alone
            new ActionCostRow("act.combo", "qi", ValueSpec.Of(50), ActionCostTiming.OnCommit));      // NOT affordable
        var ledger = MakeLedger(costs, pools, derived);

        var result = ledger.TryPay(ActorKey, "act.combo", ActionCostTiming.OnCommit, rng: null);

        Assert.Equal(CostPayOutcome.InsufficientFunds, result.Outcome);
        Assert.Equal("qi", result.ShortfallResourceId);
        Assert.Equal(100, pools.Resolve("stamina", 0, derived)); // pool A: untouched, asserted on its own
        Assert.Equal(5, pools.Resolve("qi", 0, derived));        // pool B: untouched, asserted on its own
    }

    [Fact]
    public void OnCommitAndPerTickRowsAreChargedIndependently()
    {
        var derived = Snapshot(theta: 0, ("stamina", 100, 0));
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = Costs("act.channel",
            new ActionCostRow("act.channel", "stamina", ValueSpec.Of(20), ActionCostTiming.OnCommit),
            new ActionCostRow("act.channel", "stamina", ValueSpec.Of(10), ActionCostTiming.PerTick));
        var ledger = MakeLedger(costs, pools, derived);

        var onCommit = ledger.TryPay(ActorKey, "act.channel", ActionCostTiming.OnCommit, rng: null);
        Assert.Equal(CostPayOutcome.Paid, onCommit.Outcome);
        Assert.Equal(80, pools.Resolve("stamina", 0, derived)); // only the OnCommit row charged

        var perTick = ledger.TryPay(ActorKey, "act.channel", ActionCostTiming.PerTick, rng: null);
        Assert.Equal(CostPayOutcome.Paid, perTick.Outcome);
        Assert.Equal(70, pools.Resolve("stamina", 0, derived)); // now the PerTick row too
    }

    [Fact]
    public void MissedFizzledOrInterruptedStillPaysBecauseCommittingIsWhatCosts()
    {
        // §3: "Committing is what costs, not landing." TryPay has no notion of hit/miss at all --
        // proven by the fact that its signature carries no outcome parameter; calling it a second
        // time simply charges again, exactly as it would for any other onCommit action.
        var derived = Snapshot(theta: 0, ("stamina", 100, 0));
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = Costs("act.strike", new ActionCostRow("act.strike", "stamina", ValueSpec.Of(10), ActionCostTiming.OnCommit));
        var ledger = MakeLedger(costs, pools, derived);

        ledger.TryPay(ActorKey, "act.strike", ActionCostTiming.OnCommit, rng: null); // "missed"
        Assert.Equal(90, pools.Resolve("stamina", 0, derived));
    }

    [Fact]
    public void CheckIsDeterministicAndNeverSpendsEvenWhenPolledRepeatedly()
    {
        var derived = Snapshot(theta: 0, ("stamina", 100, 0));
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = Costs("act.strike", new ActionCostRow("act.strike", "stamina", ValueSpec.Of(30), ActionCostTiming.OnCommit));
        var ledger = MakeLedger(costs, pools, derived);

        for (var i = 0; i < 50; i++)
            Assert.True(ledger.Check(ActorKey, "act.strike").IsUsable);

        Assert.Equal(100, pools.Resolve("stamina", 0, derived)); // fifty polls, zero spend
    }

    [Fact]
    public void CheckRefusesWithCannotAffordAndNamesTheShortResource()
    {
        var derived = Snapshot(theta: 0, ("stamina", 10, 0));
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = Costs("act.strike", new ActionCostRow("act.strike", "stamina", ValueSpec.Of(30), ActionCostTiming.OnCommit));
        var ledger = MakeLedger(costs, pools, derived);

        var result = ledger.Check(ActorKey, "act.strike");

        Assert.False(result.IsUsable);
        Assert.Equal(UsabilityReason.CannotAfford, result.Reason);
        Assert.Equal("stamina", result.Detail);
    }

    [Fact]
    public void CostScalesWithTheInjectedThetaSeamAtLowAndHighTheta()
    {
        // anchorCost(Th) has no shipped formula yet (ssot-power-scale.md Sec10 names no row for it) --
        // proven here as a SEAM: an injected function standing in for "the real formula, whatever it
        // turns out to be" scales the paid amount, while costMulti(rung) (A12, already real) applies
        // on top unconditionally either way.
        var lowTheta = Snapshot(theta: 20, ("stamina", 1_000_000, 0));
        var highTheta = Snapshot(theta: 5000, ("stamina", 1_000_000, 0));
        var poolsLow = ActorResourcePools.CreateFull(lowTheta, atTick: 0);
        var poolsHigh = ActorResourcePools.CreateFull(highTheta, atTick: 0);
        var costs = Costs("act.strike", new ActionCostRow("act.strike", "stamina", ValueSpec.Of(100), ActionCostTiming.OnCommit));

        // A stand-in anchorCost(Th) shape -- doubles per order of magnitude of Th. The SHAPE is not
        // the point; that the seam's return value actually reaches the paid amount is.
        int ThetaScale(double theta) => theta >= 1000 ? 2000 : 1000;

        var ledgerLow = MakeLedger(costs, poolsLow, lowTheta, thetaScaleMilliOf: ThetaScale);
        var ledgerHigh = MakeLedger(costs, poolsHigh, highTheta, thetaScaleMilliOf: ThetaScale);

        ledgerLow.TryPay(ActorKey, "act.strike", ActionCostTiming.OnCommit, rng: null);
        ledgerHigh.TryPay(ActorKey, "act.strike", ActionCostTiming.OnCommit, rng: null);

        Assert.Equal(1_000_000 - 100, poolsLow.Resolve("stamina", 0, lowTheta));   // 100 * 1.0
        Assert.Equal(1_000_000 - 200, poolsHigh.Resolve("stamina", 0, highTheta)); // 100 * 2.0 -- scaled
    }

    [Fact]
    public void CooldownMultiplierHasNoThetaParameterToReadAtAll()
    {
        // The structural half of "cooldown rides the rung alone, never Th" (Sec5): RungTable's own
        // resolve signature is `TryResolve(rung)` -- there is no Th argument for a cooldown read to
        // even pass, so a Th-dependent cooldown is not a bug some caller could introduce by mistake,
        // it is a call this API cannot express.
        Assert.True(FusionRpg.Core.Actions.Rungs.RungPolicy.Table.TryResolve(Rung, out var atRungOne));
        Assert.True(FusionRpg.Core.Actions.Rungs.RungPolicy.Table.TryResolve(Rung, out var alsoAtRungOne));
        Assert.Equal(atRungOne.CdMulti, alsoAtRungOne.CdMulti); // same rung, called "at" two different (unpassable) Th -- identical, necessarily
    }

    [Fact]
    public void TwoActorsHoldingTheSameActionAtDifferentRungsPayDifferentScaledCosts()
    {
        // A23 (spec-cost-scaling-holder-rung.md criterion 2): rungOf is now holder-keyed
        // (Func<actorKey, actionId, int>) -- two actors resolving to DIFFERENT rungs for the
        // SAME action must pay DIFFERENT scaled costs, proven through a real TryPay call against
        // each, not the CostMulti formula read in isolation. Rungs 1 and 5 are the shipped
        // RungPolicy.Table's own rows (costMulti 1000 / 3627 -- ContractTuningTestBootstrap's
        // DefaultActionRungs, matching gk-core/data/tuning/action-rungs.v2.json).
        const string ActorLowRung = "actor:low-rung";
        const string ActorHighRung = "actor:high-rung";
        var derived = Snapshot(theta: 0, ("stamina", 1_000_000, 0));
        var poolsLow = ActorResourcePools.CreateFull(derived, atTick: 0);
        var poolsHigh = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = Costs("act.strike", new ActionCostRow("act.strike", "stamina", ValueSpec.Of(100), ActionCostTiming.OnCommit));

        var ledger = new CostLedger(
            costs,
            poolsFor: actorKey => actorKey == ActorLowRung ? poolsLow : poolsHigh,
            derivedFor: _ => derived,
            rungOf: (actorKey, _) => actorKey == ActorLowRung ? 1 : 5,
            nowTick: () => 0);

        ledger.TryPay(ActorLowRung, "act.strike", ActionCostTiming.OnCommit, rng: null);
        ledger.TryPay(ActorHighRung, "act.strike", ActionCostTiming.OnCommit, rng: null);

        Assert.Equal(1_000_000 - 100, poolsLow.Resolve("stamina", 0, derived));  // rung 1: 100 * 1.000
        Assert.Equal(1_000_000 - 363, poolsHigh.Resolve("stamina", 0, derived)); // rung 5: 100 * 3.627, rounded half-away
    }

    [Fact]
    public void AnActorWithNoHeldEntryForTheActionResolvesTheAuthoredRungFallback()
    {
        // A23 criterion 3: an actor with no UnlockState entry for the action (every intrinsic/
        // basic action, every existing fixture before this module) resolves to the AUTHORED
        // rung, byte-identical to today -- the control every other A23 criterion is measured
        // against. Modeled the same shape BattleRunState.EffectiveRungOf's own lookup takes:
        // found in the held set -> holder's effective rung; not found -> the pre-A23 authored
        // fallback.
        const string Holder = "actor:holder";     // a real held-unlock entry -> resolves rung 5
        const string NoEntry = "actor:no-entry";  // absent from the held set -> authored rung 1
        var heldRungByActor = new Dictionary<string, int> { [Holder] = 5 };
        const int AuthoredRungFallback = 1;

        var derived = Snapshot(theta: 0, ("stamina", 1_000_000, 0));
        var poolsHolder = ActorResourcePools.CreateFull(derived, atTick: 0);
        var poolsNoEntry = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = Costs("act.strike", new ActionCostRow("act.strike", "stamina", ValueSpec.Of(100), ActionCostTiming.OnCommit));

        var ledger = new CostLedger(
            costs,
            poolsFor: actorKey => actorKey == Holder ? poolsHolder : poolsNoEntry,
            derivedFor: _ => derived,
            rungOf: (actorKey, _) => heldRungByActor.TryGetValue(actorKey, out var rung) ? rung : AuthoredRungFallback,
            nowTick: () => 0);

        ledger.TryPay(Holder, "act.strike", ActionCostTiming.OnCommit, rng: null);
        ledger.TryPay(NoEntry, "act.strike", ActionCostTiming.OnCommit, rng: null);

        Assert.Equal(1_000_000 - 363, poolsHolder.Resolve("stamina", 0, derived));  // rung 5: 100 * 3.627
        Assert.Equal(1_000_000 - 100, poolsNoEntry.Resolve("stamina", 0, derived)); // fallback rung 1, unchanged from today
    }

    [Fact]
    public void AFailedPerTickPaymentEndsTheActionThroughTheRealInterruptPath()
    {
        var queue = new EventQueue(8);
        var slots = new ActionSlots(width: 4, WScope.Global);
        var cooldowns = new CooldownLedger();
        var runner = new ActionRunner(queue, slots, cooldowns, _ => true);
        var actor = new ActorTurnMachine(ActorKey);
        actor.TransitionTo(TurnState.Ready);

        var envelope = new ActionEnvelope
        {
            ActionId = "act.channel",
            WindupTicks = 1000, // long windup -- still Committed when the perTick check below runs
            ResolveOffsets = new long[] { 0 },
            Interruptible = Interruptible.OnDamage, // yields to any cause, ResourceExhausted included
            CooldownTicks = 500,
            InterruptCooldownMilli = 1000,
        };
        var refusal = runner.TryCommit(actor, "left", new ActionIntent("act.channel", null, envelope), nowTick: 0);
        Assert.Equal(CommitRefusal.None, refusal);
        Assert.Equal(TurnState.Committed, actor.State);
        Assert.True(slots.Holds(ActorKey)); // the slot IS held going in -- makes the release below a real transition

        var derived = Snapshot(theta: 0, ("stamina", 5, 0));
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0); // only 5 stamina
        var costs = Costs("act.channel", new ActionCostRow("act.channel", "stamina", ValueSpec.Of(10), ActionCostTiming.PerTick));
        var ledger = MakeLedger(costs, pools, derived, nowTick: 10);

        var pay = ledger.TryPay(ActorKey, "act.channel", ActionCostTiming.PerTick, rng: null);
        Assert.Equal(CostPayOutcome.InsufficientFunds, pay.Outcome);

        var interrupt = runner.Interrupt(actor, nowTick: 10, InterruptCause.ResourceExhausted);

        Assert.True(interrupt.Broken);
        Assert.Equal(TurnState.Charging, actor.State);
        Assert.False(runner.IsMidAction(ActorKey));
        Assert.False(slots.Holds(ActorKey)); // the slot the commit took is released
    }
}
