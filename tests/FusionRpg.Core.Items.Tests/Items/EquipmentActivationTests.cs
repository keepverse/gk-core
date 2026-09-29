using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Activation;
using FusionRpg.Core.Items.Requirements;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// Shared fixtures for the activation module's tests. Everything here builds REAL collaborators —
/// a real <see cref="ActorResourcePools"/>, a real <see cref="CostLedger"/> and the real
/// <see cref="CostLedgerEquipmentPayment"/> adapter — so a passing test proves the production rule and
/// not a test double that happens to agree with it.
/// </summary>
internal static class ActivationFixtures
{
    public const string Deployment = "battle:run-1";

    public static ActorDerivedSnapshot Snapshot(params (string resourceId, double max, double regen)[] resources)
    {
        var registry = DerivedStatRegistry.CreateDefault();
        var composer = new DerivedComposer(registry);
        var mods = new List<DerivedModifier>
        {
            new(DerivedStatChannels.ProgressionPower, DerivedModifierOp.Flat, 1.0, SourceId: "test"),
            new(DerivedStatChannels.ProgressionRealm, DerivedModifierOp.Flat, 1.0, SourceId: "test"),
        };
        foreach (var (id, max, regen) in resources)
        {
            mods.Add(new DerivedModifier(DerivedStatChannels.ResourceMax(id), DerivedModifierOp.Flat, max, SourceId: "test"));
            mods.Add(new DerivedModifier(DerivedStatChannels.ResourceRegen(id), DerivedModifierOp.Flat, regen, SourceId: "test"));
        }
        return composer.Compose(mods);
    }

    /// <summary>All six pools, every one full, with the named overrides applied on top — so a test
    /// only states the resource it is about.</summary>
    public static ActorResourcePools Pools(
        ActorDerivedSnapshot derived, long atTick, params (string resourceId, long value)[] overrides)
    {
        var stored = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var id in DerivedStatChannels.ResourceIds)
            stored[id] = ResourceChannelReader.Max(derived, id);
        foreach (var (id, value) in overrides) stored[id] = value;
        return ActorResourcePools.FromStored(stored, atTick);
    }

    public static CostLedger Ledger(ActorResourcePools pools, ActorDerivedSnapshot derived, Func<long> nowTick) =>
        new(new Dictionary<string, IReadOnlyList<ActionCostRow>>(), _ => pools, _ => derived, (_, _) => 1, nowTick);

    /// <summary>The deployment's logical clock as the ledger sees it. In production this IS the
    /// deployment clock's own <c>NowTick</c> — the scheduler and the ledger read one clock. A test
    /// that moves the tick must move this with it, or it is testing an impossible wiring.</summary>
    public static CostLedger Ledger(ActorResourcePools pools, ActorDerivedSnapshot derived, TickBox tick) =>
        Ledger(pools, derived, () => tick.Value);

    public static EquipmentActorState Actor(
        ActorResourcePools pools, ActorDerivedSnapshot derived, int level = 50, AptitudeAllocation? allocation = null) =>
        new(level, allocation ?? AptitudeAllocation.Empty, pools, derived);

    public static EquipmentAssignmentIdentity Id(
        string specimenId = "spec-1",
        string role = "armament-primary",
        string refKind = EquipRefKinds.Rolled,
        string refId = "it-1",
        string deploymentKey = Deployment) =>
        new(deploymentKey, specimenId, role, refKind, refId);

    public static RequirementProfile UpkeepProfile(
        string resourceId, long cost, long reserve, long periodTicks, int? minimumLevel = null) =>
        new(minimumLevel, null, new UpkeepClause(resourceId, reserve, cost, periodTicks),
            RequirementProfileKind.Sustained);

    public static RequirementProfile LevelTrialProfile(int minimumLevel) =>
        new(minimumLevel, null, null, RequirementProfileKind.Level);

    public static RequirementProfile FreeProfile() =>
        new(null, null, null, RequirementProfileKind.None);
}

/// <summary>A mutable logical tick, shared by the deployment clock and the ledger so a test cannot
/// wire them to different clocks.</summary>
internal sealed class TickBox
{
    public long Value { get; set; }
}

