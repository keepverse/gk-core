using FusionRpg.Core.Creatures;
using FusionRpg.Core.Match.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Match.Ai;

/// <summary>zomboss-deploy-ai T3.2 — the roster/pool acceptance line: deterministic and reproducible
/// from the level's own data (wave number + catalog), never hand-authored per level.</summary>
public class ZombossDeployRosterTests
{
    static CreatureSpeciesDef Species(string id, CreatureRarity rarity, CreatureAcquisition acquisition) => new()
    {
        SpeciesId = id,
        Name = id,
        Side = "zombie",
        BaseRarity = rarity,
        Acquisition = acquisition,
        DeployMode = CreatureDeployMode.PlantAvatar,
    };

    static readonly ZombossWaveRarityCeiling[] Ceilings =
    {
        new(1, CreatureRarity.Sprout),
        new(5, CreatureRarity.Cultivated),
        new(10, CreatureRarity.Almanac),
    };

    [Fact]
    public void RarityCeilingForWave_returns_the_lowest_rung_before_the_first_threshold()
    {
        Assert.Equal(CreatureRarity.Sprout, ZombossDeployRoster.RarityCeilingForWave(0, Ceilings));
    }

    [Fact]
    public void RarityCeilingForWave_advances_exactly_at_each_threshold()
    {
        Assert.Equal(CreatureRarity.Sprout, ZombossDeployRoster.RarityCeilingForWave(4, Ceilings));
        Assert.Equal(CreatureRarity.Cultivated, ZombossDeployRoster.RarityCeilingForWave(5, Ceilings));
        Assert.Equal(CreatureRarity.Cultivated, ZombossDeployRoster.RarityCeilingForWave(9, Ceilings));
        Assert.Equal(CreatureRarity.Almanac, ZombossDeployRoster.RarityCeilingForWave(10, Ceilings));
        Assert.Equal(CreatureRarity.Almanac, ZombossDeployRoster.RarityCeilingForWave(999, Ceilings));
    }

    [Fact]
    public void AvailableSpeciesFor_excludes_species_above_the_waves_own_rarity_ceiling()
    {
        var catalog = new[]
        {
            Species("low", CreatureRarity.Chaff, CreatureAcquisition.Summonable),
            Species("mid", CreatureRarity.Cultivated, CreatureAcquisition.Summonable),
            Species("high", CreatureRarity.Almanac, CreatureAcquisition.Summonable),
        };

        var result = ZombossDeployRoster.AvailableSpeciesFor(waveNumber: 3, catalog, Ceilings);

        Assert.Equal(new[] { "low" }, result);
    }

    [Fact]
    public void AvailableSpeciesFor_excludes_non_summonable_species_even_under_the_ceiling()
    {
        var catalog = new[]
        {
            Species("summonable-one", CreatureRarity.Chaff, CreatureAcquisition.Summonable),
            Species("capture-only-one", CreatureRarity.Chaff, CreatureAcquisition.CaptureOnly),
            Species("event-only-one", CreatureRarity.Chaff, CreatureAcquisition.EventOnly),
        };

        var result = ZombossDeployRoster.AvailableSpeciesFor(waveNumber: 3, catalog, Ceilings);

        Assert.Equal(new[] { "summonable-one" }, result);
    }

    [Fact]
    public void AvailableSpeciesFor_excludes_HypnoAlly_species_even_when_summonable_and_under_ceiling()
    {
        var catalog = new[]
        {
            Species("plant-avatar-one", CreatureRarity.Chaff, CreatureAcquisition.Summonable) with { DeployMode = CreatureDeployMode.PlantAvatar },
            Species("hypno-ally-one", CreatureRarity.Chaff, CreatureAcquisition.Summonable) with { DeployMode = CreatureDeployMode.HypnoAlly },
        };

        var result = ZombossDeployRoster.AvailableSpeciesFor(waveNumber: 3, catalog, Ceilings);

        Assert.Equal(new[] { "plant-avatar-one" }, result);
    }

    [Fact]
    public void AvailableSpeciesFor_includes_a_multi_flag_species_that_IS_summonable()
    {
        var catalog = new[]
        {
            Species("dual-flagged", CreatureRarity.Chaff, CreatureAcquisition.Summonable | CreatureAcquisition.CaptureOnly),
        };

        var result = ZombossDeployRoster.AvailableSpeciesFor(waveNumber: 3, catalog, Ceilings);

        Assert.Equal(new[] { "dual-flagged" }, result);
    }

