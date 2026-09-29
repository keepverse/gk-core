using FusionRpg.Core.Actions;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Grants;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// item-todo.md Checkpoint 1 / Phase 1's own named gap: <c>ApplyEquipProjection</c> and
/// <c>ApplyEquippedGrants</c> shipped tested and correct, with zero production callers. This proves
/// <c>RpgStore.MaterializeRolledEquipRuntime</c> — the new caller — actually closes both halves from one
/// assignment read, at the DAL level, matching <see cref="EquipRuntimeStoreTests"/>'s own established
/// fixture shape rather than the full summon/battle machinery
/// <c>RolledItemEquipRuntimeTests</c> (Server.Tests) proves the same thing through.
/// </summary>
public class MaterializeRolledEquipRuntimeTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public MaterializeRolledEquipRuntimeTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);

    string MintRolledInstance(string containerId)
    {
        var container = _store.GetContainer(containerId)!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, Tuning, out var inst);
        Assert.True(r.IsOk, r.ToString());
        return _store.SaveInstance(inst!);
    }

    [Fact]
    public void Materializing_projects_a_rolled_items_binding_at_unique_actor_scope()
    {
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.materialize-power", "", 1), KindId = "stat.derived",
            FamilyId = "atom.materialize-power", Variant = "", Tier = 1, Name = "Materialize Power",
            ParamsJson = "{\"channel\":\"combat.power.fire\",\"op\":\"flat\",\"amount\":30}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.materialize-power", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.materialize-power.t1") },
        }).IsOk);

        var instanceId = MintRolledInstance("item.materialize-power");
        _store.SaveAssignment("specimen-1", ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, instanceId);

        // Confirms the gap is real before proving the fix: nothing outside rpg_item_assignment exists.
        Assert.Empty(_store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1")));

        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);

        var binding = Assert.Single(_store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1")));
        Assert.Equal(instanceId, binding.InstanceId);

        var resolution = _store.ResolveBindings(
            new OwnerScope(OwnerKind.UniqueActor, "specimen-1"), new BindContext(RuntimeId.Battle));
        var atom = Assert.Single(resolution.AtomsByBinding!.Values.SelectMany(a => a));
        Assert.Equal("atom.materialize-power.t1", atom.AtomId);
    }

    [Fact]
    public void Materializing_writes_a_rolled_items_granted_action_at_entity_scope()
    {
        const string containerId = "item.materialize-grant";
        const string actionContainerId = "skill.materialize-grant-action";
        const string actionId = "skill.materialize-grant-fireball";

        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.materialize-grant", "", 1), KindId = "stat.modify",
            FamilyId = "atom.materialize-grant", Variant = "", Tier = 1, Name = "Materialize Grant",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId, Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.materialize-grant.t1") },
        }).IsOk);
        // The granted SKILL needs its own atom -- an atom row belongs to one container's tier lineage,
        // and SeedSkillAction (BuildSquadEquippedActionsTests) never reuses one across containers either.
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.materialize-grant-action", "", 1), KindId = "stat.modify",
            FamilyId = "atom.materialize-grant-action", Variant = "", Tier = 1, Name = "Materialize Grant Action",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = actionContainerId, Kind = ContainerKind.Skill,
            Atoms = new[] { new ContainerAtomRow(1, "atom.materialize-grant-action.t1") },
        }).IsOk);
        Assert.True(_store.UpsertAction(new ActionRow
        {
            ActionId = actionId, Name = actionId, Kind = ActionKind.Skill, Rung = 1,
            ContainerId = actionContainerId, Grantable = true, Tags = new[] { ActionTag.Offensive },
        }).IsOk);
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow(containerId, 0, actionId, ItemGrantRole.Granted));

        var instanceId = MintRolledInstance(containerId);
        _store.SaveAssignment("deadbeef02", ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, instanceId);

        var failures = _store.MaterializeRolledEquipRuntime("deadbeef02", level: 50);
        Assert.Empty(failures);

        var grants = _store.ListGrants(new OwnerScope(OwnerKind.Entity, "deadbeef02"));
        Assert.Contains(grants, g => g.ActionId == actionId);
        // The unlock-ladder scope must stay untouched -- an item's grant is a different lifetime.
        Assert.Empty(_store.ListGrants(new OwnerScope(OwnerKind.UniqueActor, "deadbeef02")));

        // Unequip must withdraw it -- re-materializing after removal converges to zero, never a delta.
        _store.RemoveAssignment("deadbeef02", ItemRole.ArmamentPrimary);
        _store.MaterializeRolledEquipRuntime("deadbeef02", level: 50);
        Assert.DoesNotContain(_store.ListGrants(new OwnerScope(OwnerKind.Entity, "deadbeef02")),
            g => g.ActionId == actionId);
    }
}
