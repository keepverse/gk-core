using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// loot-pack (spec-loot-pack.md §Testing, "the_pack_never_reads_armoury_capacity"; §Boundaries
/// "Never: reading `InventoryCeiling`"). `InventoryCeiling` (`RpgStore.Items.cs:258`) is the armoury's
/// own abuse guard, not a capacity — the pack is a separate, structural per-run limit (§1) and must
/// never read it or otherwise re-derive an armoury row count. Mirrors `DelveLootNoClockGuardTests.cs`'s
/// identical source-scan shape for `Delve/Pack/`.
/// </summary>
public class PackNeverReadsArmouryCapacityGuardTests
{
    static readonly string[] ForbiddenPatterns = { "InventoryCeiling", "CountArmouryRows" };

    [Fact]
    public void Every_file_under_Core_Delve_Pack_never_reads_armoury_capacity()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Core", "Delve", "Pack");
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

    /// <summary>The pack half of `RpgStore.Delve.cs` (once it exists, per spec §Structure) is covered
    /// too — the guard scans the WHOLE file, not just a "Pack" region, since the boundary is about
    /// what the Data-layer pack code may read, not a naming convention.</summary>
    [Fact]
    public void RpgStore_Delve_never_reads_armoury_capacity()
    {
        var file = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Data", "Sqlite", "RpgStore.Delve.cs");
        Assert.True(File.Exists(file), "missing " + file);
        var text = File.ReadAllText(file);
        foreach (var forbidden in ForbiddenPatterns)
            Assert.False(text.Contains(forbidden, StringComparison.Ordinal), $"RpgStore.Delve.cs contains '{forbidden}'");
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
