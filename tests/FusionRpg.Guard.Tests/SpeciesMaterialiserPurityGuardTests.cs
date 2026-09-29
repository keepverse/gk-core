using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// T5.5 (`player-materialise`, `spec-player-materialise.md` §4): "the cost is purity, and it is
/// absolute." No clock, no unseeded RNG, no raw dictionary/hash-set iteration order — a single impure
/// input destroys the whole "roster is a derivation, not a fact" property, silently, with no error.
/// <c>Guard.Tests</c> carries no project reference to Core, on purpose, matching every other guard in
/// this project — a guard cannot accidentally start exercising the thing it is meant to police.
/// </summary>
public class SpeciesMaterialiserPurityGuardTests
{
    static readonly string[] ForbiddenPatterns =
    {
        "DateTime.Now", "DateTime.UtcNow", "Environment.TickCount",
        "new Random(", "Random.Shared", "System.Random",
        "Guid.NewGuid",
    };

    [Fact]
    public void The_materialiser_reads_no_clock_and_no_unseeded_random()
    {
        var text = ReadCore("Creatures", "Materialise", "SpeciesMaterialiser.cs");

        foreach (var forbidden in ForbiddenPatterns)
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_materialiser_sorts_its_own_iteration_rather_than_trusting_caller_order()
    {
        var text = ReadCore("Creatures", "Materialise", "SpeciesMaterialiser.cs");

        // Q5/§4's own "no dictionary or hash-set iteration order" property, made mechanical: the
        // roster loop must call an explicit ordering operator, not iterate the caller's own list.
        Assert.Contains(".OrderBy(", text, StringComparison.Ordinal);
    }

    static string ReadCore(params string[] relativeUnderCore)
    {
        var path = Path.Combine(new[] { FindRepoRoot(), "src", "FusionRpg.Core" }.Concat(relativeUnderCore).ToArray());
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
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
