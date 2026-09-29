using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Corpus;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// `lawn-combat-wire` T12c (spec-basic-attack-cost.md): <see cref="LawnBasicAttackCostGate"/>'s own
/// swing-scoped seam onto the SAME <see cref="CostLedger"/> battle already uses — never a second cost
/// authority (`guard-actor-hub.py`'s own SOLID lock). Every test here drives the real
/// <see cref="CostLedger"/>/<see cref="ActorResourcePools"/> types, never a mock of either, matching
/// this program's own verify command
/// (<c>dotnet test --filter "FullyQualifiedName~CostLedger|ActorResourcePools"</c>).
/// </summary>
public class LawnCostLedgerChargeTests
{
    const string ActorKey = "0xPLANT1";
    const int Rung = 1;
    const int Cost = 25;

    /// <summary>Deterministic in-memory cost template — independent of whatever balance pass has
    /// most recently retuned the real shipped `action-corpus-cost-templates.v{n}.json` (that contract
    /// is `LawnCombatCalibrationGuardTests`' own job, not this file's).</summary>
    static ActionCorpusCostTemplate FixedCostTemplate() => new(
        Categories: new Dictionary<ActionCategory, ActionCorpusCostTemplateRow>(),
        Kinds: new Dictionary<ActionKind, ActionCorpusCostTemplateRow>
        {
            [ActionKind.Basic] = new ActionCorpusCostTemplateRow("stamina", Cost, ActionCostTiming.OnCommit)
        });

