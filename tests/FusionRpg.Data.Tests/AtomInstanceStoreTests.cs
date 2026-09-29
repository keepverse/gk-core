using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// E6's storage half: instances with their rolls frozen, and bindings that attach them to an owner.
///
/// <para>The ordering test is the load-bearing one — the actor effect list sorts by
/// <c>priority DESC</c> and then by <b>content-derived</b> keys, never by the generated
/// <c>binding_id</c>, or two runs of one container would consume the value stream in different
/// orders.</para>
/// </summary>
public class AtomInstanceStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public AtomInstanceStoreTests()
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
        foreach (var (family, tier) in new[] { ("atom.vitality", 1), ("atom.might", 1) })
            Assert.True(_store.UpsertAtom(new AtomRow
            {
                AtomId = AtomRow.DeriveId(family, "", tier),
                KindId = "stat.modify", FamilyId = family, Variant = "", Tier = tier,
                Name = family, ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}",
            }).IsOk);

        foreach (var id in new[] { "trait.stalwart", "item.ember-band" })
            Assert.True(_store.UpsertContainer(new ContainerRow
            {
                ContainerId = id,
                Kind = id.StartsWith("trait") ? ContainerKind.Trait : ContainerKind.Item,
                Atoms = new[] { new ContainerAtomRow(1, "atom.vitality.t1") },
            }).IsOk);
    }

    // T3.4 (content-scale): 20 is the pin -- contentScale(20) == 1.000 exactly, so this store test's
    // frozen values stay exactly what they were before content-scale existed.
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, // fixed anchor (Fixed* consts are `internal` to Core+Core.Tests only)
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    string SaveInstance(string containerId = "trait.stalwart", long seed = 42)
    {
        var container = _store.GetContainer(containerId)!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);

        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, seed, 20, Tuning, out var inst);
        Assert.True(r.IsOk, r.ToString());

        return _store.SaveInstance(inst!);
    }

    // ---- instances -------------------------------------------------------------------------------

    [Fact]
    public void An_instance_round_trips_with_its_frozen_values()
    {
        var id = SaveInstance();

        var back = _store.GetInstance(id);

        Assert.NotNull(back);
        Assert.Equal("trait.stalwart", back!.ContainerId);
        Assert.Equal(42, back.RollSeed);
        Assert.Single(back.Atoms);
        Assert.Contains("\"amount\":45", back.Atoms[0].ValuesJson);
    }

    [Fact]
    public void Power_is_null_on_a_stored_instance_until_E9_backfills_it()
    {
        var back = _store.GetInstance(SaveInstance())!;

        Assert.All(back.Atoms, a => Assert.Null(a.PowerJson));
    }

    [Fact]
    public void Two_instances_of_one_container_at_one_seed_are_content_identical()
    {
        // Different instance_ids, same content: the id and timestamp are generated, which is exactly
        // why the reproducibility comparison excludes them.
        var a = _store.GetInstance(SaveInstance(seed: 7))!;
        var b = _store.GetInstance(SaveInstance(seed: 7))!;

        Assert.NotEqual(a.InstanceId, b.InstanceId);
        Assert.Equal(a.ContentFingerprint(), b.ContentFingerprint());
    }

    [Fact]
    public void Re_saving_an_instance_replaces_its_atoms_rather_than_appending()
    {
        var id = SaveInstance();
        var inst = _store.GetInstance(id)!;

        _store.SaveInstance(inst with { Atoms = new[] { inst.Atoms[0] } }, id);

        Assert.Single(_store.GetInstance(id)!.Atoms);
    }

    [Fact]
    public void An_absent_instance_reads_as_null()
    {
        Assert.Null(_store.GetInstance("nope"));
    }

    // ---- bindings ---------------------------------------------------------------------------------

    [Fact]
    public void A_binding_round_trips_on_its_owner()
    {
        var inst = SaveInstance();

        var r = _store.Bind(new BindingRow
        {
            InstanceId = inst, OwnerKind = OwnerKind.Player, OwnerKey = "1",
            Slot = "ring", Priority = 5, Source = "test",
        });
        Assert.True(r.IsOk, r.ToString());

        var bindings = _store.ListBindings(new OwnerScope(OwnerKind.Player, "1"));

        Assert.Single(bindings);
        Assert.Equal("ring", bindings[0].Slot);
        Assert.Equal(5, bindings[0].Priority);
    }

    // ---- session-scoped housekeeping (completeness-audit.md C1) --------------------------------------

    [Fact]
    public void ClearSessionScopedBindings_removes_entity_bindings_and_their_now_orphaned_instance()
    {
        // The boot-sweep sequence Program.cs now runs: entity: bindings are never durable — IL2CPP
        // reuses the pointer — so a fresh boot treats every one as stale, whether the last shutdown
        // was clean or a crash.
        var inst = SaveInstance();
        var bound = _store.Bind(new BindingRow { InstanceId = inst, OwnerKind = OwnerKind.Entity, OwnerKey = "abc123" });
        Assert.True(bound.IsOk, bound.ToString());
        Assert.NotEmpty(_store.ListBindings(new OwnerScope(OwnerKind.Entity, "abc123")));

        var cleared = _store.ClearSessionScopedBindings();

        Assert.Equal(1, cleared);
        Assert.Empty(_store.ListBindings(new OwnerScope(OwnerKind.Entity, "abc123")));
        // The instance had no other binding, so it was collected as part of the same sweep — not
        // left behind for CountOrphanInstances to keep reporting forever.
        Assert.Equal(0, _store.CountOrphanInstances());
        Assert.Null(_store.GetInstance(inst));
    }

    [Fact]
    public void ClearSessionScopedBindings_leaves_durable_owner_scopes_alone()
    {
        var inst = SaveInstance();
        var bound = _store.Bind(new BindingRow { InstanceId = inst, OwnerKind = OwnerKind.Player, OwnerKey = "1" });
        Assert.True(bound.IsOk, bound.ToString());

        _store.ClearSessionScopedBindings();

        Assert.NotEmpty(_store.ListBindings(new OwnerScope(OwnerKind.Player, "1")));
        Assert.NotNull(_store.GetInstance(inst));
    }

    [Fact]
    public void CountOrphanInstances_reports_an_instance_no_binding_reaches_without_deleting_it()
    {
        // A read-only diagnostic, deliberately: the delete half lives in the sweep methods
        // (ClearSessionScopedBindings, Withdraw), not here. "Orphan" means exactly what the SQL
        // says — zero bindings right now — not "used to have one and lost it": a freshly-saved,
        // never-bound instance already counts, which is the correct definition (E6's own model is
        // that an instance is reachable only *through* a binding).
        var neverBound = SaveInstance();
        Assert.Equal(1, _store.CountOrphanInstances());

        var bound = _store.Bind(new BindingRow { InstanceId = neverBound, OwnerKind = OwnerKind.Entity, OwnerKey = "def456" });
        Assert.True(bound.IsOk, bound.ToString());
        // Now bound — the count drops back, and the read itself deleted nothing.
        Assert.Equal(0, _store.CountOrphanInstances());
        Assert.NotNull(_store.GetInstance(neverBound));
    }

    [Fact]
    public void A_malformed_owner_key_never_binds()
    {
        var inst = SaveInstance();

        // entity keys are lowercase hex with no 0x. Both spellings were once in circulation, and two
        // spellings of one pointer means a binding the withdraw path cannot match.
        var r = _store.Bind(new BindingRow
        {
            InstanceId = inst, OwnerKind = OwnerKind.Entity, OwnerKey = "0xABC",
        });

        Assert.Equal(AtomRejectionReason.BadOwnerKey, r.Reason);
        Assert.Empty(_store.ListBindings(new OwnerScope(OwnerKind.Entity, "0xABC")));
    }

    [Fact]
    public void Binding_a_missing_instance_is_a_stale_instance()
    {
        var r = _store.Bind(new BindingRow
        {
            InstanceId = "does-not-exist", OwnerKind = OwnerKind.Match, OwnerKey = "",
        });

        Assert.Equal(AtomRejectionReason.StaleInstance, r.Reason);
    }

    [Fact]
    public void Bindings_sort_by_priority_first()
    {
        var owner = new OwnerScope(OwnerKind.Player, "1");

        foreach (var (prio, source) in new[] { (1, "low"), (9, "high"), (5, "mid") })
            _store.Bind(new BindingRow
            {
                InstanceId = SaveInstance(), OwnerKind = OwnerKind.Player, OwnerKey = "1",
                Priority = prio, Source = source,
            });

        Assert.Equal(new[] { "high", "mid", "low" },
            _store.ListBindings(owner).Select(b => b.Source));
    }

    [Fact]
    public void Ties_break_on_content_not_on_the_generated_binding_id()
    {
        // Two equal-priority bindings from different containers must order by container, so the
        // sequence does not depend on which GUID happened to be minted first.
        var owner = new OwnerScope(OwnerKind.Player, "2");

        _store.Bind(new BindingRow
        {
            InstanceId = SaveInstance("trait.stalwart"), OwnerKind = OwnerKind.Player, OwnerKey = "2",
            Source = "trait",
        });
        _store.Bind(new BindingRow
        {
            InstanceId = SaveInstance("item.ember-band"), OwnerKind = OwnerKind.Player, OwnerKey = "2",
            Source = "item",
        });

        // "item.ember-band" sorts before "trait.stalwart" ordinally, whatever the binding ids are.
        Assert.Equal(new[] { "item", "trait" }, _store.ListBindings(owner).Select(b => b.Source));
    }

    [Fact]
    public void Withdraw_removes_one_binding_and_leaves_the_rest()
    {
        var owner = new OwnerScope(OwnerKind.Player, "1");

        var keep = Guid.NewGuid().ToString("N");
        var drop = Guid.NewGuid().ToString("N");
        _store.Bind(new BindingRow { InstanceId = SaveInstance(), OwnerKind = OwnerKind.Player, OwnerKey = "1", Source = "keep" }, keep);
        _store.Bind(new BindingRow { InstanceId = SaveInstance(), OwnerKind = OwnerKind.Player, OwnerKey = "1", Source = "drop" }, drop);

        Assert.True(_store.Withdraw(drop));
        Assert.False(_store.Withdraw(drop)); // idempotent: already gone

        Assert.Equal(new[] { "keep" }, _store.ListBindings(owner).Select(b => b.Source));
    }

    [Fact]
    public void Entity_bindings_are_cleared_at_match_end_and_others_survive()
    {
        // IL2CPP reuses pointers, so an entity binding that outlived a match would attach to whatever
        // object took its address.
        _store.Bind(new BindingRow { InstanceId = SaveInstance(), OwnerKind = OwnerKind.Entity, OwnerKey = "abc" });
        _store.Bind(new BindingRow { InstanceId = SaveInstance(), OwnerKind = OwnerKind.Player, OwnerKey = "1" });

        Assert.Equal(1, _store.ClearSessionScopedBindings());

        Assert.Empty(_store.ListBindings(new OwnerScope(OwnerKind.Entity, "abc")));
        Assert.Single(_store.ListBindings(new OwnerScope(OwnerKind.Player, "1")));
    }

    [Fact]
    public void Match_scope_binds_with_an_empty_key()
    {
        var r = _store.Bind(new BindingRow
        {
            InstanceId = SaveInstance(), OwnerKind = OwnerKind.Match, OwnerKey = "",
        });

        Assert.True(r.IsOk, r.ToString());
        Assert.Single(_store.ListBindings(OwnerScope.Match));
    }

    [Fact]
    public void Re_binding_the_same_id_bumps_its_revision_rather_than_duplicating()
    {
        var id = Guid.NewGuid().ToString("N");
        var inst = SaveInstance();

        _store.Bind(new BindingRow { InstanceId = inst, OwnerKind = OwnerKind.Player, OwnerKey = "1", Priority = 1 }, id);
        _store.Bind(new BindingRow { InstanceId = inst, OwnerKind = OwnerKind.Player, OwnerKey = "1", Priority = 7 }, id);

        var bindings = _store.ListBindings(new OwnerScope(OwnerKind.Player, "1"));

        Assert.Single(bindings);
        Assert.Equal(7, bindings[0].Priority);
        Assert.True(bindings[0].Revision > 1);
    }
}
