using System.Linq;
using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Expeditions;
using Xunit;

namespace FusionRpg.Core.Tests.Power;

/// <summary>
/// content-authoring (T2.3, spec-content-authoring.md §5). Values-unchanged, expedition inheritance,
/// and the serialization-safety proof the spec calls "the whole purpose of this wave."
/// </summary>
public class ContentIndexTests
{
    static BattleActorSetup Actor(string key, string side, int level = 5) => new()
    {
        Key = key, Side = side, SpeciesId = "test-species", TypeId = 10_001, Level = level,
        MaxHp = BattleRuleset.BaseHp(level), Atk = BattleRuleset.BaseAtk(level), Defense = BattleRuleset.BaseDefense(level)
    };

    // ---- values unchanged, unit renamed --------------------------------------------------------

    [Theory]
    [InlineData("rift-skirmish", 1)]
    [InlineData("rift-warband", 3)]
    [InlineData("rift-onslaught", 6)]
    [InlineData("rift-tyrant", 10)]
    public void Wave_ContentIndex_ValuesUnchanged(string waveId, int expected)
    {
        Assert.Equal(expected, WaveCatalog.Get(waveId).ContentIndex);
    }

    [Fact]
    public void EveryWaveEnemy_IndexAliasMatchesLevel()
    {
        foreach (var wave in WaveCatalog.All)
            foreach (var enemy in wave.Enemies)
                Assert.Equal(enemy.Level, enemy.Index);
    }

    // ---- expedition inheritance — load-bearing, previously undocumented -------------------------

    [Fact]
    public void NonBossBattle_InheritsTheChainWavesContentIndex()
    {
        // scout-30m's wave chain is exactly ["rift-skirmish"] (ContentIndex=1) — every one of its
        // non-boss battles must resolve that wave and carry its index (spec-content-authoring.md §2.1).
        var squad = new[] { Actor("squad:0", "squad") };
        var resolution = ExpeditionResolver.Resolve("scout-30m", squad, seed: 5, elapsedTicks: 999);

        var battle = Assert.Single(resolution.Battles);
        Assert.False(battle.Boss);
        Assert.Equal("rift-skirmish", battle.Setup.WaveId);
        Assert.All(battle.Setup.Wave, enemy => Assert.Equal(1, enemy.Index));
    }

    [Fact]
    public void BossWave_Warpath20h_ResolvesRiftTyrantAtIndex10()
    {
        var squad = new[] { Actor("squad:0", "squad"), Actor("squad:1", "squad") };
        var resolution = ExpeditionResolver.Resolve("warpath-20h", squad, seed: 12, elapsedTicks: 999);

        var boss = Assert.Single(resolution.Battles, b => b.Boss);
        Assert.Equal("rift-tyrant", boss.Setup.WaveId);
        Assert.Equal(10, WaveCatalog.Get(boss.Setup.WaveId).ContentIndex);
        Assert.All(boss.Setup.Wave, enemy => Assert.Equal(10, enemy.Index));
    }

    // ---- serialization safety — F7, decisions.md:42 --------------------------------------------

    [Fact]
    public void BattleActorSetup_SerializesAsLevelOnly_IndexNeverAppears()
    {
        // This is the regression T2.3 actually hit while being built: a first draft's Index alias had
        // no [JsonIgnore] and System.Text.Json serialized it anyway, moving
        // ExpeditionResolverTests.Tier_goldens_are_locked's hash. Asserted directly, not just implied
        // by "the golden test still passes" — so a future edit that reintroduces the leak fails here
        // with a message that says why, not as a cryptic hash diff three files away.
        var actor = Actor("wave:0", "wave", level: 7);
        var json = JsonSerializer.Serialize(actor);

        Assert.Contains("\"Level\":7", json);
        Assert.DoesNotContain("Index", json);
    }

    [Fact]
    public void BattleSetup_HashesByteIdentical_ClampedVsExactElapsed()
    {
        // Same proof shape WaveCDRegressionLockTests already uses for this exact tier — re-asserted
        // here because T2.3's rename is the change that could have broken it.
        var squad = new[] { Actor("squad:0", "squad") };
        var exact = ExpeditionResolver.Resolve("scout-30m", squad, seed: 5, elapsedTicks: 6);
        var over = ExpeditionResolver.Resolve("scout-30m", squad, seed: 5, elapsedTicks: 999);

        Assert.Equal(JsonSerializer.Serialize(exact), JsonSerializer.Serialize(over));
    }
}
