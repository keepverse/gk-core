using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Activation;
using FusionRpg.Core.Items.Requirements;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// item/spec-equipment-activation.md §"Deployment-run status" — the store's own contract, plus the
/// deployment-lifecycle rows (lawn, Delve, siege) and the effect-read filter that makes activation
/// observable. Every lifecycle here is expressed through the SAME store operations a real host calls,
/// so the test is the contract the adapters implement.
/// </summary>
public class EquipmentDeploymentStatusTests
{
    const string Deployment = "battle:run-1";

    static (EquipmentRunStatusStore Store, EquipmentActivationService Service, ActorResourcePools Pools,
        ActorDerivedSnapshot Derived) Rig(string deploymentKey = Deployment, long stamina = 1000, TickBox? tick = null)
    {
        tick ??= new TickBox();
        var derived = ActivationFixtures.Snapshot(("stamina", 1000, 0));
        var pools = ActivationFixtures.Pools(derived, 0, ("stamina", stamina));
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(deploymentKey);
        var service = new EquipmentActivationService(store, new CostLedgerEquipmentPayment(
            ActivationFixtures.Ledger(pools, derived, tick)));
        return (store, service, pools, derived);
    }

    // ── the store contract ──────────────────────────────────────────────────────────

    [Fact]
    public void A_status_can_never_be_read_or_written_against_another_deployment()
    {
        var (store, service, pools, derived) = Rig("lawn:match-1");
        var mine = ActivationFixtures.Id(deploymentKey: "lawn:match-1");
        var other = ActivationFixtures.Id(deploymentKey: "lawn:match-2");

        service.Evaluate(mine, ActivationFixtures.FreeProfile(), ActivationFixtures.Actor(pools, derived), 0);
        Assert.True(store.IsActive(mine));

        // A different deployment key has no rows: nothing crosses, and no key is inferred from shape.
        Assert.False(store.IsActive(other));
        Assert.False(store.TryGet(other, out _));
        Assert.False(store.IsDeployed("lawn:match-2"));
        Assert.Equal(0, store.CountIn("lawn:match-2"));
    }

    [Fact]
    public void Writing_into_an_unopened_deployment_throws_rather_than_creating_an_unclearable_row()
    {
        var store = new EquipmentRunStatusStore();
        Assert.Throws<InvalidOperationException>(() =>
            store.Set(ActivationFixtures.Id(deploymentKey: "lawn:never-begun"), EquipmentRunStatus.Initial));
    }

    [Fact]
    public void The_revision_moves_only_when_something_observable_changes()
    {
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var id = ActivationFixtures.Id();

        var first = store.Set(id, new EquipmentRunStatus(
            EquipmentActivationState.Active, EquipmentActivationReason.None, 10, false, 0));
        var same = store.Set(id, new EquipmentRunStatus(
            EquipmentActivationState.Active, EquipmentActivationReason.None, 10, false, 999));
        var moved = store.Set(id, new EquipmentRunStatus(
            EquipmentActivationState.Suspended, EquipmentActivationReason.UpkeepShortfall("stamina"), null, false, 0));

        Assert.Equal(1L, first.Revision);
        Assert.Equal(1L, same.Revision);          // a caller cannot rewind or inflate the counter
        Assert.Equal(2L, moved.Revision);
    }

    [Fact]
    public void Ending_a_deployment_removes_every_row_and_reopening_a_key_starts_fresh()
    {
        var (store, service, pools, derived) = Rig();
        var id = ActivationFixtures.Id();
        service.Evaluate(id, ActivationFixtures.FreeProfile(), ActivationFixtures.Actor(pools, derived), 0);
        Assert.Equal(1, store.CountIn(Deployment));

        Assert.Equal(1, store.EndDeployment(Deployment));
        Assert.False(store.IsDeployed(Deployment));
        Assert.False(store.IsActive(id));
        Assert.Equal(0, store.CountIn(Deployment));

        // A reused key is a NEW run: BeginDeployment resets it, so no due tick or latch survives.
        Assert.Equal(0, store.BeginDeployment(Deployment));
    }