    [Fact]
    public void AvailableSpeciesFor_is_deterministic_same_inputs_twice_same_output()
    {
        var catalog = new[]
        {
            Species("b-species", CreatureRarity.Chaff, CreatureAcquisition.Summonable),
            Species("a-species", CreatureRarity.Chaff, CreatureAcquisition.Summonable),
        };

        var first = ZombossDeployRoster.AvailableSpeciesFor(3, catalog, Ceilings);
        var second = ZombossDeployRoster.AvailableSpeciesFor(3, catalog, Ceilings);

        Assert.Equal(first, second);
        Assert.Equal(new[] { "a-species", "b-species" }, first); // ordinal, not catalog-array order
    }

    const string ValidJson = """
        {
          "version": 1,
          "difficultyPolicyIds": { "normal": "zomboss-deploy-ai:default" },
          "waveRarityCeilings": [
            { "maxWaveAtLeast": 1, "rarityCeiling": "Sprout" },
            { "maxWaveAtLeast": 10, "rarityCeiling": "Almanac" }
          ],
          "rosterSizeMax": 8,
          "scorer": { "fireChanceMilli": 300, "minEnemyUnitsToConsiderDeploy": 3, "maxConcurrentOwnUnits": 2 }
        }
        """;

    [Fact]
    public void Loader_parses_a_valid_file()
    {
        var tuning = ZombossDeployTuningLoader.Parse(ValidJson);

        Assert.Equal(1, tuning.Version);
        Assert.Equal("zomboss-deploy-ai:default", tuning.DifficultyPolicyIds["normal"]);
        Assert.Equal(2, tuning.WaveRarityCeilings.Count);
        Assert.Equal(8, tuning.RosterSizeMax);
        Assert.Equal(300, tuning.Scorer.FireChanceMilli);
        Assert.Equal(3, tuning.Scorer.MinEnemyUnitsToConsiderDeploy);
        Assert.Equal(2, tuning.Scorer.MaxConcurrentOwnUnits);
    }

    const string NonAscendingCeilingsJson = """
        {
          "version": 1,
          "difficultyPolicyIds": { "normal": "zomboss-deploy-ai:default" },
          "waveRarityCeilings": [
            { "maxWaveAtLeast": 10, "rarityCeiling": "Sprout" },
            { "maxWaveAtLeast": 1, "rarityCeiling": "Almanac" }
          ],
          "rosterSizeMax": 8,
          "scorer": { "fireChanceMilli": 300, "minEnemyUnitsToConsiderDeploy": 3, "maxConcurrentOwnUnits": 2 }
        }
        """;

    [Fact]
    public void Loader_rejects_non_ascending_maxWaveAtLeast()
    {
        Assert.Throws<ZombossDeployTuningRejection>(() => ZombossDeployTuningLoader.Parse(NonAscendingCeilingsJson));
    }

    [Fact]
    public void Loader_rejects_an_unknown_rarity_name()
    {
        var bad = ValidJson.Replace("\"Sprout\"", "\"NotARealRarity\"");

        Assert.Throws<ZombossDeployTuningRejection>(() => ZombossDeployTuningLoader.Parse(bad));
    }

    [Fact]
    public void Loader_rejects_an_empty_difficulty_map()
    {
        var bad = ValidJson.Replace("{ \"normal\": \"zomboss-deploy-ai:default\" }", "{}");

        Assert.Throws<ZombossDeployTuningRejection>(() => ZombossDeployTuningLoader.Parse(bad));
    }

    [Fact]
    public void Loader_rejects_a_fireChanceMilli_above_1000()
    {
        var bad = ValidJson.Replace("\"fireChanceMilli\": 300", "\"fireChanceMilli\": 1001");

        Assert.Throws<ZombossDeployTuningRejection>(() => ZombossDeployTuningLoader.Parse(bad));
    }

    [Fact]
    public void Loader_rejects_a_negative_minEnemyUnitsToConsiderDeploy()
    {
        var bad = ValidJson.Replace("\"minEnemyUnitsToConsiderDeploy\": 3", "\"minEnemyUnitsToConsiderDeploy\": -1");

        Assert.Throws<ZombossDeployTuningRejection>(() => ZombossDeployTuningLoader.Parse(bad));
    }

    [Fact]
    public void Hub_throws_a_named_error_before_Configure_and_resets_cleanly()
    {
        ZombossDeployTuningHub.ResetForTests();
        Assert.False(ZombossDeployTuningHub.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => ZombossDeployTuningHub.Tuning);

        ZombossDeployTuningHub.Configure(ZombossDeployTuningLoader.Parse(ValidJson));
        Assert.True(ZombossDeployTuningHub.IsConfigured);
        Assert.Equal(1, ZombossDeployTuningHub.Tuning.Version);

        ZombossDeployTuningHub.ResetForTests();
        Assert.False(ZombossDeployTuningHub.IsConfigured);
    }
}
