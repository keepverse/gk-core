using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using Xunit;

namespace FusionRpg.Server.Tests;

// T5: equipped title instances reach the Hub through the EXISTING equip path
// (EquippedBoundAtoms.DerivedFromStore → EquipAtomSource → AtomDerivedSubsystem),
// with role-tagged equip:title-{slot} SourceIds. No parallel composer exists;
// this test would fail if titles ever bypassed the gate.
public class TitleHubReachTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public TitleHubReachTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        Seed();
    }

    public void Dispose() => _testStore.Dispose();

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);
    const int PinTheta = 20;

    void Seed()
    {
        var ar = _store.UpsertAtom(new AtomRow
        {
            AtomId = "atom.oath.t1", KindId = "stat.derived", FamilyId = "atom.oath",
            Tier = 1, Name = "Oath",
            ParamsJson = "{\"channel\":\"progression.bonus.maxHp\",\"op\":\"flat\",\"amount\":30}",
        });
        Assert.True(ar.IsOk, ar.ToString());
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "actor-title.oathkeeper", Kind = ContainerKind.ActorTitle,
            Atoms = new[] { new ContainerAtomRow(1, "atom.oath.t1") },
        }).IsOk);
    }

    static IReadOnlyList<string> NoDomains(string domain) => Array.Empty<string>();

    [Fact]
    public void Equipped_title_reaches_hub_with_role_tagged_source()
    {
        var produced = _store.ProduceAndBind(
            _store.GetContainer("actor-title.oathkeeper")!, NoDomains, 3, PinTheta, Tuning,
            new OwnerScope(OwnerKind.Player, "11"),
            slot: null, priority: 1, source: "test",
            out var instanceId, out _);
        Assert.True(produced.IsOk, produced.ToString());
        var equipped = _store.EquipActorTitle(11, "theta", 1, instanceId!);
        Assert.True(equipped.IsOk, equipped.ToString());

        var bound = EquippedBoundAtoms.DerivedFromStore(_store, "theta");

        var hit = Assert.Single(bound, b => b.Channel == "progression.bonus.maxHp");
        Assert.StartsWith("equip:title-1:", hit.SourceId);
        Assert.Equal("actor-title.oathkeeper", _store.GetWornTitle("theta"));
    }

    [Fact]
    public void Grant_binding_alone_never_reaches_actor_hub()
    {
        // Inventory/equip split: the player-owned grant binding is invisible to the
        // actor projection — each title composes exactly once, only when equipped.
        var produced = _store.ProduceAndBind(
            _store.GetContainer("actor-title.oathkeeper")!, NoDomains, 4, PinTheta, Tuning,
            new OwnerScope(OwnerKind.Player, "12"),
            slot: null, priority: 1, source: "test",
            out _, out _);
        Assert.True(produced.IsOk, produced.ToString());
        Assert.Empty(EquippedBoundAtoms.DerivedFromStore(_store, "kappa"));
    }

    [Fact]
    public void Unequipped_title_does_not_reach_hub()
    {
        var bound = EquippedBoundAtoms.DerivedFromStore(_store, "iota");
        Assert.Empty(bound);
    }
}
