using FusionRpg.Core.Battle.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Ai;

/// <summary>species-build-todo.md T4.4 — <see cref="ZombossAdaptiveTuningLoader"/>. A missing key is a
/// load rejection naming it (spec's own requirement); `rotationWeights` doubles as a roster-consistency
/// check (missing or unknown pattern ids both reject).</summary>
public class ZombossAdaptiveTuningLoaderTests
{
    static string ValidWeights() => string.Join(",\n",
        ZombossPatterns.All.Select(id => $"\"{id}\": 100"));

    [Fact]
    public void Parse_theRealShippedFile_succeeds()
    {
        var path = FindShippedFile();
        var tuning = ZombossAdaptiveTuningLoader.Parse(File.ReadAllText(path));

        Assert.True(tuning.LoseStreakThreshold > 0);
        Assert.True(tuning.CounterBiasPermille > 0);
        Assert.True(tuning.RepatternCooldownEncounters > 0);
        Assert.Equal(ZombossPatterns.All.Count, tuning.RotationWeights.Count);
        foreach (var id in ZombossPatterns.All)
            Assert.True(tuning.RotationWeights[id] > 0);
    }

    [Fact]
    public void Parse_missingLoseStreakThreshold_rejectsNamingIt()
    {
        var json = $$"""
            {
              "schemaVersion": 1, "version": 1,
              "counterBiasPermille": 600, "repatternCooldownEncounters": 3, "revealDelayEncounters": 1,
              "rotationWeights": { {{ValidWeights()}} }
            }
            """;
        var ex = Assert.Throws<ZombossAdaptiveTuningRejection>(() => ZombossAdaptiveTuningLoader.Parse(json));
        Assert.Contains("loseStreakThreshold", ex.Message);
    }

    [Fact]
    public void Parse_missingRotationWeights_rejectsNamingIt()
    {
        var json = """
            {
              "schemaVersion": 1, "version": 1,
              "loseStreakThreshold": 3, "counterBiasPermille": 600,
              "repatternCooldownEncounters": 3, "revealDelayEncounters": 1
            }
            """;
        var ex = Assert.Throws<ZombossAdaptiveTuningRejection>(() => ZombossAdaptiveTuningLoader.Parse(json));
        Assert.Contains("rotationWeights", ex.Message);
    }

    [Fact]
    public void Parse_rotationWeightsMissingOnePatternId_rejectsNamingIt()
    {
        var missing = ZombossPatterns.All[0];
        var partial = string.Join(",\n",
            ZombossPatterns.All.Skip(1).Select(id => $"\"{id}\": 100"));
        var json = $$"""
            {
              "schemaVersion": 1, "version": 1,
              "loseStreakThreshold": 3, "counterBiasPermille": 600,
              "repatternCooldownEncounters": 3, "revealDelayEncounters": 1,
              "rotationWeights": { {{partial}} }
            }
            """;
        var ex = Assert.Throws<ZombossAdaptiveTuningRejection>(() => ZombossAdaptiveTuningLoader.Parse(json));
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void Parse_rotationWeightsWithAnUnknownPatternId_rejects()
    {
        var json = $$"""
            {
              "schemaVersion": 1, "version": 1,
              "loseStreakThreshold": 3, "counterBiasPermille": 600,
              "repatternCooldownEncounters": 3, "revealDelayEncounters": 1,
              "rotationWeights": { {{ValidWeights()}}, "not-a-real-pattern": 100 }
            }
            """;
        var ex = Assert.Throws<ZombossAdaptiveTuningRejection>(() => ZombossAdaptiveTuningLoader.Parse(json));
        Assert.Contains("not-a-real-pattern", ex.Message);
    }

    [Fact]
    public void Parse_emptyDocument_rejects()
    {
        Assert.Throws<ZombossAdaptiveTuningRejection>(() => ZombossAdaptiveTuningLoader.Parse(""));
    }

    static string FindShippedFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "tuning", "zomboss-adaptive.v1.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not locate data/tuning/zomboss-adaptive.v1.json above " + AppContext.BaseDirectory);
    }
}
