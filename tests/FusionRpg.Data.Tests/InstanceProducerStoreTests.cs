using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// T3.6 (`instance-producer`, ⭐ the payoff) — the Data-layer half: <c>RpgStore.ProduceAndBind</c>
/// really writes an instance and a binding, atomically, for a real owner, and
/// <c>ResolveBindings</c> — verified 2026-09-02 not to be hardcoded empty, just never written to
/// before this task — comes back non-empty for the first time in the repo's history.
///
/// <para>The fixture here is a <c>species-passive</c> container, never an <c>item</c> bound to an
/// equipped slot — the mixed-source invariant `spec-instance-producer.md` names explicitly: path 1
/// (this module) must not bind an equipped-item effect while path 2 (<c>mods_json</c>) is still the
/// live path for the same slot. That case is module 5's, deliberately excluded here.</para>
/// </summary>
public class InstanceProducerStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public InstanceProducerStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        Seed();
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    // Same fixed pin theta/tuning AtomInstanceStoreTests already established for this store.
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);
    const int PinTheta = 20;

    void Seed()
    {
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = "atom.vitality.t1", KindId = "stat.modify", FamilyId = "atom.vitality", Tier = 1,
            Name = "Vitality", ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}",
        }).IsOk);

        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "species-passive.hardy", Kind = ContainerKind.SpeciesPassive,
            Atoms = new[] { new ContainerAtomRow(1, "atom.vitality.t1") },
        }).IsOk);
    }

    static IReadOnlyList<string> NoDomains(string domain) => Array.Empty<string>();

    AtomRejection Produce(
        OwnerScope owner, out string? instanceId, out string? bindingId,
        string containerId = "species-passive.hardy", long seed = 1, string source = "test") =>
        _store.ProduceAndBind(
            _store.GetContainer(containerId)!, NoDomains, seed, PinTheta, Tuning,
            owner, slot: null, priority: 1, source, out instanceId, out bindingId);

    [Fact]
    public void Produce_writes_an_instance_and_a_binding_for_a_real_owner()
    {
        var owner = new OwnerScope(OwnerKind.Player, "1");

        var r = Produce(owner, out var instanceId, out var bindingId);

        Assert.True(r.IsOk, r.ToString());
        Assert.NotNull(instanceId);
        Assert.NotNull(bindingId);
        Assert.NotNull(_store.GetInstance(instanceId!));
    }

    [Fact]
    public void ResolveBindings_returns_non_empty_after_produce()
    {
        // effect-atom-map.md:213's own "not hardcoded to return empty" claim, proven for real: this
        // was never reachable in production before this task, because nothing had ever written a row.
        var owner = new OwnerScope(OwnerKind.Player, "2");
        var r = Produce(owner, out _, out _);
        Assert.True(r.IsOk, r.ToString());

        var resolved = _store.ResolveBindings(owner, new BindContext(RuntimeId.Lawn));

        Assert.NotEmpty(resolved.Bindings);
        Assert.NotNull(resolved.AtomsByBinding);
        Assert.NotEmpty(resolved.AtomsByBinding![resolved.Bindings[0].BindingId]);
    }

    [Fact]
    public void PowerJson_stays_null_after_produce()
    {
        var owner = new OwnerScope(OwnerKind.Player, "3");
        Produce(owner, out var instanceId, out _);

        var instance = _store.GetInstance(instanceId!)!;

        Assert.All(instance.Atoms, a => Assert.Null(a.PowerJson));
    }

    [Fact]
    public void Same_container_revision_seed_and_variant_reproduces_identically()
    {
        // The extended reproducibility law (definitions.md:246, T3.6's own addition of `variant` to
        // the tuple) — two independent produces of the same inputs land on content-identical instances.
        var a = Produce(new OwnerScope(OwnerKind.Player, "4"), out var idA, out _, seed: 99);
        var b = Produce(new OwnerScope(OwnerKind.Player, "5"), out var idB, out _, seed: 99);

        Assert.True(a.IsOk); Assert.True(b.IsOk);
        Assert.Equal(
            _store.GetInstance(idA!)!.ContentFingerprint(),
            _store.GetInstance(idB!)!.ContentFingerprint());
    }

    [Fact]
    public void Producing_for_an_equipped_item_slot_is_not_this_modules_test_surface()
    {
        // This fixture's own container is a species-passive, never an item bound to an equipped
        // slot — that scope belongs to module 5 (mods-absorption), which this task deliberately does
        // not touch. Asserted directly: the container this test suite exercises never carries
        // ContainerKind.Item.
        var container = _store.GetContainer("species-passive.hardy")!;
        Assert.NotEqual(ContainerKind.Item, container.Kind);
    }

    [Fact]
    public void A_rejected_compose_never_writes_anything()
    {
        var bad = new ContainerRow
        {
            ContainerId = "trait.bad", Kind = ContainerKind.Trait,
            Atoms = new[] { new ContainerAtomRow(1, "atom.nope.t1") },
        };
        var owner = new OwnerScope(OwnerKind.Player, "6");

        var r = _store.ProduceAndBind(bad, NoDomains, 1, PinTheta, Tuning, owner,
            slot: null, priority: 1, "test", out var instanceId, out var bindingId);

        Assert.False(r.IsOk);
        Assert.Null(instanceId);
        Assert.Null(bindingId);
        Assert.Empty(_store.ListBindings(owner));
    }

    [Fact]
    public void Partial_failure_never_leaves_an_orphaned_instance_with_no_binding()
    {
        // A malformed owner key would fail INSIDE SaveInstanceAndBind's own transaction — proving the
        // instance half never commits when the binding half is refused, the transactional guarantee
        // ProduceAndBind exists to provide over two separate SaveInstance()/Bind() calls.
        var badOwner = new OwnerScope(OwnerKind.Entity, "0xNotHex");

        var r = _store.ProduceAndBind(
            _store.GetContainer("species-passive.hardy")!, NoDomains, 1, PinTheta, Tuning,
            badOwner, slot: null, priority: 1, "test", out var instanceId, out var bindingId);

        Assert.Equal(AtomRejectionReason.BadOwnerKey, r.Reason);
        Assert.Null(instanceId);
        Assert.Null(bindingId);
        Assert.Equal(0, _store.CountOrphanInstances());
    }
}
