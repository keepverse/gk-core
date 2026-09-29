using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// delve-attrition (spec-delve-attrition.md §10, Testing Strategy): "no `DateTime.UtcNow`,
/// `DateTimeOffset.UtcNow`, `.Now`, `Environment.TickCount`, `ElapsedDays` or `System.Random` under
/// `Core/Delve/Attrition/`... `rpg_unique_actor_recovery` has no `*_utc` column." Recovery is a
/// counter, decremented once per `CloseDelve`, never a timed due-stamp (R6) — this guard makes that a
/// build-time fact rather than a claim in a doc comment. `Guard.Tests` carries no reference to Core or
/// Data, matching every other guard in this project, so this scans source text directly.
/// </summary>
public class DelveAttritionNoClockGuardTests
{
    static readonly string[] ForbiddenPatterns =
    {
        "DateTime.Now", "DateTime.UtcNow", "DateTimeOffset.Now", "DateTimeOffset.UtcNow",
        "Environment.TickCount", "ElapsedDays", "new Random(", "Random.Shared", "System.Random",
    };

    [Fact]
    public void Every_file_under_Core_Delve_Attrition_reads_no_clock_and_no_unseeded_random()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Core", "Delve", "Attrition");
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

    [Fact]
    public void The_rpg_unique_actor_recovery_table_carries_no_utc_column()
    {
        var text = ReadData("Sqlite", "RpgStore.cs");
        var start = text.IndexOf("CREATE TABLE IF NOT EXISTS rpg_unique_actor_recovery", StringComparison.Ordinal);
        Assert.True(start >= 0, "rpg_unique_actor_recovery table definition not found in RpgStore.cs");
        var end = text.IndexOf(");", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not find the end of the rpg_unique_actor_recovery table definition");
        var definition = text.Substring(start, end - start);

        Assert.DoesNotContain("_utc", definition, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rpg_unique_actor_pools_table_also_carries_no_utc_column()
    {
        var text = ReadData("Sqlite", "RpgStore.cs");
        var start = text.IndexOf("CREATE TABLE IF NOT EXISTS rpg_unique_actor_pools", StringComparison.Ordinal);
        Assert.True(start >= 0, "rpg_unique_actor_pools table definition not found in RpgStore.cs");
        var end = text.IndexOf(");", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not find the end of the rpg_unique_actor_pools table definition");
        var definition = text.Substring(start, end - start);

        Assert.DoesNotContain("_utc", definition, StringComparison.Ordinal);
    }

    static string ReadData(params string[] relativeUnderData)
    {
        var path = Path.Combine(new[] { FindRepoRoot(), "src", "FusionRpg.Data" }.Concat(relativeUnderData).ToArray());
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
