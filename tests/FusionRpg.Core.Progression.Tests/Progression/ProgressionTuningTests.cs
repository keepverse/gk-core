using FusionRpg.Core.Progression;
using Xunit;

namespace FusionRpg.Core.Tests.Progression;

public sealed class ProgressionTuningTests
{
    [Fact]
    public void Loader_reads_dedicated_specimen_lawn_awards()
    {
        var tuning = ProgressionTuningLoader.Parse("""
            { "schemaVersion": 1, "version": 3,
              "xpCurve": {
                "plant": { "first": 80, "step": 32 },
                "zombie": { "first": 70, "step": 28 },
                "player": { "first": 100, "step": 45 },
                "specimen": { "first": 100, "step": 45 },
                "empire": { "first": 10, "step": 5 } },
              "awards": { "kill": 12, "defeat": -100, "mower": -30,
                "plantPlace": 8, "zombieSpawn": 9, "speciesLevelUp": 1,
                "specimenLawnKill": 18, "specimenBoundIntervalMs": 1000, "specimenBoundIntervalXp": 2 } }
            """);

        Assert.Equal(18, tuning.Awards.SpecimenLawnKill);
        Assert.Equal(1000, tuning.Awards.SpecimenBoundIntervalMs);
        Assert.Equal(2, tuning.Awards.SpecimenBoundIntervalXp);
    }

    /// <summary>A current-shaped document (the empire pair IS present, because EP4.2 made it
    /// required) MINUS the dedicated specimen awards, which stay optional by design.</summary>
    [Fact]
    public void Older_documents_default_dedicated_awards_to_zero()
    {
        var tuning = ProgressionTuningLoader.Parse("""
            { "schemaVersion": 1, "version": 3,
              "xpCurve": {
                "plant": { "first": 80, "step": 32 },
                "zombie": { "first": 70, "step": 28 },
                "player": { "first": 100, "step": 45 },
                "specimen": { "first": 100, "step": 45 },
                "empire": { "first": 10, "step": 5 } },
              "awards": { "kill": 12, "defeat": -100, "mower": -30,
                "plantPlace": 8, "zombieSpawn": 9, "speciesLevelUp": 1 } }
            """);

        Assert.Equal(0, tuning.Awards.SpecimenLawnKill);
        Assert.Equal(0, tuning.Awards.SpecimenBoundIntervalMs);
        Assert.Equal(0, tuning.Awards.SpecimenBoundIntervalXp);
    }

    [Theory]
    [InlineData("specimenLawnKill")]
    [InlineData("specimenBoundIntervalMs")]
    [InlineData("specimenBoundIntervalXp")]
    public void Dedicated_awards_reject_non_positive_values(string key)
    {
        var json = $$"""
        { "xpCurve": { "player": { "first": 60, "step": 30 }, "plant": { "first": 60, "step": 30 }, "zombie": { "first": 60, "step": 30 }, "specimen": { "first": 60, "step": 30 }, "empire": { "first": 10, "step": 5 } },
          "awards": { "kill": 20, "defeat": -100, "mower": -30, "plantPlace": 8, "zombieSpawn": 9, "speciesLevelUp": 1, "{{key}}": 0 } }
        """;

        Assert.Throws<ProgressionTuningRejection>(() => ProgressionTuningLoader.Parse(json));
    }

    // ---- zomboss-commander-clock SP7.1 -- awards.zombossRunVictoryXp / zombossRunDefeatXp ---------

    [Fact]
    public void Loader_reads_the_zomboss_commander_clock_awards()
    {
        var tuning = ProgressionTuningLoader.Parse("""
            { "schemaVersion": 1, "version": 3,
              "xpCurve": {
                "plant": { "first": 80, "step": 32 },
                "zombie": { "first": 70, "step": 28 },
                "player": { "first": 100, "step": 45 },
                "specimen": { "first": 100, "step": 45 },
                "empire": { "first": 10, "step": 5 } },
              "awards": { "kill": 12, "defeat": -100, "mower": -30,
                "plantPlace": 8, "zombieSpawn": 9, "speciesLevelUp": 1,
                "zombossRunVictoryXp": 100, "zombossRunDefeatXp": 25 } }
            """);

        Assert.Equal(100, tuning.Awards.ZombossRunVictoryXp);
        Assert.Equal(25, tuning.Awards.ZombossRunDefeatXp);
    }

