using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

public class BattleEngineTests
{
    static BattleActorSetup Actor(string key, string side, int level = 3,
        ElementTypeId? elem = null, ElementTypeId? elemSecondary = null) => new()
    {
        Key = key,
        Side = side,
        SpeciesId = "test-species",
        TypeId = 10_001,
        Level = level,
        ElementPrimary = elem,
        ElementSecondary = elemSecondary,
        MaxHp = BattleRuleset.BaseHp(level),
        Atk = BattleRuleset.BaseAtk(level),
        Defense = BattleRuleset.BaseDefense(level)
    };

    static BattleSetup Setup(int squadLevel = 3, int waveLevel = 3, int squadN = 3, int waveN = 3) => new()
    {
        WaveId = "test-wave",
        Squad = Enumerable.Range(0, squadN).Select(i => Actor($"squad:{i}", "squad", squadLevel)).ToList(),
        Wave = Enumerable.Range(0, waveN).Select(i => Actor($"wave:{i}", "wave", waveLevel)).ToList()
    };

    [Fact]
    public void Same_setup_and_seed_is_byte_identical()
    {
        var a = JsonSerializer.Serialize(BattleEngine.Resolve(Setup(), 42));
        var b = JsonSerializer.Serialize(BattleEngine.Resolve(Setup(), 42));
        Assert.Equal(a, b);
    }

    [Fact]
    public void ZombossPatternId_travelsFromSetupToTheReport_andIsAbsentWhenNull()
    {
        // species-build-todo.md T4.5: "pattern id on BattleSetup and on the report" -- proven by the
        // real BattleEngine, not just record-shape inspection. Absent-by-default (null) also asserted:
        // a non-Zomboss battle must not gain a field in its serialized report.
        var withPattern = Setup() with { ZombossPatternId = "force-pure" };
        var report = BattleEngine.Resolve(withPattern, 5);
        Assert.Equal("force-pure", report.ZombossPatternId);

        var withoutPattern = BattleEngine.Resolve(Setup(), 5);
        Assert.Null(withoutPattern.ZombossPatternId);
        Assert.DoesNotContain("ZombossPatternId", JsonSerializer.Serialize(withoutPattern));
    }

    [Fact]
    public void Different_seeds_diverge()
    {
        var a = JsonSerializer.Serialize(BattleEngine.Resolve(Setup(), 1));
        var b = JsonSerializer.Serialize(BattleEngine.Resolve(Setup(), 2));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Overleveled_squad_wins_underleveled_wave_loses()
    {
        var stomp = BattleEngine.Resolve(Setup(squadLevel: 10, waveLevel: 1), 7);
        Assert.Equal(BattleOutcome.Victory, stomp.Outcome);
        Assert.All(stomp.Actors.Where(a => a.Side == "wave"), a => Assert.False(a.Survived));

        var wipe = BattleEngine.Resolve(Setup(squadLevel: 1, waveLevel: 10), 7);
        Assert.Equal(BattleOutcome.Defeat, wipe.Outcome);
    }

    [Fact]
    public void Report_carries_version_stamps_and_lifecycle_events()
    {
        var report = BattleEngine.Resolve(Setup(), 5);
        Assert.Equal(BattleRuleset.EngineVersion, report.EngineVersion);
        Assert.Equal(SeededRng.RngAlgoVersion, report.RngAlgoVersion);
        Assert.Equal(BattleRuleset.RulesetVersion, report.RulesetVersion);
        Assert.Equal(5UL, report.Seed);
        Assert.Equal(6, report.Events.Count(e => e.Kind == BattleEventKinds.Spawn));
        // Every non-survivor has exactly one die event.
        var deaths = report.Events.Where(e => e.Kind == BattleEventKinds.Die).Select(e => e.ActorKey).ToList();
        Assert.Equal(deaths.Distinct().Count(), deaths.Count);
        Assert.Equal(report.Actors.Count(a => !a.Survived), deaths.Count);
    }

    [Fact]
    public void Direct_lethal_hits_record_the_attacker_key_on_each_death()
    {
        var report = BattleEngine.Resolve(Setup(squadLevel: 10, waveLevel: 1, squadN: 1, waveN: 1), 5);
        var death = Assert.Single(report.Events.Where(e => e.Kind == BattleEventKinds.Die));

        Assert.Equal("wave:0", death.ActorKey);
        Assert.Equal("squad:0", death.KillerActorKey);
        Assert.Equal(1, report.Actors.Single(a => a.Key == "squad:0").Kills);
    }

    [Fact]
    public void Element_advantage_swings_a_mirror_match()
    {
        // Fire squad vs ice wave (STR) should on average end faster / better than the mirror.
        var strongWins = 0;
        var weakWins = 0;
        for (ulong seed = 0; seed < 30; seed++)
        {
            var strong = BattleEngine.Resolve(new BattleSetup
            {
                WaveId = "elem",
                Squad = new[] { Actor("squad:0", "squad", 3, ElementTypeId.Fire) },
                Wave = new[] { Actor("wave:0", "wave", 3, ElementTypeId.Ice) }
            }, seed);
            if (strong.Outcome == BattleOutcome.Victory) strongWins++;

            var weak = BattleEngine.Resolve(new BattleSetup
            {
                WaveId = "elem",
                Squad = new[] { Actor("squad:0", "squad", 3, ElementTypeId.Ice) },
                Wave = new[] { Actor("wave:0", "wave", 3, ElementTypeId.Fire) }
            }, seed);
            if (weak.Outcome == BattleOutcome.Victory) weakWins++;
        }

        Assert.True(strongWins > weakWins, $"fire-vs-ice won {strongWins}, ice-vs-fire won {weakWins}");
    }

    // ShareMilli_mirrors_the_matchup_policy_constant deleted with the symbol itself
    // (battle-adoption): matchup lives ONLY in the SSOT resolver now — the combat-unification
    // ban test asserts the duplicate never returns.

    [Fact]
    public void Wave_catalog_builds_from_the_species_roster()
    {
        Assert.True(WaveCatalog.All.Count >= 4);
        foreach (var wave in WaveCatalog.All)
        {
            Assert.NotEmpty(wave.Enemies);
            foreach (var e in wave.Enemies)
            {
                Assert.True(CreatureSpeciesCatalog.IsKnown(e.SpeciesId));
                Assert.True(e.TypeId >= CreatureSpeciesCatalog.CreatureTypeIdFloor);
                Assert.True(e.MaxHp > 0 && e.Atk > 0);
            }
        }

        Assert.Throws<ArgumentException>(() => WaveCatalog.Get("no-such-wave"));
    }

    [Fact]
    public void Empty_sides_reject()
    {
        Assert.Throws<ArgumentException>(() => BattleEngine.Resolve(new BattleSetup
        {
            Squad = Array.Empty<BattleActorSetup>(),
            Wave = new[] { Actor("wave:0", "wave") }
        }, 1));
    }
}
