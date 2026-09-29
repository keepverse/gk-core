using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// event-deck (spec-event-deck.md, Testing Strategy §"Untouched"): "No clock, no `System.Random`: a
/// guard test over `Core/Delve/Events/` (the `spec-turn-engine.md:138` scan shape)." Every draw in this
/// module resolves through `SeededRng.DeriveStream` off a sealed delve seed — this guard makes "no wall
/// clock, no unseeded RNG" a build-time fact rather than a claim in a doc comment, mirroring
/// `DelveAttritionNoClockGuardTests.cs`'s own identical shape for `Delve/Attrition/`.
/// </summary>
public class DelveEventsNoClockGuardTests
{
    static readonly string[] ForbiddenPatterns =
    {
        "DateTime.Now", "DateTime.UtcNow", "DateTimeOffset.Now", "DateTimeOffset.UtcNow",
        "Environment.TickCount", "ElapsedDays", "new Random(", "Random.Shared", "System.Random",
    };

    [Fact]
    public void Every_file_under_Core_Delve_Events_reads_no_clock_and_no_unseeded_random()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Core", "Delve", "Events");
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
