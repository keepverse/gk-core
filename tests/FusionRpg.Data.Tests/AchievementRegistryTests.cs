using FusionRpg.Core.Achievements;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

// T1: registry grammars, closed vocabs, per-row isolation, exactly-once unlocks.
// Asserts contract + closed enums only — never row counts or generated strings.
public class AchievementRegistryTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public AchievementRegistryTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static AchievementDefinition Def(string id, string scope = "empire") => new(
        id, 1, scope, "counter-reach", "{\"need\":10}", "permanent", "never", "revealed",
        0, "bundle.first-blood", "souls", "achievement.first-blood", null, null);

    [Theory]
    [InlineData("achievement.first-blood", true)]
    [InlineData("achievement.a", true)]
    [InlineData("Achievement.first-blood", false)]
    [InlineData("achievement.", false)]
    [InlineData("achievement.-lead", false)]
    [InlineData("achievement.trail-", false)]
    [InlineData("achievement.a--b", false)]
    [InlineData("trophy.first-blood", false)]
    public void Id_grammar_accepts_and_rejects(string id, bool expected) =>
        Assert.Equal(expected, AchievementId.IsValid(id));

    [Fact]
    public void Long_id_rejects()
    {
        var id = "achievement." + new string('a', 65);
        Assert.False(AchievementId.IsValid(id));
    }

    [Theory]
    [InlineData("void", "counter-reach", "UnknownScope")]
    [InlineData("empire", "smell", "UnknownTrigger")]
    public void Unknown_vocab_rejects_naming_row(string scope, string trigger, string expected)
    {
        var def = Def("achievement.bad-row", scope) with { Trigger = trigger };
        var ok = _store.TryRegisterAchievementDefinition(def, "{}", DateTime.UtcNow.ToString("o"), out var cause);
        Assert.False(ok);
        Assert.Contains(def.DefId, cause);
        Assert.Contains(expected, cause);
    }

    [Fact]
    public void Malformed_bundle_ref_rejects()
    {
        var def = Def("achievement.bad-bundle") with { BundleRef = "first-blood" };
        var ok = _store.TryRegisterAchievementDefinition(def, "{}", DateTime.UtcNow.ToString("o"), out var cause);
        Assert.False(ok);
        Assert.Contains("MissingBundleRef", cause);
    }

    [Fact]
    public void Loam_sink_in_world_bundle_rejects()
    {
        var def = Def("achievement.loam-rush") with
        {
            ReearnScope = "world", SinkStock = "loam", SinkReason = "test",
        };
        var ok = _store.TryRegisterAchievementDefinition(def, "{}", DateTime.UtcNow.ToString("o"), out var cause);
        Assert.False(ok);
        Assert.Contains("LoamInWorldBundle", cause);
    }

    [Fact]
    public void Counter_reach_without_need_rejects()
    {
        var def = Def("achievement.no-need") with { TriggerPayloadJson = "{}" };
        var ok = _store.TryRegisterAchievementDefinition(def, "{}", DateTime.UtcNow.ToString("o"), out var cause);
        Assert.False(ok);
        Assert.Contains("MissingNeed", cause);
    }

    [Fact]
    public void Faucet_without_sink_rejects()
    {
        var def = Def("achievement.no-sink") with { SinkStock = "", SinkReason = "" };
        var ok = _store.TryRegisterAchievementDefinition(def, "{}", DateTime.UtcNow.ToString("o"), out var cause);
        Assert.False(ok);
        Assert.Contains("MissingSink", cause);
    }

    [Fact]
    public void Duplicate_id_across_scopes_rejects()
    {
        Assert.True(_store.TryRegisterAchievementDefinition(
            Def("achievement.shared"), "{}", "t", out _));
        var ok = _store.TryRegisterAchievementDefinition(
            Def("achievement.shared", "unique-actor"), "{}", "t", out var cause);
        Assert.False(ok);
        Assert.Contains("DuplicateIdAcrossScopes", cause);
    }

    [Fact]
    public void Bad_row_does_not_fail_good_rows()
    {
        Assert.True(_store.TryRegisterAchievementDefinition(Def("achievement.good"), "{}", "t", out _));
        Assert.False(_store.TryRegisterAchievementDefinition(Def("achievement.bad", "void"), "{}", "t", out _));
        var (id, replayed) = _store.TryAppendAchievementUnlock(
            1, "empire", "1", "achievement.good", 1, "fact-1", "grant", "t");
        Assert.True(id > 0);
        Assert.False(replayed);
    }

    [Fact]
    public void Set_graph_cycle_rejects_naming_sets()
    {
        var defs = new[]
        {
            Def("achievement.a") with { MemberSetId = "s1", MetaForSetId = "s2" },
            Def("achievement.b") with { MemberSetId = "s2", MetaForSetId = "s1" },
        };
        var ex = Assert.Throws<RegistryLoadException>(
            () => AchievementRegistryValidator.ValidateSetGraph(defs));
        Assert.Contains("CycleAt", ex.Message);
    }

    [Fact]
    public void Double_append_returns_canonical_receipt()
    {
        var first = _store.TryAppendAchievementUnlock(
            1, "empire", "1", "achievement.good", 1, "fact-1", "grant", "t");
        var second = _store.TryAppendAchievementUnlock(
            1, "empire", "1", "achievement.good", 1, "fact-1", "grant", "t");
        Assert.Equal(first.UnlockId, second.UnlockId);
        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
    }

    [Fact]
    public void Unknown_unlock_kind_throws_naming_row()
    {
        var ex = Assert.Throws<RegistryLoadException>(() =>
            _store.TryAppendAchievementUnlock(1, "empire", "1", "achievement.good", 1, "f", "beam", "t"));
        Assert.Contains("achievement.good", ex.Message);
    }
}
