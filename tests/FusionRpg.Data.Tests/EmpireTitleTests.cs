using FusionRpg.Core.Achievements;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

// T4: Hall 3-slot equip, P2 upkeep rule, stacking/exclusion, soft caps, withdraw.
[Trait("VerificationId", "data.titles")]
public class EmpireTitleTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public EmpireTitleTests()
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

    static AchievementTitlesTuning TitleTuning() => new(
        1, 3, new[] { 10 }, new Dictionary<string, long>(),
        new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["atom.might"] = 200, ["atom.tithe"] = 0,
        },
        new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["atom.might"] = 0, ["atom.tithe"] = 100,
        },
        "additive-in-family-one-per-group",
        new Dictionary<string, long>(), 500, 50,
        new Dictionary<string, long>(), 312, "highestTier", 1000, 1000);

    void Seed()
    {
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = "atom.might.t1", KindId = "stat.modify", FamilyId = "atom.might",
            Tier = 1, Name = "Might",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
        }).IsOk);
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = "atom.might.t2", KindId = "stat.modify", FamilyId = "atom.might",
            Tier = 2, Name = "Might II",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":20}",
        }).IsOk);
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = "atom.tithe.t1", KindId = "stat.modify", FamilyId = "atom.tithe",
            Tier = 1, Name = "Tithe",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":5}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "empire-title.warlord", Kind = ContainerKind.EmpireTitle,
            Atoms = new[]
            {
                new ContainerAtomRow(1, "atom.might.t1"),
                new ContainerAtomRow(2, "atom.tithe.t1"),
            },
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "empire-title.warlord-2", Kind = ContainerKind.EmpireTitle,
            Atoms = new[]
            {
                new ContainerAtomRow(1, "atom.might.t2"),
                new ContainerAtomRow(2, "atom.tithe.t1"),
            },
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "empire-title.miser", Kind = ContainerKind.EmpireTitle,
            Atoms = new[] { new ContainerAtomRow(1, "atom.might.t1") },
        }).IsOk);
    }

    static IReadOnlyList<string> NoDomains(string domain) => Array.Empty<string>();

    string Grant(string containerId, long player, long seed)
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
    public void Equip_then_intent_squashed_once()
    {
        var inst = Grant("empire-title.warlord", 1, 5);
        var r = _store.EquipHallTitle(1, 1, inst, TitleTuning());
        Assert.True(r.IsOk, r.ToString());
        var intent = _store.GetHallIntent(1, TitleTuning());
        Assert.Equal(1000 * 200 / 1200, intent.YieldShareMilli);
        Assert.Equal(1000 * 100 / 1100, intent.UpkeepShareMilli);
    }

    [Fact]
    public void Slot_outside_1_to_3_refuses()
    {
        var inst = Grant("empire-title.warlord", 2, 6);
        Assert.False(_store.EquipHallTitle(2, 0, inst, TitleTuning()).IsOk);
        Assert.False(_store.EquipHallTitle(2, 4, inst, TitleTuning()).IsOk);
    }

    [Fact]
    public void Yield_without_upkeep_refuses_naming_container()
    {
        var inst = Grant("empire-title.miser", 3, 7);
        var r = _store.EquipHallTitle(3, 1, inst, TitleTuning());
        Assert.False(r.IsOk);
        Assert.Contains("empire-title.miser", r.ToString());
    }

    [Fact]
    public void Same_family_variant_keeps_max_tier_once()
    {
        var a = Grant("empire-title.warlord", 4, 8);
        var b = Grant("empire-title.warlord-2", 4, 9);
        Assert.True(_store.EquipHallTitle(4, 1, a, TitleTuning()).IsOk);
        Assert.True(_store.EquipHallTitle(4, 2, b, TitleTuning()).IsOk);
        var intent = _store.GetHallIntent(4, TitleTuning());
        // One might-share (200) not two: exclusion keeps a single winner.
        Assert.Equal(1000 * 200 / 1200, intent.YieldShareMilli);
    }

    [Fact]
    public void Unequip_is_idempotent_and_clears_intent()
    {
        var inst = Grant("empire-title.warlord", 5, 10);
        Assert.True(_store.EquipHallTitle(5, 2, inst, TitleTuning()).IsOk);
        Assert.True(_store.UnequipHallTitle(5, 2).IsOk);
        Assert.True(_store.UnequipHallTitle(5, 2).IsOk);
        var intent = _store.GetHallIntent(5, TitleTuning());
        Assert.Equal(0, intent.YieldShareMilli);
        Assert.Equal(0, intent.UpkeepShareMilli);
    }

    [Fact]
    public void Foreign_instance_refuses()
    {
        var inst = Grant("empire-title.warlord", 6, 11);
        var r = _store.EquipHallTitle(7, 1, inst, TitleTuning());
        Assert.False(r.IsOk);
        Assert.Contains(inst, r.ToString());
    }
}
