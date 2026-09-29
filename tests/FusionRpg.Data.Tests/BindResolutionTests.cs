using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// E6's bind gate, on the path that actually runs it.
///
/// <para><b>Why this file exists.</b> <c>BindGate</c> shipped with 34 passing tests and was called
/// from nowhere but those tests — the same defect as E4's validator not wiring E2 and E3. A gate
/// exercised only by its own unit tests protects nothing.</para>
///
/// <para>The separation these pin: <c>Bind</c> is <b>persistence</b> and checks grammar and
/// existence, because a durable binding outlives any one runtime. The runtime and scope gate belongs
/// to <c>ResolveBindings</c>, where a host asks "what may execute here" — the same stored binding is
/// legal on the lawn and refused in battle, which is correct rather than a bug.</para>
/// </summary>
public class BindResolutionTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public BindResolutionTests()
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
        Add("atom.warding", "stat.modify", "{\"channel\":\"defense\",\"op\":\"flat\",\"amount\":10}");
        Add("atom.vitality", "stat.modify", "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}");
        Add("atom.cherry", "board.action", "{\"op\":\"cherry\"}", "{\"trigger\":\"OnDeath\"}");

        Container("trait.warded", "atom.warding.t1");
        Container("trait.stalwart", "atom.vitality.t1");
        Container("trait.groundskeeper", "atom.cherry.t1");

        void Add(string family, string kind, string paramsJson, string whenJson = "{}") =>
            Assert.True(_store.UpsertAtom(new AtomRow
            {
                AtomId = AtomRow.DeriveId(family, "", 1),
                KindId = kind, FamilyId = family, Variant = "", Tier = 1,
                Name = family, ParamsJson = paramsJson, WhenJson = whenJson,
            }).IsOk, family);

        void Container(string id, string atomId) =>
            Assert.True(_store.UpsertContainer(new ContainerRow
            {
                ContainerId = id, Kind = ContainerKind.Trait,
                Atoms = new[] { new ContainerAtomRow(1, atomId) },
            }).IsOk, id);
    }

    // T3.4 (content-scale): 20 is the pin -- contentScale(20) == 1.000 exactly.
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, // fixed anchor (Fixed* consts are `internal` to Core+Core.Tests only)
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    string BindOf(string containerId, OwnerKind kind, string key, string source = "test")
    {
        var container = _store.GetContainer(containerId)!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);

        Assert.True(Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, Tuning, out var inst).IsOk);

        var instanceId = _store.SaveInstance(inst!);
        var bindingId = Guid.NewGuid().ToString("N");

        Assert.True(_store.Bind(new BindingRow
        {
            InstanceId = instanceId, OwnerKind = kind, OwnerKey = key, Source = source,
        }, bindingId).IsOk);

        return bindingId;
    }

    // ---- G8 on the real path -------------------------------------------------------------------

    [Fact]
    public void A_defense_binding_at_a_narrow_scope_is_refused_by_resolution()
    {
        // The TakeDamage prefix reads one side-wide cached value, so this binding would apply to
        // nothing at all. It must not reach a host.
        BindOf("trait.warded", OwnerKind.Plant, "7");

        var resolved = _store.ResolveBindings(new OwnerScope(OwnerKind.Plant, "7"),
            new BindContext(RuntimeId.Lawn));

        Assert.Empty(resolved.Bindings);
        Assert.Single(resolved.Refused);
        Assert.Equal(AtomRejectionReason.ScopeUnsupported, resolved.Refused[0].Reason);
    }

    [Fact]
    public void The_same_defense_binding_resolves_at_match_scope()
    {
        BindOf("trait.warded", OwnerKind.Match, "");

        var resolved = _store.ResolveBindings(OwnerScope.Match, new BindContext(RuntimeId.Lawn));

        Assert.Single(resolved.Bindings);
        Assert.Empty(resolved.Refused);
    }

    // ---- the runtime matrix on the real path ------------------------------------------------------

    [Fact]
    public void A_board_kind_binding_does_not_resolve_in_battle()
    {
        BindOf("trait.groundskeeper", OwnerKind.Match, "");

        var lawn = _store.ResolveBindings(OwnerScope.Match, new BindContext(RuntimeId.Lawn));
        var battle = _store.ResolveBindings(OwnerScope.Match, new BindContext(RuntimeId.Battle));

        Assert.Single(lawn.Bindings);
        Assert.Empty(battle.Bindings);
        Assert.Equal(AtomRejectionReason.RuntimeUnsupported, battle.Refused[0].Reason);
    }

    [Fact]
    public void A_plan_only_kind_resolves_only_for_a_planner_host()
    {
        BindOf("trait.stalwart", OwnerKind.Match, "");

        Assert.Empty(_store.ResolveBindings(OwnerScope.Match, new BindContext(RuntimeId.Sim)).Bindings);
        Assert.Single(_store.ResolveBindings(OwnerScope.Match,
            new BindContext(RuntimeId.Sim, IsPlanner: true)).Bindings);
    }

    [Fact]
    public void One_refused_binding_does_not_hide_the_others()
    {
        // Whole-binding rejection, not whole-owner: a bad trait must not silently disarm a good one.
        BindOf("trait.warded", OwnerKind.Plant, "7", "bad");
        BindOf("trait.stalwart", OwnerKind.Plant, "7", "good");

        var resolved = _store.ResolveBindings(new OwnerScope(OwnerKind.Plant, "7"),
            new BindContext(RuntimeId.Lawn));

        Assert.Equal(new[] { "good" }, resolved.Bindings.Select(b => b.Source));
        Assert.Single(resolved.Refused);
    }

    [Fact]
    public void Resolution_preserves_the_effect_list_order()
    {
        var owner = new OwnerScope(OwnerKind.Player, "1");
        BindOf("trait.stalwart", OwnerKind.Player, "1", "low");
        BindOf("trait.stalwart", OwnerKind.Player, "1", "high");

        // Priority is equal here, so the content tiebreak decides — and resolution must not reorder.
        var stored = _store.ListBindings(owner).Select(b => b.BindingId).ToList();
        var resolved = _store.ResolveBindings(owner, new BindContext(RuntimeId.Lawn))
            .Bindings.Select(b => b.BindingId).ToList();

        Assert.Equal(stored, resolved);
    }

    // ---- catalog_revision: what makes StaleInstance detectable ------------------------------------

    [Fact]
    public void An_instance_records_the_catalog_revision_it_was_rolled_against()
    {
        var revision = _store.GetCatalogRevision();
        var id = BindOf("trait.stalwart", OwnerKind.Match, "");

        var binding = _store.ListBindings(OwnerScope.Match).Single(b => b.BindingId == id);

        Assert.Equal(revision, _store.GetInstance(binding.InstanceId)!.CatalogRevision);
    }

    [Fact]
    public void A_content_import_leaves_untouched_items_bindable()
    {
        // R2 (item-ideal.md D9's corrected sequencing, D32): compatibility is judged per atom, not by
        // one whole-instance catalog_revision equality. Bumping the revision with NO atom actually
        // changed used to refuse every circulating item regardless of whether it used the edited atom
        // -- that was the "one content import silently disables every rolled item" defect this test
        // used to assert as correct. It is the opposite: a global catalog edit that touches nothing
        // this instance uses must leave it bindable.
        BindOf("trait.stalwart", OwnerKind.Match, "");
        _store.BumpCatalogRevision();

        var resolved = _store.ResolveBindings(OwnerScope.Match, new BindContext(RuntimeId.Lawn));

        Assert.Single(resolved.Bindings);
        Assert.Empty(resolved.Refused);
    }

    [Fact]
    public void A_current_instance_is_not_stale()
    {
        BindOf("trait.stalwart", OwnerKind.Match, "");

        Assert.Single(_store.ResolveBindings(OwnerScope.Match, new BindContext(RuntimeId.Lawn)).Bindings);
    }

    [Fact]
    public void An_atom_whose_kind_changed_since_rolling_is_refused_and_only_it()
    {
        // D32: a params_json/when_json retune is a balance edit and must reach circulating gear --
        // only a kind_id change makes a frozen row unsafe to reuse, because it decides which executor
        // interprets the row at all. Two different items: one rolls the atom that gets re-kinded, one
        // does not -- only the first must be refused. trait.groundskeeper (board.action), not
        // trait.warded (stat.modify/defense, ScopeUnsupported outside match scope — G8), is the
        // control here so the "untouched" binding is not refused for an unrelated reason.
        var changed = BindOf("trait.stalwart", OwnerKind.Player, "9", "changed");
        BindOf("trait.groundskeeper", OwnerKind.Player, "9", "untouched");

        // Re-kind atom.vitality (stat.modify) into the exact shape atom.cherry already validates as
        // (board.action, {"op":"cherry"}, OnDeath) -- same atom_id, structurally different mechanism.
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.vitality", "", 1),
            KindId = "board.action", FamilyId = "atom.vitality", Variant = "", Tier = 1,
            Name = "Vitality", ParamsJson = "{\"op\":\"cherry\"}", WhenJson = "{\"trigger\":\"OnDeath\"}",
        }).IsOk);

        var resolved = _store.ResolveBindings(new OwnerScope(OwnerKind.Player, "9"),
            new BindContext(RuntimeId.Lawn));

        Assert.Equal(new[] { "untouched" }, resolved.Bindings.Select(b => b.Source));
        Assert.Single(resolved.Refused);
        Assert.Equal(changed, resolved.Refused[0].BindingId);
        Assert.Equal(AtomRejectionReason.StaleInstance, resolved.Refused[0].Reason);
        Assert.Contains("changed kind", resolved.Refused[0].Detail);
    }

    [Fact]
    public void A_disabled_atom_refuses_only_the_instances_carrying_it()
    {
        // Was already true of BindGate.Check (it refuses a disabled atom per binding) -- it was
        // simply unreachable while the blunt whole-instance catalog_revision check refused every
        // binding before BindGate ever ran. R2 removing that check is what makes this observable.
        var disabled = BindOf("trait.stalwart", OwnerKind.Player, "11", "disabled");
        BindOf("trait.groundskeeper", OwnerKind.Player, "11", "fine");

        var atom = _store.ListAtoms().Single(a => a.AtomId == AtomRow.DeriveId("atom.vitality", "", 1));
        Assert.True(_store.UpsertAtom(atom with { Enabled = false }).IsOk);

        var resolved = _store.ResolveBindings(new OwnerScope(OwnerKind.Player, "11"),
            new BindContext(RuntimeId.Lawn));

        Assert.Equal(new[] { "fine" }, resolved.Bindings.Select(b => b.Source));
        Assert.Single(resolved.Refused);
        Assert.Equal(disabled, resolved.Refused[0].BindingId);
        Assert.Equal(AtomRejectionReason.StaleInstance, resolved.Refused[0].Reason);
        Assert.Contains("is disabled", resolved.Refused[0].Detail);
    }

    // ---- orphan instances ---------------------------------------------------------------------

    [Fact]
    public void Clearing_session_bindings_does_not_leave_orphan_instances()
    {
        // entity: bindings are dropped at match end. The instances they pointed at have no other
        // owner and can never be reached again -- so every match would leak rows into a durable
        // database forever.
        for (var i = 0; i < 5; i++)
            BindOf("trait.stalwart", OwnerKind.Entity, "abc" + i);

        _store.ClearSessionScopedBindings();

        Assert.Equal(0, _store.CountOrphanInstances());
    }

    [Fact]
    public void Withdrawing_the_last_binding_does_not_leave_an_orphan_instance()
    {
        var id = BindOf("trait.stalwart", OwnerKind.Player, "1");

        _store.Withdraw(id);

        Assert.Equal(0, _store.CountOrphanInstances());
    }

    [Fact]
    public void An_instance_with_a_surviving_binding_is_never_collected()
    {
        BindOf("trait.stalwart", OwnerKind.Player, "1", "keep");
        var drop = BindOf("trait.stalwart", OwnerKind.Player, "1", "drop");

        _store.Withdraw(drop);

        Assert.Single(_store.ListBindings(new OwnerScope(OwnerKind.Player, "1")));
        Assert.Equal(0, _store.CountOrphanInstances());
    }
}
