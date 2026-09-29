using FusionRpg.Core.Achievements;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

// T5: title slots, ownership, six-resource rule, withdraw, worn derivation.
[Trait("VerificationId", "data.titles")]
public class ActorTitleTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ActorTitleTests()
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

    void Atom(string id, string family, int tier, string channel, int amount, string kind = "stat.modify")
    {
        var r = _store.UpsertAtom(new AtomRow
        {
            AtomId = id, KindId = kind, FamilyId = family,
            Tier = tier, Name = id,
            ParamsJson = "{\"channel\":\"" + channel + "\",\"op\":\"flat\",\"amount\":" + amount + "}",
        });
        Assert.True(r.IsOk, $"{id}: {r}");
    }

    void Seed()
    {
        Atom("atom.valor.t1", "atom.valor", 1, "maxHp", 10);
        Atom("atom.valor.t2", "atom.valor", 2, "maxHp", 20);
        Atom("atom.vigorhp.t1", "atom.vigorhp", 1, "resource.max.hp", 5, "stat.derived");
        foreach (var r in new[] { "hp", "stamina", "hunger", "spirit", "qi", "poise" })
            Atom($"atom.vigor-{r}.t1", $"atom.vigor-{r}", 1, $"resource.max.{r}", 5, "stat.derived");
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "actor-title.valorous", Kind = ContainerKind.ActorTitle,
            Atoms = new[] { new ContainerAtomRow(1, "atom.valor.t1") },
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "actor-title.valorous-2", Kind = ContainerKind.ActorTitle,
            Atoms = new[] { new ContainerAtomRow(1, "atom.valor.t2") },
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "actor-title.half-vigor", Kind = ContainerKind.ActorTitle,
            Atoms = new[] { new ContainerAtomRow(1, "atom.vigor-hp.t1") },
        }).IsOk);
        var full = new List<ContainerAtomRow>();
        var seq = 1;
        foreach (var r in new[] { "hp", "stamina", "hunger", "spirit", "qi", "poise" })
            full.Add(new ContainerAtomRow(seq++, $"atom.vigor-{r}.t1"));
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "actor-title.full-vigor", Kind = ContainerKind.ActorTitle,
            Atoms = full,
        }).IsOk);
    }

    static IReadOnlyList<string> NoDomains(string domain) => Array.Empty<string>();

    string GrantToPlayer(string containerId, long player, long seed)
    {
        var r = _store.ProduceAndBind(
            _store.GetContainer(containerId)!, NoDomains, seed, PinTheta, Tuning,
            new OwnerScope(OwnerKind.Player, player.ToString()),
            slot: null, priority: 1, source: "test",
            out var instanceId, out _);
        Assert.True(r.IsOk, r.ToString());
        return instanceId!;
    }

    [Fact]
    public void Equip_wears_and_worn_derives_highest_tier()
    {
        var a = GrantToPlayer("actor-title.valorous", 1, 1);
        var b = GrantToPlayer("actor-title.valorous-2", 1, 2);
        Assert.True(_store.EquipActorTitle(1, "alpha", 1, a).IsOk);
        Assert.True(_store.EquipActorTitle(1, "alpha", 2, b).IsOk);
        Assert.Equal("actor-title.valorous-2", _store.GetWornTitle("alpha"));
    }

    [Fact]
    public void Slot_outside_1_to_3_refuses()
    {
        var a = GrantToPlayer("actor-title.valorous", 2, 3);
        Assert.False(_store.EquipActorTitle(2, "beta", 0, a).IsOk);
        Assert.False(_store.EquipActorTitle(2, "beta", 4, a).IsOk);
    }

    [Fact]
    public void Foreign_instance_refuses()
    {
        var a = GrantToPlayer("actor-title.valorous", 6, 4);
        var r = _store.EquipActorTitle(7, "delta", 1, a);
        Assert.False(r.IsOk);
        Assert.Contains(a, r.ToString());
    }

    [Fact]
    public void Partial_resource_coverage_refuses_naming_container()
    {
        var a = GrantToPlayer("actor-title.half-vigor", 8, 5);
        var r = _store.EquipActorTitle(8, "epsilon", 1, a);
        Assert.False(r.IsOk);
        Assert.Contains("actor-title.half-vigor", r.ToString());
        Assert.Contains("stamina", r.ToString());
    }

    [Fact]
    public void Full_resource_coverage_equips()
    {
        var a = GrantToPlayer("actor-title.full-vigor", 9, 6);
        var r = _store.EquipActorTitle(9, "zeta", 1, a);
        Assert.True(r.IsOk, r.ToString());
    }

    [Fact]
    public void Unequip_clears_slot_and_worn()
    {
        var a = GrantToPlayer("actor-title.valorous", 10, 7);
        Assert.True(_store.EquipActorTitle(10, "eta", 1, a).IsOk);
        Assert.Equal("actor-title.valorous", _store.GetWornTitle("eta"));
        Assert.True(_store.UnequipActorTitle("eta", 1).IsOk);
        Assert.True(_store.UnequipActorTitle("eta", 1).IsOk);
        Assert.Null(_store.GetWornTitle("eta"));
    }
}
