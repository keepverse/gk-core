using FusionRpg.Core.Achievements;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

// T2: Cold evaluation — lookup-first receipts, criterion gating, race-stable keys,
// reearn worlds, re-grant kinds, explicit refusal of unbuilt verbs.
public class AchievementEvalTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public AchievementEvalTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static AchievementDefinition Def(
        string id, string trigger = "counter-reach", string payload = "{\"need\":10}",
        string reearn = "never", string scope = "empire") => new(
        id, 1, scope, trigger, payload, "permanent", reearn, "revealed",
        0, "bundle.first-blood", "souls", "achievement.first-blood", null, null);

    static AchievementEvidence Ev(
        long count = 10, long maxFact = 50, long? fact = null,
        string scopeKey = "1", string? world = null, long turn = 7) => new(
        1, scopeKey, fact, count, maxFact, world, null, turn, "t");

    [Fact]
    public void Criterion_unmet_returns_null_and_writes_nothing()
    {
        var r = _store.EvaluateAchievement(Def("achievement.counter"), Ev(count: 9));
        Assert.Null(r);
        Assert.Null(_store.GetAchievementUnlockByKey(1, "empire", "1", "achievement.counter", 1, "need=10:w="));
    }

    [Fact]
    public void Criterion_met_grants_then_replays_canonical_receipt()
    {
        var def = Def("achievement.counter");
        var first = _store.EvaluateAchievement(def, Ev(count: 10, maxFact: 50));
        var second = _store.EvaluateAchievement(def, Ev(count: 14, maxFact: 61, turn: 9));
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Value.UnlockId, second.Value.UnlockId);
        Assert.False(first.Value.Replayed);
        Assert.True(second.Value.Replayed);
    }

    [Fact]
    public void Out_of_order_subsets_converge()
    {
        var def = Def("achievement.converge");
        var a = _store.EvaluateAchievement(def, Ev(count: 12, maxFact: 70));
        var b = _store.EvaluateAchievement(def, Ev(count: 10, maxFact: 55));
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a.Value.UnlockId, b.Value.UnlockId);
    }

    [Fact]
    public void Event_seen_requires_fact_and_dedupes_on_it()
    {
        var def = Def("achievement.seen", "event-seen", "{}");
        Assert.Null(_store.EvaluateAchievement(def, Ev(fact: null)));
        var a = _store.EvaluateAchievement(def, Ev(fact: 42));
        var b = _store.EvaluateAchievement(def, Ev(fact: 42));
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a.Value.UnlockId, b.Value.UnlockId);
        Assert.True(b.Value.Replayed);
    }

    [Fact]
    public void Unbuilt_verb_refuses_with_cause()
    {
        var ex = Assert.Throws<RegistryLoadException>(() =>
            _store.EvaluateAchievement(Def("achievement.thresh", "threshold-cross", "{}"), Ev()));
        Assert.Contains("UnknownTriggerAtEval", ex.Message);
        Assert.Contains("achievement.thresh", ex.Message);
    }

    [Fact]
    public void Reearn_world_grants_per_world_as_regrant()
    {
        var def = Def("achievement.seasonal", "counter-reach", "{\"need\":5}", "world");
        var w1 = _store.EvaluateAchievement(def, Ev(count: 5, world: "world-a"));
        var w1again = _store.EvaluateAchievement(def, Ev(count: 9, world: "world-a"));
        var w2 = _store.EvaluateAchievement(def, Ev(count: 5, world: "world-b"));
        Assert.NotNull(w1);
        Assert.NotNull(w1again);
        Assert.NotNull(w2);
        Assert.Equal(w1.Value.UnlockId, w1again.Value.UnlockId);
        Assert.NotEqual(w1.Value.UnlockId, w2.Value.UnlockId);
        Assert.Equal("grant", _store.GetAchievementUnlockKind(w1.Value.UnlockId));
        Assert.Equal("re-grant", _store.GetAchievementUnlockKind(w2.Value.UnlockId));
    }

    [Fact]
    public void Actor_scope_requires_instance_key()
    {
        var def = Def("achievement.actor-earn", "event-seen", "{}", "never", "unique-actor");
        var ex = Assert.Throws<RegistryLoadException>(() =>
            _store.EvaluateAchievement(def, Ev(fact: 7, scopeKey: "")));
        Assert.Contains("MissingScopeKey", ex.Message);
    }
}