/// <summary>A deployment-shaped logical clock with a mutable tick and a mutable actor table — the
/// adapter contract every real runtime supplies (lawn board, battle run, Delve run, siege
/// engagement). <see cref="NowTick"/> freezing is how a pause is expressed.</summary>
internal sealed class FakeDeploymentClock : IEquipmentDeploymentClock
{
    readonly Dictionary<string, EquipmentActorState> _actors = new(StringComparer.Ordinal);

    public FakeDeploymentClock(string deploymentKey = ActivationFixtures.Deployment) => DeploymentKey = deploymentKey;

    public string DeploymentKey { get; }

    public long NowTick { get; set; }

    public void Set(string specimenId, EquipmentActorState state) => _actors[specimenId] = state;

    public bool TryResolveActor(string specimenId, out EquipmentActorState state) =>
        _actors.TryGetValue(specimenId, out state);
}

public class EquipmentActivationTests
{
    const string Deployment = ActivationFixtures.Deployment;

    // ── trial, first activation, schedule ────────────────────────────────────────────

    [Fact]
    public void An_unmet_level_trial_equips_the_item_with_no_effects_and_no_schedule()
    {
        var derived = ActivationFixtures.Snapshot(("stamina", 100, 0));
        var pools = ActivationFixtures.Pools(derived, 0);
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var service = new EquipmentActivationService(store, new CostLedgerEquipmentPayment(
            ActivationFixtures.Ledger(pools, derived, () => 0)));

        var id = ActivationFixtures.Id();
        var status = service.Evaluate(id, ActivationFixtures.LevelTrialProfile(60),
            ActivationFixtures.Actor(pools, derived, level: 10), 0);

        Assert.Equal(EquipmentActivationState.Trial, status.State);
        Assert.Equal(EquipmentActivationReasonKind.LevelUnmet, status.Reason.Kind);
        Assert.Null(status.NextDueTick);
        Assert.False(store.IsActive(id));
        // The durable assignment is not this store's business — nothing here removed anything.
        Assert.Equal(100L, pools.Resolve("stamina", 0, derived));
    }

    [Fact]
    public void A_met_trial_with_no_upkeep_activates_with_no_schedule()
    {
        var derived = ActivationFixtures.Snapshot(("stamina", 100, 0));
        var pools = ActivationFixtures.Pools(derived, 0);
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var service = new EquipmentActivationService(store, new CostLedgerEquipmentPayment(
            ActivationFixtures.Ledger(pools, derived, () => 0)));

        var id = ActivationFixtures.Id();
        var status = service.Evaluate(id, ActivationFixtures.FreeProfile(), ActivationFixtures.Actor(pools, derived), 0);

        Assert.Equal(EquipmentActivationState.Active, status.State);
        Assert.True(store.IsActive(id));
        Assert.Null(status.NextDueTick);
        Assert.Equal(100L, pools.Resolve("stamina", 0, derived));
    }

    [Fact]
    public void First_activation_costs_nothing_and_schedules_the_first_payment_one_period_out()
    {
        var derived = ActivationFixtures.Snapshot(("stamina", 100, 0));
        var pools = ActivationFixtures.Pools(derived, 0, ("stamina", 100));
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var service = new EquipmentActivationService(store, new CostLedgerEquipmentPayment(
            ActivationFixtures.Ledger(pools, derived, () => 0)));

        var id = ActivationFixtures.Id();
        var status = service.Evaluate(id, ActivationFixtures.UpkeepProfile("stamina", 30, 0, 10),
            ActivationFixtures.Actor(pools, derived), nowTick: 5);

        Assert.Equal(EquipmentActivationState.Active, status.State);
        Assert.Equal(15L, status.NextDueTick);
        Assert.Equal(100L, pools.Resolve("stamina", 5, derived));   // equipping itself costs nothing
    }

    [Fact]
    public void A_due_tick_charges_exactly_one_interval_and_spends_the_pool()
    {
        var derived = ActivationFixtures.Snapshot(("stamina", 100, 0));
        var pools = ActivationFixtures.Pools(derived, 0, ("stamina", 100));
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var service = new EquipmentActivationService(store, new CostLedgerEquipmentPayment(
            ActivationFixtures.Ledger(pools, derived, () => 0)));

        var id = ActivationFixtures.Id();
        var profile = ActivationFixtures.UpkeepProfile("stamina", 30, 0, 10);
        service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 0);