    [Fact]
    public void Clearing_an_assignment_removes_its_row_and_its_contribution()
    {
        var (store, service, pools, derived) = Rig();
        var id = ActivationFixtures.Id();
        service.Evaluate(id, ActivationFixtures.FreeProfile(), ActivationFixtures.Actor(pools, derived), 0);
        Assert.True(store.IsActive(id));

        Assert.True(store.ClearAssignment(id));
        Assert.False(store.IsActive(id));
        Assert.False(store.ClearAssignment(id));
    }

    [Fact]
    public void Rows_are_reported_in_the_specs_canonical_order()
    {
        var store = new EquipmentRunStatusStore();
        store.BeginDeployment(Deployment);
        var ids = new[]
        {
            ActivationFixtures.Id(specimenId: "spec-2", role: "armament-primary", refId: "it-a"),
            ActivationFixtures.Id(specimenId: "spec-1", role: "mantle", refId: "it-c"),
            ActivationFixtures.Id(specimenId: "spec-1", role: "core-guard", refId: "it-b"),
            ActivationFixtures.Id(specimenId: "spec-1", role: "core-guard", refKind: EquipRefKinds.Stock, refId: "it-a"),
            ActivationFixtures.Id(specimenId: "spec-1", role: "core-guard", refKind: EquipRefKinds.Rolled, refId: "it-b"),
        };
        foreach (var id in ids) store.Set(id, EquipmentRunStatus.Initial);

        var rows = store.RowsIn(Deployment);

        Assert.Equal(
            new[]
            {
                "battle:run-1/spec-1/core-guard/rolled/it-b",
                "battle:run-1/spec-1/core-guard/stock/it-a",
                "battle:run-1/spec-1/mantle/rolled/it-c",
                "battle:run-1/spec-2/armament-primary/rolled/it-a",
            },
            rows.Select(r => r.Identity.ToString()).ToArray());
    }

    [Fact]
    public void An_identity_requires_all_five_parts_and_matches_the_durable_assignments_own_id()
    {
        Assert.Throws<ArgumentException>(() => new EquipmentAssignmentIdentity("", "s", "r", "k", "i"));
        Assert.Throws<ArgumentException>(() => new EquipmentAssignmentIdentity("d", "s", "r", "k", ""));

        var assignment = new EquipAssignment("spec-9", ItemRole.CoreGuard, EquipRefKinds.Rolled, "it-9", "2026-01-01T00:00:00Z");
        var id = EquipmentAssignmentIdentity.Of("battle:run-9", assignment);

        Assert.Equal("battle:run-9", id.DeploymentKey);
        Assert.Equal("spec-9", id.SpecimenId);
        Assert.Equal("core-guard", id.Role);            // the registry's kebab-case id, never the enum name
        Assert.Equal(EquipRefKinds.Rolled, id.RefKind);
        Assert.Equal("it-9", id.RefId);
    }

    // ── deployment lifecycles ───────────────────────────────────────────────────────

    [Fact]
    public void Lawn_status_starts_only_at_bound_freezes_while_paused_and_clears_at_board_end()
    {
        // Before the PendingSpawn -> Bound edge the board has begun but this specimen has no status:
        // a roster assignment or a Deploying intent is not yet a deployment-run status.
        var (store, service, pools, derived) = Rig("lawn:match-7");
        var scheduler = new EquipmentUpkeepScheduler(service);
        var clock = new FakeDeploymentClock("lawn:match-7");
        var id = ActivationFixtures.Id(specimenId: "spec-7", deploymentKey: "lawn:match-7");
        var profile = ActivationFixtures.UpkeepProfile("stamina", 10, 0, 10);

        Assert.False(store.IsActive(id));   // not Bound yet

        // Bound at tick 0: status begins.
        clock.NowTick = 0;
        clock.Set("spec-7", ActivationFixtures.Actor(pools, derived));
        service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 0);
        Assert.True(store.IsActive(id));

        // Paused: the logical clock is frozen, so a scheduler pass charges nothing at all.
        clock.NowTick = 10;
        clock.Set("spec-7", ActivationFixtures.Actor(pools, derived));
        Assert.Equal(1, scheduler.AdvanceDue(clock, new[] { new EquipmentAssignmentInput(id, profile) })[0]
            .IntervalsCharged);

        clock.NowTick = 10;   // frozen
        Assert.Equal(0, scheduler.AdvanceDue(clock, new[] { new EquipmentAssignmentInput(id, profile) })[0]
            .IntervalsCharged);
        Assert.Equal(990L, pools.Resolve("stamina", 10, derived));

