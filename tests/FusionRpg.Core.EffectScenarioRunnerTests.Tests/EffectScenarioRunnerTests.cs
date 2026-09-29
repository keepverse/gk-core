using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests;

public class EffectScenarioRunnerTests
{
    public static IEnumerable<object[]> ScenarioFiles()
    {
        var dir = FindScenariosDir();
        foreach (var path in Directory.GetFiles(dir, "effect-*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            yield return new object[] { path };
    }

    [Theory]
    [MemberData(nameof(ScenarioFiles))]
    public void Offline_scenario_passes(string path)
    {
        var goldenRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, ".."));
        var result = EffectScenarioRunner.RunFile(path, goldenRoot);
        Assert.True(result.Ok, $"{result.Id}: {result.Error}");
    }

    [Fact]
    public void Scenarios_directory_has_expected_ids()
    {
        var ids = Directory.GetFiles(FindScenariosDir(), "effect-*.json")
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in new[]
                 {
                     "effect-butter-hit", "effect-freeze-hit", "effect-cold-hit", "effect-clear-butter",
                     "effect-passive-atk", "effect-spawn-ondeath", "effect-spawn-plant-bullet",
                     "effect-board-cherry", "effect-grid-cycle", "effect-set-dirt", "effect-economy-sun",
                     "effect-icd-butter", "effect-withdraw", "effect-spawn-filter", "effect-entity-atk",
                     "effect-butter-filter", "effect-withdraw-on-die",
                     "effect-secondary-butter-match", "effect-secondary-match-cycle"
                 })
            Assert.Contains(id, ids);
    }

    static string FindScenariosDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "effects", "scenarios");
            if (Directory.Exists(candidate)) return candidate;
            var up = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "fixtures", "effects", "scenarios"));
            if (Directory.Exists(up)) return up;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }

        throw new DirectoryNotFoundException("fixtures/effects/scenarios");
    }
}
