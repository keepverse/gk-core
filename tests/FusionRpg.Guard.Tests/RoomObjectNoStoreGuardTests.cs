using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// D3.27 (spec-supplies-and-objects.md §4, own closing line: "Building the projection draws
/// nothing") — the todo's own "nothing is written to a new table" Verify line, made a real guard
/// rather than an unchecked claim. Mirrors `DelveLootNoClockGuardTests.cs`'s identical source-scan
/// shape for `Delve/Objects/`, extended with store-write markers since this file's own claim is about
/// persistence, not determinism.
/// </summary>
public class RoomObjectNoStoreGuardTests
{
    static readonly string[] ForbiddenPatterns =
    {
        "DateTime.Now", "DateTime.UtcNow", "DateTimeOffset.Now", "DateTimeOffset.UtcNow",
        "Environment.TickCount", "new Random(", "Random.Shared", "System.Random",
        "SqliteConnection", "SqliteCommand", "RpgStore", "INSERT INTO", "UPDATE ", "CREATE TABLE",
    };

    [Fact]
    public void Every_file_under_Core_Delve_Objects_reads_and_writes_no_store()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Core", "Delve", "Objects");
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