        var step = service.EvaluateStep(id, profile, ActivationFixtures.Actor(pools, derived), 10);

        Assert.True(step.ChargedInterval);
        Assert.Equal(20L, step.Status.NextDueTick);
        Assert.Equal(70L, pools.Resolve("stamina", 10, derived));
    }

    [Fact]
    public void A_delayed_frame_charges_every_due_interval_and_lands_on_the_next_future_tick()
    {
        var derived = ActivationFixtures.Snapshot(("stamina", 1000, 0));
        var pools = ActivationFixtures.Pools(derived, 0, ("stamina", 1000));
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var service = new EquipmentActivationService(store, new CostLedgerEquipmentPayment(
            ActivationFixtures.Ledger(pools, derived, () => 0)));
        var scheduler = new EquipmentUpkeepScheduler(service);
        var clock = new FakeDeploymentClock();
        var id = ActivationFixtures.Id();
        var profile = ActivationFixtures.UpkeepProfile("stamina", 5, 0, 10);

        service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 0);   // due at 10
        clock.NowTick = 100;
        clock.Set("spec-1", ActivationFixtures.Actor(pools, derived));
        var outcomes = scheduler.AdvanceDue(clock, new[] { new EquipmentAssignmentInput(id, profile) });

        var outcome = Assert.Single(outcomes);
        Assert.Equal(10, outcome.IntervalsCharged);                       // ticks 10,20,…,100
        Assert.Equal(110L, outcome.Status!.Value.NextDueTick);
        Assert.Equal(950L, pools.Resolve("stamina", 100, derived));
    }

    // ── shortfall, reserve, contention, ordering ─────────────────────────────────────

    [Fact]
    public void A_shortfall_spends_nothing_and_suspends_only_its_own_item()
    {
        var derived = ActivationFixtures.Snapshot(("stamina", 100, 0));
        var pools = ActivationFixtures.Pools(derived, 0, ("stamina", 40));
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var service = new EquipmentActivationService(store, new CostLedgerEquipmentPayment(
            ActivationFixtures.Ledger(pools, derived, () => 0)));

        var id = ActivationFixtures.Id();
        var profile = ActivationFixtures.UpkeepProfile("stamina", 30, 25, 10);
        service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 0);
        var step = service.EvaluateStep(id, profile, ActivationFixtures.Actor(pools, derived), 10);

        Assert.Equal(EquipmentActivationState.Suspended, step.Status.State);
        Assert.Equal(EquipmentActivationReasonKind.UpkeepShortfall, step.Status.Reason.Kind);
        Assert.Equal("stamina", step.Status.Reason.ResourceId);
        Assert.Null(step.Status.NextDueTick);
        Assert.Equal(40L, pools.Resolve("stamina", 10, derived));         // no partial debit
    }

    [Fact]
    public void Canonical_contention_leaves_the_same_active_set_regardless_of_source_order()
    {
        var derived = ActivationFixtures.Snapshot(("stamina", 100, 0));
        var profile = ActivationFixtures.UpkeepProfile("stamina", 6, 0, 10);

        static (string[] Active, long Left) Run(bool reversed, ActorDerivedSnapshot derived, RequirementProfile profile)
        {
            var pools = ActivationFixtures.Pools(derived, 0, ("stamina", 10));
            var store = new EquipmentRunStatusStore();
            store.BeginDeployment(Deployment);
            var service = new EquipmentActivationService(store, new CostLedgerEquipmentPayment(
                ActivationFixtures.Ledger(pools, derived, () => 0)));
            var scheduler = new EquipmentUpkeepScheduler(service);
            var clock = new FakeDeploymentClock();
            clock.Set("spec-1", ActivationFixtures.Actor(pools, derived));

            var first = ActivationFixtures.Id(role: "armament-primary", refId: "it-a");
            var second = ActivationFixtures.Id(role: "core-guard", refId: "it-b");
            var assignments = reversed
                ? new[] { new EquipmentAssignmentInput(second, profile), new EquipmentAssignmentInput(first, profile) }
                : new[] { new EquipmentAssignmentInput(first, profile), new EquipmentAssignmentInput(second, profile) };

            foreach (var a in assignments)
                service.Evaluate(a.Identity, a.Profile, ActivationFixtures.Actor(pools, derived), 0);

            clock.NowTick = 10;
            scheduler.AdvanceDue(clock, assignments);

            var active = new List<string>();
            foreach (var a in assignments)
                if (store.IsActive(a.Identity)) active.Add(a.Identity.RefId);
            active.Sort(StringComparer.Ordinal);
            return (active.ToArray(), pools.Resolve("stamina", 10, derived));
        }

        var forward = Run(reversed: false, derived, profile);
        var backward = Run(reversed: true, derived, profile);

        Assert.Equal(new[] { "it-a" }, forward.Active);   // armament-primary wins ordinally
        Assert.Equal(forward.Active, backward.Active);
        Assert.Equal(forward.Left, backward.Left);
        Assert.Equal(4L, forward.Left);
    }

    [Fact]
    public void A_due_tick_is_processed_for_every_assignment_in_canonical_order()
    {
        var derived = ActivationFixtures.Snapshot(("stamina", 100, 0));
        var pools = ActivationFixtures.Pools(derived, 0, ("stamina", 100));
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var service = new EquipmentActivationService(store, new CostLedgerEquipmentPayment(
            ActivationFixtures.Ledger(pools, derived, () => 0)));
        var scheduler = new EquipmentUpkeepScheduler(service);
        var clock = new FakeDeploymentClock();
        clock.Set("spec-1", ActivationFixtures.Actor(pools, derived));
        var profile = ActivationFixtures.UpkeepProfile("stamina", 5, 0, 10);

        var ids = new[]
        {
            ActivationFixtures.Id(role: "core-guard", refId: "it-b"),
            ActivationFixtures.Id(role: "armament-primary", refId: "it-a"),
            ActivationFixtures.Id(role: "mantle", refId: "it-c"),
        };
        foreach (var id in ids) service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 0);

        clock.NowTick = 10;
        var outcomes = scheduler.AdvanceDue(clock, ids.Select(i => new EquipmentAssignmentInput(i, profile)).ToArray());

        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal(1, o.IntervalsCharged));
        Assert.Equal(85L, pools.Resolve("stamina", 10, derived));
        // Canonical order is specimen, role, refKind, refId — asserted on the report itself.
        Assert.Equal(
            new[] { "armament-primary", "core-guard", "mantle" },
            outcomes.Select(o => o.Identity.Role).ToArray());
    }

    [Fact]
    public void A_cross_deployment_assignment_is_refused_rather_than_charged_against_another_run()
    {
        var derived = ActivationFixtures.Snapshot(("stamina", 100, 0));
        var pools = ActivationFixtures.Pools(derived, 0);
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var scheduler = new EquipmentUpkeepScheduler(new EquipmentActivationService(
            store, new CostLedgerEquipmentPayment(ActivationFixtures.Ledger(pools, derived, () => 0))));
        var clock = new FakeDeploymentClock("battle:run-2");
        clock.Set("spec-1", ActivationFixtures.Actor(pools, derived));
        var foreign = ActivationFixtures.Id(deploymentKey: "battle:run-1");

        Assert.Throws<InvalidOperationException>(() => scheduler.AdvanceDue(
            clock, new[] { new EquipmentAssignmentInput(foreign, ActivationFixtures.FreeProfile()) }));
    }

    // ── reactivation, offline, HP ────────────────────────────────────────────────────

    [Fact]
    public void Paused_or_offline_time_creates_no_retroactive_debit()
    {
        var derived = ActivationFixtures.Snapshot(("stamina", 100, 0));
        var pools = ActivationFixtures.Pools(derived, 0, ("stamina", 100));
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var service = new EquipmentActivationService(store, new CostLedgerEquipmentPayment(
            ActivationFixtures.Ledger(pools, derived, () => 0)));
        var scheduler = new EquipmentUpkeepScheduler(service);
        var clock = new FakeDeploymentClock();
        clock.Set("spec-1", ActivationFixtures.Actor(pools, derived));
        var id = ActivationFixtures.Id();
        var profile = ActivationFixtures.UpkeepProfile("stamina", 30, 0, 10);

        service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 0);   // due at 10
        clock.NowTick = 9;
        var early = scheduler.AdvanceDue(clock, new[] { new EquipmentAssignmentInput(id, profile) });
        var again = scheduler.AdvanceDue(clock, new[] { new EquipmentAssignmentInput(id, profile) });

        Assert.Equal(0, early[0].IntervalsCharged);
        Assert.Equal(0, again[0].IntervalsCharged);
        Assert.Equal(100L, pools.Resolve("stamina", 9, derived));
    }

    [Fact]
    public void A_non_hp_suspension_reactivates_on_affordability_without_back_charging()
    {
        var derived = ActivationFixtures.Snapshot(("stamina", 100, 0));
        var pools = ActivationFixtures.Pools(derived, 0, ("stamina", 100));
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var tick = new TickBox();
        var service = new EquipmentActivationService(store, new CostLedgerEquipmentPayment(
            ActivationFixtures.Ledger(pools, derived, tick)));
        var id = ActivationFixtures.Id();
        var profile = ActivationFixtures.UpkeepProfile("stamina", 50, 0, 10);

        service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 0);   // due at 10
        tick.Value = 10;
        pools.TrySpend("stamina", 100, 10, derived);                                  // drained before the due tick
        var suspended = service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 10);
        Assert.Equal(EquipmentActivationState.Suspended, suspended.State);
        Assert.Null(suspended.NextDueTick);

        // Five periods later the pool is refilled: the item returns, and the missed intervals are not
        // back-charged — the schedule restarts one period out from NOW, not from the old due tick.
        tick.Value = 50;
        pools.Add("stamina", 100, 50, derived);
        var reactivated = service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 50);

        Assert.Equal(EquipmentActivationState.Active, reactivated.State);
        Assert.Equal(60L, reactivated.NextDueTick);
        Assert.Equal(100L, pools.Resolve("stamina", 50, derived));
        Assert.False(reactivated.HpRecoveryLocked);
    }

    [Fact]
    public void An_unaffordable_reactivation_stays_suspended_with_a_named_shortfall()
    {
        var derived = ActivationFixtures.Snapshot(("stamina", 100, 0));
        var pools = ActivationFixtures.Pools(derived, 0, ("stamina", 100));
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var tick = new TickBox();
        var service = new EquipmentActivationService(store, new CostLedgerEquipmentPayment(
            ActivationFixtures.Ledger(pools, derived, tick)));
        var id = ActivationFixtures.Id();
        var profile = ActivationFixtures.UpkeepProfile("stamina", 50, 0, 10);

        service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 0);
        tick.Value = 10;
        pools.TrySpend("stamina", 100, 10, derived);
        service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 10);

        tick.Value = 40;
        var still = service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 40);

        Assert.Equal(EquipmentActivationState.Suspended, still.State);
        Assert.Equal("stamina", still.Reason.ResourceId);
        Assert.Null(still.NextDueTick);
        Assert.False(store.IsActive(id));
    }

    [Fact]
    public void An_hp_charge_is_non_lethal_and_cannot_cross_the_profiles_own_reserve()
    {
        var derived = ActivationFixtures.Snapshot(("hp", 100, 0));
        var pools = ActivationFixtures.Pools(derived, 0, ("hp", 100));
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var service = new EquipmentActivationService(store, new CostLedgerEquipmentPayment(
            ActivationFixtures.Ledger(pools, derived, () => 0)));

        // cost 100 against max 100 leaves 0, which is below the hp floor of 1: refused, pool untouched.
        var id = ActivationFixtures.Id();
        var lethal = ActivationFixtures.UpkeepProfile("hp", 100, 0, 10);
        var refused = service.Evaluate(id, lethal, ActivationFixtures.Actor(pools, derived), 0);
        Assert.Equal(EquipmentActivationState.Suspended, refused.State);
        Assert.Equal("hp", refused.Reason.ResourceId);
        Assert.Equal(100L, pools.Resolve("hp", 0, derived));

        // With a reserve the floor is the reserve itself: 60 - 30 = 30 < 40, so still refused.
        var pools2 = ActivationFixtures.Pools(derived, 0, ("hp", 60));
        var store2 = new EquipmentRunStatusStore();
        store2.BeginDeployment(Deployment);
        var service2 = new EquipmentActivationService(store2, new CostLedgerEquipmentPayment(
            ActivationFixtures.Ledger(pools2, derived, () => 0)));
        var reserved = ActivationFixtures.UpkeepProfile("hp", 30, 40, 10);
        service2.Evaluate(id, reserved, ActivationFixtures.Actor(pools2, derived), 0);   // hp 60, reserve 40 → 30 < 40
        Assert.Equal(EquipmentActivationState.Suspended, service2.Evaluate(
            id, reserved, ActivationFixtures.Actor(pools2, derived), 10).State);
        Assert.Equal(60L, pools2.Resolve("hp", 10, derived));
    }

    [Fact]
    public void An_hp_shortfall_latches_until_the_pool_is_full_and_then_still_pays_at_the_next_due_tick()
    {
        var derived = ActivationFixtures.Snapshot(("hp", 100, 0));
        var pools = ActivationFixtures.Pools(derived, 0, ("hp", 100));
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var tick = new TickBox();
        var service = new EquipmentActivationService(store, new CostLedgerEquipmentPayment(
            ActivationFixtures.Ledger(pools, derived, tick)));
        var id = ActivationFixtures.Id();
        var profile = ActivationFixtures.UpkeepProfile("hp", 30, 0, 10);

        service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 0);   // active, due at 10
        tick.Value = 10;
        pools.TrySpend("hp", 90, 10, derived);                                       // hp 10
        var latched = service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 10);
        Assert.Equal(EquipmentActivationState.Suspended, latched.State);
        Assert.True(latched.HpRecoveryLocked);
        Assert.Equal(EquipmentActivationReasonKind.HpRecoveryLocked, latched.Reason.Kind);

        // Not full yet: the latch holds even though the trial passes and the cost is affordable.
        tick.Value = 11;
        pools.Add("hp", 89, 11, derived);                                            // hp 99
        var stillLatched = service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 11);
        Assert.Equal(EquipmentActivationState.Suspended, stillLatched.State);
        Assert.True(stillLatched.HpRecoveryLocked);

        // Full: the latch clears, affordability is re-checked, and the item returns with a FRESH
        // schedule — the missed intervals are not back-charged.
        tick.Value = 12;
        pools.Add("hp", 1, 12, derived);                                             // hp 100
        var unlocked = service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 12);
        Assert.Equal(EquipmentActivationState.Active, unlocked.State);
        Assert.False(unlocked.HpRecoveryLocked);
        Assert.Equal(22L, unlocked.NextDueTick);

        // The lock clearing did not waive the next payment.
        tick.Value = 22;
        var paid = service.EvaluateStep(id, profile, ActivationFixtures.Actor(pools, derived), 22);
        Assert.True(paid.ChargedInterval);
        Assert.Equal(70L, pools.Resolve("hp", 22, derived));
    }

    // ── lapsed trials, unpayable resources, tuning gaps ──────────────────────────────

    [Fact]
    public void A_build_trial_that_lapses_while_active_suspends_the_item_and_clears_its_schedule()
    {
        var derived = ActivationFixtures.Snapshot(("stamina", 100, 0));
        var pools = ActivationFixtures.Pools(derived, 0, ("stamina", 100));
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var service = new EquipmentActivationService(store, new CostLedgerEquipmentPayment(
            ActivationFixtures.Ledger(pools, derived, () => 0)));
        var id = ActivationFixtures.Id();
        var profile = new RequirementProfile(
            10, new BuildTrialClause("Might", 5, null), new UpkeepClause("stamina", 0, 10, 10),
            RequirementProfileKind.Fixed);

        var built = ActivationFixtures.Actor(pools, derived, level: 20,
            allocation: AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", 5));
        Assert.Equal(EquipmentActivationState.Active, service.Evaluate(id, profile, built, 0).State);

        // The allocation is withdrawn — the trial lapses while the item was contributing.
        var unbuilt = ActivationFixtures.Actor(pools, derived, level: 20, allocation: AptitudeAllocation.Empty);
        var lapsed = service.Evaluate(id, profile, unbuilt, 5);

        Assert.Equal(EquipmentActivationState.Suspended, lapsed.State);
        Assert.Equal(EquipmentActivationReasonKind.BuildUnmet, lapsed.Reason.Kind);
        Assert.Null(lapsed.NextDueTick);
        Assert.Equal(100L, pools.Resolve("stamina", 5, derived));
    }

    [Fact]
    public void A_trial_that_was_never_met_reads_trial_rather_than_suspended()
    {
        var derived = ActivationFixtures.Snapshot(("stamina", 100, 0));
        var pools = ActivationFixtures.Pools(derived, 0);
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var service = new EquipmentActivationService(store, new CostLedgerEquipmentPayment(
            ActivationFixtures.Ledger(pools, derived, () => 0)));
        var id = ActivationFixtures.Id();
        var profile = new RequirementProfile(10, new BuildTrialClause("Might", 5, null), null,
            RequirementProfileKind.Fixed);

        var status = service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived, level: 20), 0);

        Assert.Equal(EquipmentActivationState.Trial, status.State);
        Assert.Equal(EquipmentActivationReasonKind.BuildUnmet, status.Reason.Kind);
    }

    [Fact]
    public void A_maintenance_resource_outside_the_actor_pools_reads_as_a_visible_shortfall_never_a_crash()
    {
        var derived = ActivationFixtures.Snapshot(("stamina", 100, 0));
        var pools = ActivationFixtures.Pools(derived, 0);
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var service = new EquipmentActivationService(store, new CostLedgerEquipmentPayment(
            ActivationFixtures.Ledger(pools, derived, () => 0)));

        // `souls` is what gk-core/data/tuning/equipment-requirements.v1.json actually draws. It is an empire
        // resource, not an actor pool, so the deployment cannot charge it — and the item says so.
        var status = service.Evaluate(ActivationFixtures.Id(), ActivationFixtures.UpkeepProfile("souls", 5, 0, 10),
            ActivationFixtures.Actor(pools, derived), 0);

        Assert.Equal(EquipmentActivationState.Suspended, status.State);
        Assert.Equal(EquipmentActivationReasonKind.UpkeepShortfall, status.Reason.Kind);
        Assert.Equal("souls", status.Reason.ResourceId);
    }

    [Fact]
    public void The_ledger_refuses_a_maintenance_charge_naming_a_non_pool_resource()
    {
        var derived = ActivationFixtures.Snapshot(("stamina", 100, 0));
        var pools = ActivationFixtures.Pools(derived, 0);
        var ledger = ActivationFixtures.Ledger(pools, derived, () => 0);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ledger.TryPayProfileMaintenance("spec-1", new FrozenResourceCharge("souls", 5, 0)));
    }

    [Fact]
    public void A_zero_period_upkeep_is_a_tuning_gap_that_throws_rather_than_looping_forever()
    {
        var derived = ActivationFixtures.Snapshot(("stamina", 100, 0));
        var pools = ActivationFixtures.Pools(derived, 0);
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var service = new EquipmentActivationService(store, new CostLedgerEquipmentPayment(
            ActivationFixtures.Ledger(pools, derived, () => 0)));

        Assert.Throws<ArgumentOutOfRangeException>(() => service.Evaluate(
            ActivationFixtures.Id(), ActivationFixtures.UpkeepProfile("stamina", 5, 0, 0),
            ActivationFixtures.Actor(pools, derived), 0));
    }

    [Fact]
    public void The_ledger_entry_point_spends_exactly_the_cost_and_preserves_the_reserve()
    {
        var derived = ActivationFixtures.Snapshot(("stamina", 100, 0));
        var pools = ActivationFixtures.Pools(derived, 0, ("stamina", 50));
        var ledger = ActivationFixtures.Ledger(pools, derived, () => 0);
        var charge = new FrozenResourceCharge("stamina", 20, 30);

        Assert.True(ledger.CanPayProfileMaintenance("spec-1", charge));   // 50 - 20 = 30 >= 30
        Assert.Equal(CostPayOutcome.Paid, ledger.TryPayProfileMaintenance("spec-1", charge).Outcome);
        Assert.Equal(30L, pools.Resolve("stamina", 0, derived));
        Assert.False(ledger.CanPayProfileMaintenance("spec-1", charge));  // 30 - 20 = 10 < 30
        Assert.Equal(CostPayOutcome.InsufficientFunds,
            ledger.TryPayProfileMaintenance("spec-1", charge).Outcome);
        Assert.Equal(30L, pools.Resolve("stamina", 0, derived));          // unchanged by the refusal
    }
}
