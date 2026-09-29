using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests.Status;

public class StatusScenarioTests
{
    [Theory]
        [InlineData("status-wither-apply.json")]
        [InlineData("status-wither-area.json")]
        [InlineData("status-blight-spread.json")]
        [InlineData("status-blight-row.json")]
        [InlineData("status-blight-resist.json")]
        [InlineData("status-butter-apply.json")]
        [InlineData("status-butter-resist.json")]
        [InlineData("status-poison-immune.json")]
        [InlineData("status-leech-apply.json")]
        [InlineData("status-bond-counter.json")]
        [InlineData("status-rally-apply.json")]
        [InlineData("status-expose-apply.json")]
        [InlineData("status-command-apply.json")]
        [InlineData("status-shatter-apply.json")]
        [InlineData("status-rot-column.json")]
        [InlineData("status-spark-square.json")]
        [InlineData("status-pact-mark-random.json")]
        [InlineData("status-spore-rect.json")]
        [InlineData("status-cold-apply.json")]
        [InlineData("status-hypno-apply.json")]
        [InlineData("status-ember-apply.json")]
        [InlineData("status-jala-apply.json")]
        [InlineData("status-kelp-apply.json")]
        [InlineData("status-charm-pulse-apply.json")]
        [InlineData("status-freeze-resist.json")]
    public void Status_scenario_passes(string file)
    {
        var root = FindFixtures();
        var path = Path.Combine(root, "effects", "scenarios", file);
        var result = EffectScenarioRunner.RunFile(path, root);
        Assert.True(result.Ok, result.Error);
    }

    static string FindFixtures()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            var up = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "fixtures"));
            if (Directory.Exists(up)) return up;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }

        throw new DirectoryNotFoundException("fixtures");
    }
}
