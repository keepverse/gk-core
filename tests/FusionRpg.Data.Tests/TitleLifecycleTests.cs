using FusionRpg.Core.Achievements;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

// T6: windows/expiry, clock refusals, honors, expire idempotence, curses + atomic
// ritual, tombstones, Hall overflow. Integer turns only — no float math exists here.
[Trait("VerificationId", "data.titles")]
public class TitleLifecycleTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public TitleLifecycleTests()
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
        new Dictionary<string, long>(StringComparer.Ordinal) { ["atom.might"] = 200 },
        new Dictionary<string, long>(StringComparer.Ordinal) { ["atom.tithe"] = 100 },
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
    }

    static IReadOnlyList<string> NoDomains(string domain) => Array.Empty<string>();

    string GrantWarlord(long player, long seed, string? slot = null,
        OwnerKind owner = OwnerKind.Player, string key = "")
    {
        var r = _store.ProduceAndBind(
            _store.GetContainer("empire-title.warlord")!, NoDomains, seed, PinTheta, Tuning,
            new OwnerScope(owner, owner == OwnerKind.Player ? player.ToString() : key),
            slot: slot, priority: 1, source: "test",
            out var instanceId, out _);
        Assert.True(r.IsOk, r.ToString());
        return instanceId!;
    }

    [Fact]
    public void Window_expiry_reads_latest_anchor()
    {
        var (ok, reason, _) = _store.RecordTitleWindow(
            1, "empire", "1", "empire-title.warlord", "world-turns", null, 10, 20, "t");
        Assert.True(ok, reason);
        Assert.Equal((false, 30), _store.EvaluateTitleExpiry(1, "empire", "1", "empire-title.warlord", 29));
        Assert.Equal((true, 30), _store.EvaluateTitleExpiry(1, "empire", "1", "empire-title.warlord", 30));
        // Re-anchor moves the window; mid-window reads converge on anchor math.
        _store.RecordTitleWindow(1, "empire", "1", "empire-title.warlord", "world-turns", null, 25, 20, "t");
        Assert.Equal((false, 45), _store.EvaluateTitleExpiry(1, "empire", "1", "empire-title.warlord", 40));
    }

    [Fact]
    public void Clock_refusals_name_cause()
    {
        Assert.False(_store.RecordTitleWindow(
            1, "empire", "1", "t", "wall-clock", null, 1, 5, "t").Ok);
        Assert.False(_store.RecordTitleWindow(
            1, "unique-actor", "a", "t", "world-turns", null, 1, 5, "t").Ok);
        Assert.False(_store.RecordTitleWindow(
            1, "unique-actor", "a", "t", "battle-ticks", null, 1, 5, "t").Ok);
        Assert.False(_store.RecordTitleWindow(
            1, "void", "a", "t", "world-turns", null, 1, 5, "t").Ok);
        Assert.False(_store.RecordTitleWindow(
            1, "empire", "1", "t", "world-turns", null, 1, 0, "t").Ok);
        var named = _store.RecordTitleWindow(
            1, "unique-actor", "a", "t", "battle-ticks", "round", 1, 5, "t");
        Assert.True(named.Ok, named.Reason);
    }

    [Fact]
    public void Honor_never_expires_and_expire_is_idempotent()
    {
        _store.RecordHonor(2, "empire", "2", "empire-title.warlord", "fact-9", "t");
        Assert.Equal((false, 0), _store.EvaluateTitleExpiry(2, "empire", "2", "empire-title.warlord", 99999));
        var a = _store.RecordTitleExpire(2, "empire", "2", "empire-title.warlord", 10, 30, "t");
        var b = _store.RecordTitleExpire(2, "empire", "2", "empire-title.warlord", 10, 30, "t");
        Assert.Equal(a, b);
        var rows = _store.ListTitleLifecycle(2, "empire", "2", "empire-title.warlord");
        Assert.Contains(rows, r => r.Kind == "honor");
        Assert.Single(rows, r => r.Kind == "expire");
    }

    [Fact]
    public void Curse_taxonomy_closed_and_hidden()
    {
        var bad = _store.RecordCurse(3, "unique-actor", "c", "curse.fell",
            "stole.cake", "fact-1", "t");
        Assert.False(bad.Ok);
        Assert.Contains("stole.cake", bad.Reason);
        var good = _store.RecordCurse(3, "unique-actor", "c", "curse.fell",
            "kill.innocent", "fact-2", "t");
        Assert.True(good.Ok, good.Reason);
        var rows = _store.ListTitleLifecycle(3, "unique-actor", "c", "curse.fell");
        var curse = Assert.Single(rows, r => r.Kind == "curse");
        Assert.Contains("hidden", curse.EvidenceJson);
        Assert.Contains("kill.innocent", curse.EvidenceJson);
    }

    void Fund(long player)
    {
        Assert.True(_store.AwardSouls(player, 1000, "test", $"fund-{player}").Inserted);
        _store.GrantMaterials(player,
            new List<(string, long)> { ("essence.fire", 100) });
    }

    string BindCurse(long player, string actor)
    {
        var inst = GrantWarlord(player, 21);
        var bound = _store.Bind(new BindingRow
        {
            OwnerKind = OwnerKind.UniqueActor, OwnerKey = actor,
            InstanceId = inst, Slot = "curse", Priority = 1, Source = "test",
        });
        Assert.True(bound.IsOk, bound.ToString());
        var rec = _store.RecordCurse(player, "unique-actor", actor, "curse.fell",
            "kill.innocent", "fact-3", "t");
        Assert.True(rec.Ok, rec.Reason);
        return inst;
    }

    [Fact]
    public void Ritual_insufficient_writes_nothing()
    {
        BindCurse(4, "doomed");
        var r = _store.LiftCurse(4, "doomed", "curse.fell", "fire", 500, 50, "corr-poor");
        Assert.False(r.Ok);
        Assert.Equal(0, _store.GetSoulBalance(4).Balance);
        Assert.Empty(_store.ListTitleLifecycle(4, "unique-actor", "doomed", "curse.fell").Where(x => x.Kind == "lift"));
    }

    [Fact]
    public void Ritual_atomic_then_replays_without_respend()
    {
        Fund(5);
        BindCurse(5, "cursed");
        var first = _store.LiftCurse(5, "cursed", "curse.fell", "fire", 500, 50, "corr-1");
        Assert.True(first.Ok, first.Reason);
        var soulsAfter = _store.GetSoulBalance(5).Balance;
        Assert.Equal(500, soulsAfter);
        Assert.Equal(50, _store.GetMaterialQty(5, "essence.fire"));
        var rows = _store.ListTitleLifecycle(5, "unique-actor", "cursed", "curse.fell");
        Assert.Single(rows, r => r.Kind == "lift");

        var replay = _store.LiftCurse(5, "cursed", "curse.fell", "fire", 500, 50, "corr-1");
        Assert.True(replay.Ok);
        Assert.Equal("replay", replay.Reason);
        Assert.Equal(soulsAfter, _store.GetSoulBalance(5).Balance);
        Assert.Equal(50, _store.GetMaterialQty(5, "essence.fire"));
    }

    [Fact]
    public void Tombstoned_curse_refuses_without_spending()
    {
        Fund(6);
        var inst = GrantWarlord(6, 22);
        var bound = _store.Bind(new BindingRow
        {
            OwnerKind = OwnerKind.UniqueActor, OwnerKey = "fallen",
            InstanceId = inst, Slot = "curse", Priority = 1, Source = "test",
        });
        Assert.True(bound.IsOk, bound.ToString());
        var rec = _store.RecordCurse(6, "unique-actor", "fallen", "curse.fell",
            "kill.innocent", "fact-4", "t");
        Assert.True(rec.Ok, rec.Reason);
        // Death withdraws the binding; the ledger row stays (tombstone).
        foreach (var b in _store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "fallen")))
            _store.Withdraw(b.BindingId);
        var r = _store.LiftCurse(6, "fallen", "curse.fell", "fire", 500, 50, "corr-tomb");
        Assert.False(r.Ok);
        Assert.Equal("curse.tombstoned", r.Reason);
        Assert.Equal(1000, _store.GetSoulBalance(6).Balance);
        Assert.Equal(100, _store.GetMaterialQty(6, "essence.fire"));
    }

    [Fact]
    public void Overflow_victim_is_oldest_slot_then_null_when_free()
    {
        Fund(7);
        var t = TitleTuning();
        var i1 = GrantWarlord(7, 31);
        var i2 = GrantWarlord(7, 32);
        Assert.True(_store.EquipHallTitle(7, 1, i1, t, "2026-01-01T00:00:00Z").IsOk);
        Assert.True(_store.EquipHallTitle(7, 2, i2, t, "2026-01-02T00:00:00Z").IsOk);
        Assert.Null(_store.HallOverflowVictim(7));
        var i3 = GrantWarlord(7, 33);
        Assert.True(_store.EquipHallTitle(7, 3, i3, t, "2026-01-03T00:00:00Z").IsOk);
        Assert.Equal("hall-1", _store.HallOverflowVictim(7));
    }
}
