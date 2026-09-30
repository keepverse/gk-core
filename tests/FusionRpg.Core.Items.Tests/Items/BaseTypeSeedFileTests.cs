using FusionRpg.Core.Items.Drops;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `BaseTypeSeedFile` (party-dungeon-todo.md D4.12, 2026-09-07) — the minimal `(id, frame, role)`
/// reader feeding `RpgStore.ImportBaseTypes`/`BuildLiveLootContentView.BaseTypesFor`. Deliberately
/// tested against both a hand-built temp corpus (full control over malformed rows) and the real,
/// shipped `gk-data/packs/fusion/data/seed/items/base-types/**` tree (proving the real content actually parses, not just a
/// synthetic fixture) — the same two-fixture split `DomainSeedFileTests`/`LootContentViewStoreTests`
/// already established for this program's other seed-file readers.
/// </summary>
public class BaseTypeSeedFileTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fusionrpg-base-type-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void LoadAll_null_dir_throws()
    {
        Assert.Throws<ArgumentNullException>(() => BaseTypeSeedFile.LoadAll(null!));
    }

    [Fact]
    public void LoadAll_missing_dir_returns_empty()
    {
        var missing = Path.Combine(Path.GetTempPath(), "fusionrpg-does-not-exist-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(BaseTypeSeedFile.LoadAll(missing));
    }

    [Fact]
    public void LoadAll_parses_a_well_formed_entry()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.json"), """
                { "kind": "base-type", "entries": [
                  { "id": "item.test-a-001", "frame": "plant", "role": "armament-primary" }
                ]}
                """);

            var rows = BaseTypeSeedFile.LoadAll(dir);

            var row = Assert.Single(rows);
            Assert.Equal("item.test-a-001", row.Id);
            Assert.Equal("plant", row.Frame);
            Assert.Equal("armament-primary", row.Role);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadAll_skips_an_entry_missing_a_required_field_rather_than_throwing()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.json"), """
                { "kind": "base-type", "entries": [
                  { "id": "item.no-role", "frame": "plant" },
                  { "id": "item.no-frame", "role": "footing" },
                  { "frame": "plant", "role": "footing" },
                  { "id": "item.good", "frame": "plant", "role": "footing" }
                ]}
                """);

            var rows = BaseTypeSeedFile.LoadAll(dir);

            var row = Assert.Single(rows);
            Assert.Equal("item.good", row.Id);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadAll_ignores_a_file_with_no_entries_array()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.json"), """{ "kind": "base-type" }""");
            Assert.Empty(BaseTypeSeedFile.LoadAll(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadAll_is_recursive_over_subdirectories()
    {
        var dir = NewTempDir();
        try
        {
            var nested = Path.Combine(dir, "footing", "humanoid");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(dir, "root.json"), """
                { "kind": "base-type", "entries": [ { "id": "item.root", "frame": "humanoid", "role": "footing" } ]}
                """);
            File.WriteAllText(Path.Combine(nested, "a.json"), """
                { "kind": "base-type", "entries": [ { "id": "item.nested", "frame": "humanoid", "role": "footing" } ]}
                """);

            var ids = BaseTypeSeedFile.LoadAll(dir).Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();

            Assert.Equal(new[] { "item.nested", "item.root" }, ids);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadAll_reads_the_real_shipped_corpus()
    {
        var rows = BaseTypeSeedFile.LoadAll(Path.Combine(RepoRoot(), "data", "seed", "items", "base-types"));

        // Program.cs's own boot comment cites "the 740-entry base-type corpus" -- a loose lower bound,
        // not an exact pin, since the corpus grows; this proves the real tree is actually being read,
        // not just a directory that happens to exist.
        Assert.True(rows.Count > 500, $"expected the real base-type corpus to carry >500 entries, got {rows.Count}");

        var byId = rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
        Assert.True(byId.ContainsKey("item.humanoid-feet-a-001"), "a known real base-type id did not resolve");
        var known = byId["item.humanoid-feet-a-001"];
        Assert.Equal("humanoid", known.Frame);
        Assert.Equal("footing", known.Role);

        // The recursive-partition claim (ItemBaseTypeCorpus.Load's own stated reason for walking
        // AllDirectories), proven against the real tree: `footing/humanoid/a.json` is a real,
        // subdirectory-nested file (not at the corpus root), and its own entries must still be read.
        Assert.Contains(rows, r => r.Frame == "humanoid" && r.Role == "footing");
    }
}