        // Bound -> Cleared / board end: the status is gone and the item can no longer be read active.
        Assert.Equal(1, store.EndDeployment("lawn:match-7"));
        Assert.False(store.IsActive(id));
    }

    [Fact]
    public void Delve_status_and_its_schedule_survive_room_boundaries_then_clear_at_run_end()
    {
        var (store, service, pools, derived) = Rig("delve:run-3", stamina: 1000);
        var scheduler = new EquipmentUpkeepScheduler(service);
        var clock = new FakeDeploymentClock("delve:run-3");
        var id = ActivationFixtures.Id(specimenId: "spec-3", deploymentKey: "delve:run-3");
        var profile = ActivationFixtures.UpkeepProfile("stamina", 10, 0, 10);
        clock.Set("spec-3", ActivationFixtures.Actor(pools, derived));

        clock.NowTick = 0;
        service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 0);
        var dueAfterRoomOne = store.TryGet(id, out var s1) ? s1.NextDueTick : null;
        Assert.Equal(10L, dueAfterRoomOne);

        // A room boundary is NOT a deployment end: the deployment key is unchanged, so the same status
        // and the same due tick are still there and the next due interval is charged normally.
        clock.NowTick = 10;
        Assert.Equal(1, scheduler.AdvanceDue(clock, new[] { new EquipmentAssignmentInput(id, profile) })[0]
            .IntervalsCharged);
        Assert.True(store.TryGet(id, out var s2));
        Assert.Equal(20L, s2.NextDueTick);
        Assert.Equal(990L, pools.Resolve("stamina", 10, derived));

        // The Delve deployment ends: everything clears.
        Assert.Equal(1, store.EndDeployment("delve:run-3"));
        Assert.False(store.IsActive(id));
    }

    [Fact]
    public void Siege_charges_only_individually_equipped_participants_and_never_a_legion_or_a_structure()
    {
        var (store, service, pools, derived) = Rig("siege:engagement-1", stamina: 1000);
        var scheduler = new EquipmentUpkeepScheduler(service);
        var clock = new FakeDeploymentClock("siege:engagement-1");
        var profile = ActivationFixtures.UpkeepProfile("stamina", 10, 0, 10);

        // Only the individually equipped participant is ever handed to the deployment's clock or the
        // scheduler's assignment list — a troop legion or a structure has no entry at all, so it can
        // never be charged, and it can never read active.
        var participant = ActivationFixtures.Id(specimenId: "spec-equipped", deploymentKey: "siege:engagement-1");
        var legion = ActivationFixtures.Id(
            specimenId: "legion-7", role: "retinue", refKind: EquipRefKinds.Stock, refId: "legion-kit",
            deploymentKey: "siege:engagement-1");
        clock.Set("spec-equipped", ActivationFixtures.Actor(pools, derived));

        service.Evaluate(participant, profile, ActivationFixtures.Actor(pools, derived), 0);
        clock.NowTick = 10;
        var outcomes = scheduler.AdvanceDue(clock, new[] { new EquipmentAssignmentInput(participant, profile) });

        Assert.Equal(1, outcomes.Count);
        Assert.Equal(1, outcomes[0].IntervalsCharged);
        Assert.True(store.IsActive(participant));
        Assert.False(store.IsActive(legion));
        Assert.False(store.TryGet(legion, out _));
    }

    [Fact]
    public void An_assignment_whose_actor_the_deployment_cannot_resolve_is_reported_and_never_charged()
    {
        var (store, service, pools, derived) = Rig("battle:run-8");
        var scheduler = new EquipmentUpkeepScheduler(service);
        var clock = new FakeDeploymentClock("battle:run-8");   // no actor registered
        var id = ActivationFixtures.Id(deploymentKey: "battle:run-8");
        var profile = ActivationFixtures.UpkeepProfile("stamina", 10, 0, 10);

        var outcomes = scheduler.AdvanceDue(clock, new[] { new EquipmentAssignmentInput(id, profile) });

        Assert.Null(outcomes[0].Status);
        Assert.Equal(0, outcomes[0].IntervalsCharged);
        Assert.False(store.IsActive(id));
        Assert.Equal(1000L, pools.Resolve("stamina", 0, derived));
    }

    // ── the effect-read filter ──────────────────────────────────────────────────────

    static AtomRow DerivedAtom(string familyId, long amount) => new()
    {
        AtomId = AtomRow.DeriveId(familyId, "", 1),
        KindId = "stat.derived",
        FamilyId = familyId,
        Variant = "",
        Tier = 1,
        Name = familyId,
        ParamsJson = $"{{\"channel\":\"{DerivedStatChannels.CombatPowerFire}\",\"op\":\"flat\",\"amount\":{amount}}}",
    };

    [Fact]
    public void A_suspended_items_atoms_reach_neither_the_derived_projection_nor_the_battle_read()
    {
        var tick = new TickBox();
        var (store, service, pools, derived) = Rig("battle:run-5", tick: tick);
        var clock = new FakeDeploymentClock("battle:run-5");
        clock.Set("spec-5", ActivationFixtures.Actor(pools, derived));
        var id = ActivationFixtures.Id(specimenId: "spec-5", refId: "it-5", deploymentKey: "battle:run-5");
        var profile = ActivationFixtures.UpkeepProfile("stamina", 10, 0, 10);
        service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 0);
        Assert.True(store.IsActive(id));

        var inputs = new[]
        {
            new EquippedAtomInput("armament-primary", "it-5", DerivedAtom("atom.filter-a", 200),
                RefKind: EquipRefKinds.Rolled),
        };
        var source = EquipAtomSource
            .FromEquippedResolver(_ => inputs)
            .WithActivationFilter("battle:run-5", i => store.IsActive(i));

        Assert.Single(source.DerivedAtomsFor("spec-5"));

        // Suspend it: the SAME source now projects nothing, and the durable input list is untouched.
        tick.Value = 10;
        pools.TrySpend("stamina", 1000, 10, derived);
        service.Evaluate(id, profile, ActivationFixtures.Actor(pools, derived), 10);
        Assert.False(store.IsActive(id));

        Assert.Empty(source.DerivedAtomsFor("spec-5"));
        Assert.Single(inputs);
    }

    [Fact]
    public void An_unfiltered_source_projects_every_binding_exactly_as_before()
    {
        var inputs = new[]
        {
            new EquippedAtomInput("armament-primary", "it-5", DerivedAtom("atom.filter-b", 30)),
        };
        var source = EquipAtomSource.FromEquippedResolver(_ => inputs);

        Assert.Single(source.DerivedAtomsFor("spec-5"));
    }

    [Fact]
    public void An_activation_filter_needs_its_deployment_key_and_a_key_needs_its_filter()
    {
        Assert.Throws<ArgumentException>(() => EquipAtomSource.FromEquippedResolver(
            _ => Array.Empty<EquippedAtomInput>(), deploymentKey: "battle:run-1", isActive: null));
        Assert.Throws<ArgumentException>(() => EquipAtomSource.FromEquippedResolver(
            _ => Array.Empty<EquippedAtomInput>(), deploymentKey: null, isActive: _ => true));
        Assert.Throws<ArgumentException>(() => EquipAtomSource.None.WithActivationFilter("", _ => true));
    }

    [Fact]
    public void A_filtered_source_refuses_an_input_whose_host_refkind_is_missing()
    {
        var source = EquipAtomSource
            .FromEquippedResolver(_ => new[]
            {
                new EquippedAtomInput("armament-primary", "it-5", DerivedAtom("atom.filter-c", 30)),
            })
            .WithActivationFilter("battle:run-1", _ => true);

        Assert.Throws<InvalidOperationException>(() => source.DerivedAtomsFor("spec-5"));
    }

    [Fact]
    public void The_filter_asks_the_activation_question_about_the_durable_assignment_identity()
    {
        var asked = new List<string>();
        var source = EquipAtomSource
            .FromEquippedResolver(_ => new[]
            {
                new EquippedAtomInput("core-guard", "it-5", DerivedAtom("atom.filter-d", 30),
                    RefKind: EquipRefKinds.Stock),
            })
            .WithActivationFilter("battle:run-6", i =>
            {
                asked.Add(i.ToString());
                return true;
            });

        Assert.Single(source.DerivedAtomsFor("spec-6"));
        Assert.Equal(new[] { "battle:run-6/spec-6/core-guard/stock/it-5" }, asked.ToArray());
    }
}
