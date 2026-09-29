using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// <c>rpg_item</c> — the second reachability root beside <c>effect_binding</c> (item-ideal.md
/// durable-ownership, module 1). Closes R1 (unequipping deleted an owned item), S2 (the missing
/// <c>effect_binding</c> FK), and C3 (an empty <c>effect_atom.name</c> loaded clean) — three defects
/// on code already running in production.
/// </summary>
public class OwnershipTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public OwnershipTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        Seed();
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    void Seed()
    {
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.vitality", "", 1),
            KindId = "stat.modify", FamilyId = "atom.vitality", Variant = "", Tier = 1,
            Name = "Vitality", ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}",
        }).IsOk);

        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.ember-band", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.vitality.t1") },
        }).IsOk);
    }

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    string SaveInstance()
    {
        var container = _store.GetContainer("item.ember-band")!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);

        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, Tuning, out var inst);
        Assert.True(r.IsOk, r.ToString());

        return _store.SaveInstance(inst!);
    }

    // ---- R1: two reachability roots ------------------------------------------------------------

    [Fact]
    public void Unequipping_does_not_delete_an_owned_instance()
    {
        var instanceId = SaveInstance();
        _store.SaveItem(new RpgItemRow
        {
            InstanceId = instanceId, PlayerId = "p1", AcquiredUtc = DateTime.UtcNow.ToString("O"),
        });

        var bindingId = Guid.NewGuid().ToString("N");
        Assert.True(_store.Bind(new BindingRow
        {
            InstanceId = instanceId, OwnerKind = OwnerKind.Player, OwnerKey = "1",
        }, bindingId).IsOk);

        // Unequip: withdraw the only binding. Before rpg_item existed this made the instance
        // unreachable and the sweep deleted it -- the live data-loss defect this test guards.
        _store.Withdraw(bindingId);

        Assert.NotNull(_store.GetInstance(instanceId));
        Assert.Equal(0, _store.CountOrphanInstances());
    }

    [Fact]
    public void Withdrawing_the_last_binding_of_an_unowned_instance_still_collects_it()
    {
        var instanceId = SaveInstance();
        var bindingId = Guid.NewGuid().ToString("N");
        Assert.True(_store.Bind(new BindingRow
        {
            InstanceId = instanceId, OwnerKind = OwnerKind.Player, OwnerKey = "1",
        }, bindingId).IsOk);

        // No SaveItem call -- this instance was never owned, only equipped (e.g. a granted trait's
        // own instance). The sweep must still collect it, or every match leaks rows forever.
        _store.Withdraw(bindingId);

        Assert.Null(_store.GetInstance(instanceId));
        Assert.Equal(0, _store.CountOrphanInstances());
    }

    // ---- S2: the effect_binding cascade --------------------------------------------------------

    [Fact]
    public void Deleting_an_instance_cascades_its_bindings_and_ownership()
    {
        var instanceId = SaveInstance();
        _store.SaveItem(new RpgItemRow
        {
            InstanceId = instanceId, PlayerId = "p1", AcquiredUtc = DateTime.UtcNow.ToString("O"),
        });
        var bindingId = Guid.NewGuid().ToString("N");
        Assert.True(_store.Bind(new BindingRow
        {
            InstanceId = instanceId, OwnerKind = OwnerKind.Player, OwnerKey = "1",
        }, bindingId).IsOk);

        // A deliberate disposition -- never a side effect of unequip or of the orphan sweep.
        _store.DeleteInstance(instanceId);

        Assert.Null(_store.GetInstance(instanceId));
        Assert.Empty(_store.ListBindings(new OwnerScope(OwnerKind.Player, "1")));
        Assert.Null(_store.GetItem(instanceId));
    }

    // ---- C3: an empty display name -------------------------------------------------------------

    [Fact]
    public void An_empty_atom_name_is_rejected_at_load()
    {
        var r = _store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.nameless", "", 1),
            KindId = "stat.modify", FamilyId = "atom.nameless", Variant = "", Tier = 1,
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}",
        });

        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, r.Reason);
        Assert.StartsWith("atom.empty-name:", r.Detail);
    }

    [Fact]
    public void ContentRuleViolated_carries_a_registered_rule_namespace()
    {
        // §2b.1's code, wired not just named: the empty-name refusal above is its first real
        // consumer, and the rule id it raises is under a namespace this program registered.
        Assert.True(ContentRuleNamespaces.IsRegistered("atom.empty-name"));
        Assert.Throws<InvalidOperationException>(() => AtomRejection.ContentRule("unregistered.rule", "x"));
    }

    // ---- rpg_item's own shape -------------------------------------------------------------------

    [Fact]
    public void Rpg_item_is_one_to_one_with_effect_instance()
    {
        var instanceId = SaveInstance();
        Assert.Null(_store.GetItem(instanceId));

        _store.SaveItem(new RpgItemRow
        {
            InstanceId = instanceId, PlayerId = "p1", AcquiredUtc = DateTime.UtcNow.ToString("O"),
        });

        var item = _store.GetItem(instanceId);
        Assert.NotNull(item);
        Assert.Equal(instanceId, item!.InstanceId);

        // Upserting again updates the SAME row -- one owner record per instance, never a second.
        _store.SaveItem(item with { Locked = true });
        Assert.Single(_store.ListItemsByPlayer("p1"));
        Assert.True(_store.GetItem(instanceId)!.Locked);
    }

    [Fact]
    public void A_salvaged_item_is_excluded_from_the_armoury_list_but_still_reads_by_id()
    {
        var instanceId = SaveInstance();
        _store.SaveItem(new RpgItemRow
        {
            InstanceId = instanceId, PlayerId = "p1", AcquiredUtc = DateTime.UtcNow.ToString("O"),
        });
        Assert.Single(_store.ListItemsByPlayer("p1"));

        // A deliberate disposition -- the row survives, only the armoury listing changes.
        _store.SaveItem(_store.GetItem(instanceId)! with { Disposition = "salvaged" });

        Assert.Empty(_store.ListItemsByPlayer("p1"));
        Assert.Equal("salvaged", _store.GetItem(instanceId)!.Disposition);
    }

    [Fact]
    public void No_rolled_value_is_duplicated_into_rpg_item()
    {
        var instanceId = SaveInstance();
        var before = _store.GetInstance(instanceId)!;

        _store.SaveItem(new RpgItemRow
        {
            InstanceId = instanceId, PlayerId = "p1", AcquiredUtc = DateTime.UtcNow.ToString("O"),
            Locked = true, Seen = true, Note = "a note",
        });

        // Ownership bookkeeping changes; the roll itself is untouched -- rolls live only in the
        // instance, never copied into the ownership row.
        var after = _store.GetInstance(instanceId)!;
        Assert.Equal(before.Atoms[0].ValuesJson, after.Atoms[0].ValuesJson);
        Assert.Equal(before.RollSeed, after.RollSeed);
    }
}