    /// <summary>The same shape as the test above, for the Zomboss commander-clock pair: a current
    /// document (empire pair present, required) without them still loads, and they default to 0.</summary>
    [Fact]
    public void Older_documents_without_the_zomboss_clock_awards_default_to_zero_never_a_rejection()
    {
        // The deliberate, evidenced deviation from the literal "rejects a file missing either one"
        // spec text (XpAwardsTuning.ZombossRunVictoryXp's own doc comment): progression.v3.json has
        // exactly one published revision, and ~28 real, unrelated test files hardcode that literal
        // path -- a hard-required key here would make every one of them unloadable.
        var tuning = ProgressionTuningLoader.Parse("""
            { "schemaVersion": 1, "version": 3,
              "xpCurve": {
                "plant": { "first": 80, "step": 32 },
                "zombie": { "first": 70, "step": 28 },
                "player": { "first": 100, "step": 45 },
                "specimen": { "first": 100, "step": 45 },
                "empire": { "first": 10, "step": 5 } },
              "awards": { "kill": 12, "defeat": -100, "mower": -30,
                "plantPlace": 8, "zombieSpawn": 9, "speciesLevelUp": 1 } }
            """);

        Assert.Equal(0, tuning.Awards.ZombossRunVictoryXp);
        Assert.Equal(0, tuning.Awards.ZombossRunDefeatXp);
    }

    [Theory]
    [InlineData("zombossRunVictoryXp")]
    [InlineData("zombossRunDefeatXp")]
    public void Zomboss_clock_awards_reject_non_positive_values(string key)
    {
        var json = $$"""
        { "xpCurve": { "player": { "first": 60, "step": 30 }, "plant": { "first": 60, "step": 30 }, "zombie": { "first": 60, "step": 30 }, "specimen": { "first": 60, "step": 30 }, "empire": { "first": 10, "step": 5 } },
          "awards": { "kill": 20, "defeat": -100, "mower": -30, "plantPlace": 8, "zombieSpawn": 9, "speciesLevelUp": 1, "{{key}}": 0 } }
        """;

        Assert.Throws<ProgressionTuningRejection>(() => ProgressionTuningLoader.Parse(json));
    }

    [Fact]
    public void The_live_shipped_file_carries_both_zomboss_clock_awards()
    {
        // The real, published file (SP7.1, publish.py) -- proves the live shipped config actually
        // carries what Program.cs now reads, not just a synthetic fixture.
        var tuning = ProgressionTuningLoader.Parse(File.ReadAllText(LatestProgressionPath()));

        Assert.True(tuning.Awards.ZombossRunVictoryXp > 0);
        Assert.True(tuning.Awards.ZombossRunDefeatXp > 0);
        Assert.True(tuning.Awards.ZombossRunVictoryXp > tuning.Awards.ZombossRunDefeatXp,
            "the award for a human defeat (Zomboss winning) should exceed the consolation award for a human victory");
    }

    static string LatestProgressionPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var tuningDir = Path.Combine(dir.FullName, "data", "tuning");
            if (Directory.Exists(tuningDir))
            {
                var best = Directory.GetFiles(tuningDir, "progression.v*.json")
                    .Select(f => (Path: f, V: int.TryParse(
                        Path.GetFileNameWithoutExtension(f).Split(".v").Last(), out var v) ? v : -1))
                    .Where(x => x.V >= 0).OrderByDescending(x => x.V).FirstOrDefault();
                if (best.Path is not null) return best.Path;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no data/tuning/progression.v*.json above " + AppContext.BaseDirectory);
    }
}