    static ActorDerivedSnapshot Snapshot(long staminaMax, double regenPerTick = 0)
    {
        var registry = DerivedStatRegistry.CreateDefault();
        var composer = new DerivedComposer(registry);
        return composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.ResourceMax("stamina"), DerivedModifierOp.Flat, staminaMax, SourceId: "test"),
            new DerivedModifier(DerivedStatChannels.ResourceRegen("stamina"), DerivedModifierOp.Flat, regenPerTick, SourceId: "test"),
        });
    }

    static (LawnBasicAttackCostGate Gate, ActorResourcePools Pools, ActorDerivedSnapshot Derived) Build(
        long staminaMax, double regenPerTick, Func<long> nowTick)
    {
        var derived = Snapshot(staminaMax, regenPerTick);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = LawnBasicAttackCostRow.Build(FixedCostTemplate());
        var ledger = new CostLedger(costs, _ => pools, _ => derived, (_, _) => Rung, nowTick);
        return (new LawnBasicAttackCostGate(ledger), pools, derived);
    }

    // ------------------------------------------------------------------------------------------
    // One payment per swing, regardless of victim count (D8 / T12 acceptance).

    [Fact]
    public void OnePaymentPerSwingRegardlessOfVictimCount()
    {
        var (gate, pools, derived) = Build(staminaMax: 100, regenPerTick: 0, nowTick: () => 0);

        var v1 = gate.TryChargeForSwing(ActorKey, "swing-1", isFirstOfSwing: true);
        var v2 = gate.TryChargeForSwing(ActorKey, "swing-1", isFirstOfSwing: false);
        var v3 = gate.TryChargeForSwing(ActorKey, "swing-1", isFirstOfSwing: false);
        var v4 = gate.TryChargeForSwing(ActorKey, "swing-1", isFirstOfSwing: false);
        var v5 = gate.TryChargeForSwing(ActorKey, "swing-1", isFirstOfSwing: false);

        Assert.True(v1); Assert.True(v2); Assert.True(v3); Assert.True(v4); Assert.True(v5);
        // 100 - 25 = 75, spent EXACTLY ONCE -- a second/third/fourth/fifth charge would have driven
        // this to 50, 25, 0, and then refused.
        Assert.Equal(100 - Cost, pools.Resolve("stamina", 0, derived));
    }

    [Fact]
    public void ANewSwingPaysAgain()
    {
        var (gate, pools, derived) = Build(staminaMax: 100, regenPerTick: 0, nowTick: () => 0);

        gate.TryChargeForSwing(ActorKey, "swing-1", isFirstOfSwing: true);
        gate.TryChargeForSwing(ActorKey, "swing-1", isFirstOfSwing: false);
        var secondSwingFirst = gate.TryChargeForSwing(ActorKey, "swing-2", isFirstOfSwing: true);

        Assert.True(secondSwingFirst);
        Assert.Equal(100 - Cost - Cost, pools.Resolve("stamina", 0, derived));
    }

    // ------------------------------------------------------------------------------------------
    // At zero (insufficient) stamina: no rider for ANY victim of the swing, and nothing is spent.

    [Fact]
    public void AtInsufficientStaminaNoVictimOfTheSwingGetsTheRiderAndNothingIsSpent()
    {
        var (gate, pools, derived) = Build(staminaMax: 10, regenPerTick: 0, nowTick: () => 0); // < Cost (25)

        var v1 = gate.TryChargeForSwing(ActorKey, "swing-1", isFirstOfSwing: true);
        var v2 = gate.TryChargeForSwing(ActorKey, "swing-1", isFirstOfSwing: false);
        var v3 = gate.TryChargeForSwing(ActorKey, "swing-1", isFirstOfSwing: false);

        Assert.False(v1); Assert.False(v2); Assert.False(v3);
        Assert.Equal(10, pools.Resolve("stamina", 0, derived)); // untouched -- CostLedger.TryPay's
                                                                 // own validate-all-spend-none contract
    }

    // ------------------------------------------------------------------------------------------
    // Regen restores the SAME actor instance; its next swing (a NEW swing id) contributes again.

    [Fact]
    public void AfterRegenTheSameActorsNextSwingContributesAgain()
    {
        long tick = 0;
        var (gate, pools, derived) = Build(staminaMax: 30, regenPerTick: 0.025, nowTick: () => tick); // 25‰/tick

        // Drains the actor to 5 (30 - 25).
        var first = gate.TryChargeForSwing(ActorKey, "swing-1", isFirstOfSwing: true);
        Assert.True(first);
        Assert.Equal(5, pools.Resolve("stamina", tick, derived));

        // Immediately exhausted -- a second swing right now cannot afford the same cost.
        var tooSoon = gate.TryChargeForSwing(ActorKey, "swing-2", isFirstOfSwing: true);
        Assert.False(tooSoon);
        Assert.Equal(5, pools.Resolve("stamina", tick, derived)); // untouched by the refused attempt

        // Advance the SAME clock 1000 ticks at 25‰/tick == 25 whole units regenerated -- the SAME
        // ActorResourcePools instance (never a respawn), the SAME ptr/key.
        tick += 1000;
        Assert.Equal(30, pools.Resolve("stamina", tick, derived)); // capped at max, not overshot

        var afterRegen = gate.TryChargeForSwing(ActorKey, "swing-3", isFirstOfSwing: true);
        Assert.True(afterRegen);
        Assert.Equal(5, pools.Resolve("stamina", tick, derived));
    }

    /// <summary>Regen does not run while paused — a lawn clock that never advances (the injector's
    /// own `KernelDriveHost.NowTicks`, which freezes at `Time.timeScale == 0`) must not manufacture
    /// stamina out of nothing between two charges at the SAME tick.</summary>
    [Fact]
    public void RegenDoesNotAccrueWhileTheClockIsPaused()
    {
        const long pausedTick = 500;
        var (gate, pools, derived) = Build(staminaMax: 30, regenPerTick: 0.025, nowTick: () => pausedTick);

        gate.TryChargeForSwing(ActorKey, "swing-1", isFirstOfSwing: true); // -> 5
        Assert.Equal(5, pools.Resolve("stamina", pausedTick, derived));

        // The clock never moves (paused) -- a second read at the SAME tick must read the SAME value,
        // never having silently accrued anything in between.
        Assert.Equal(5, pools.Resolve("stamina", pausedTick, derived));
        Assert.Equal(5, pools.Resolve("stamina", pausedTick, derived));
    }

    // ------------------------------------------------------------------------------------------
    // The exact failure class this task's own restored acceptance criterion names: CostLedger.Check
    // (and TryPay) only ever inspect rows authored for the TIMING the caller asks about. The lawn's
    // charge site (LawnBasicAttackCostCharger, injector) hardcodes ActionCostTiming.OnCommit and
    // fails loudly at construction if the real shipped row is ever authored otherwise -- this test
    // proves WHY that guard exists: without it, a perTick-authored row would make an OnCommit charge
    // silently vacuous (Success, nothing spent), never a visible failure.

    [Fact]
    public void APerTickAuthoredRowMakesAnOnCommitChargeSilentlyVacuousTheFailureTheInjectorGuardPrevents()
    {
        var derived = Snapshot(staminaMax: 100);
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = new Dictionary<string, IReadOnlyList<ActionCostRow>>
        {
            [LawnBasicAttackCostRow.ActionId] = new[]
            {
                // Authored perTick, NOT onCommit -- the exact drift this task's own charge site must
                // detect and refuse to ship silently against.
                new ActionCostRow(LawnBasicAttackCostRow.ActionId, "stamina", ValueSpec.Of(Cost), ActionCostTiming.PerTick)
            }
        };
        var ledger = new CostLedger(costs, _ => pools, _ => derived, (_, _) => Rung, () => 0);

        // The lawn's own charge site always calls TryPay with OnCommit (LawnBasicAttackCostGate).
        var result = ledger.TryPay(ActorKey, LawnBasicAttackCostRow.ActionId, ActionCostTiming.OnCommit, rng: null);

        Assert.Equal(CostPayOutcome.Paid, result.Outcome); // "succeeds" -- there was simply nothing to pay
        Assert.Equal(100, pools.Resolve("stamina", 0, derived)); // NOTHING was spent -- the vacuous state
    }

    // ------------------------------------------------------------------------------------------
    // Independence: two different actors, and two different gates, never bleed into each other.

    [Fact]
    public void TwoDifferentActorsSharingOneGateNeverBleedIntoEachOther()
    {
        var derived = Snapshot(staminaMax: 100);
        var poolsA = ActorResourcePools.CreateFull(derived, atTick: 0);
        var poolsB = ActorResourcePools.CreateFull(derived, atTick: 0);
        var byActor = new Dictionary<string, ActorResourcePools> { ["actorA"] = poolsA, ["actorB"] = poolsB };
        var costs = LawnBasicAttackCostRow.Build(FixedCostTemplate());
        var ledger = new CostLedger(costs, key => byActor[key], _ => derived, (_, _) => Rung, () => 0);
        var gate = new LawnBasicAttackCostGate(ledger);

        // Same swing id from two different actors (theoretically impossible on a real lawn -- swing
        // ids are ptr/frame-derived -- but the gate's cache is swing-keyed, not actor-keyed, so this
        // is the sharpest test that actor A's charge never touches actor B's pool.)
        gate.TryChargeForSwing("actorA", "swing-shared", isFirstOfSwing: true);

        Assert.Equal(100 - Cost, poolsA.Resolve("stamina", 0, derived));
        Assert.Equal(100, poolsB.Resolve("stamina", 0, derived)); // untouched
    }
}
