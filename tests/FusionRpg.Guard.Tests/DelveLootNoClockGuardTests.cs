using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// dungeon-loot (spec-dungeon-loot.md :236, verbatim: "no `System.Random`, no clock"). Mirrors
/// `DelveEventsNoClockGuardTests.cs`'s identical shape for `Delve/Events/`.
/// </summary>
public class DelveLootNoClockGuardTests
{
    static readonly string[] ForbiddenPatterns =
    {
        "DateTime.Now", "DateTime.UtcNow", "DateTimeOffset.Now", "DateTimeOffset.UtcNow",
        "Environment.TickCount", "ElapsedDays", "new Random(", "Random.Shared", "System.Random",
    };

    [Fact]
    public void Every_file_under_Core_Delve_Loot_reads_no_clock_and_no_unseeded_random()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Core", "Delve", "Loot");
        Assert.True(Directory.Exists(dir), "missing " + dir);
        var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
        Assert.True(files.Length > 0, "no source files found under " + dir);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in ForbiddenPatterns)
                Assert.False(text.Contains(forbidden, StringComparison.Ordinal), $"{Path.GetFileName(file)} contains '{forbidden}'");
        }
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
