using FusionRpg.Core.Items;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `AffixFamilySeedFile` (party-dungeon-todo.md D4.12/D3.11, `mintAt`'s Equipment-kind arm,
/// 2026-09-07) — the production reader `RoleFamilyTable.Derive`/`AffixFilters` (item module 8) always
/// needed but never had outside `RoleFamilyTableTests.LoadFamilies`. Deliberately tested against both a
/// hand-built temp corpus (full control over malformed rows) and the real, shipped
/// `gk-data/packs/fusion/data/seed/items/affix-families/**` tree — the same two-fixture split
/// `BaseTypeSeedFileTests`/`RoleFamilyTableTests` already established for this program's other seed
/// readers.
/// </summary>
public class AffixFamilySeedFileTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CONTRIBUTING.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fusionrpg-affix-family-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void LoadAll_null_dir_throws()
    {
        Assert.Throws<ArgumentNullException>(() => AffixFamilySeedFile.LoadAll(null!));
    }

    [Fact]
    public void LoadAll_missing_dir_returns_empty()
    {
        var missing = Path.Combine(Path.GetTempPath(), "fusionrpg-does-not-exist-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(AffixFamilySeedFile.LoadAll(missing));
    }

    [Fact]
    public void LoadAll_parses_a_well_formed_entry()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.json"), """
                { "kind": "affix-family", "entries": [
                  { "id": "atom.test-fam", "roles": ["footing", "girdle"], "frames": ["humanoid", "plant"],
                    "side": "both", "kindId": "stat.modify" }
                ]}
                """);

            var rows = AffixFamilySeedFile.LoadAll(dir);

            var row = Assert.Single(rows);
            Assert.Equal("atom.test-fam", row.FamilyId);
            Assert.Equal(new[] { "footing", "girdle" }, row.Roles);
            Assert.Equal(new[] { "humanoid", "plant" }, row.Frames);
            Assert.Equal("both", row.Side);
            Assert.Equal("stat.modify", row.KindId);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadAll_skips_an_entry_missing_a_required_scalar_field_rather_than_throwing()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.json"), """
                { "kind": "affix-family", "entries": [
                  { "id": "atom.no-side", "roles": ["footing"], "frames": ["humanoid"], "kindId": "stat.modify" },
                  { "id": "atom.no-kind", "roles": ["footing"], "frames": ["humanoid"], "side": "both" },
                  { "roles": ["footing"], "frames": ["humanoid"], "side": "both", "kindId": "stat.modify" },
                  { "id": "atom.good", "roles": ["footing"], "frames": ["humanoid"], "side": "both", "kindId": "stat.modify" }
                ]}
                """);

            var rows = AffixFamilySeedFile.LoadAll(dir);

            var row = Assert.Single(rows);
            Assert.Equal("atom.good", row.FamilyId);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadAll_skips_an_entry_with_an_empty_roles_or_frames_list()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.json"), """
                { "kind": "affix-family", "entries": [
                  { "id": "atom.no-roles", "roles": [], "frames": ["humanoid"], "side": "both", "kindId": "stat.modify" },
                  { "id": "atom.no-frames", "roles": ["footing"], "frames": [], "side": "both", "kindId": "stat.modify" },
                  { "id": "atom.good", "roles": ["footing"], "frames": ["humanoid"], "side": "both", "kindId": "stat.modify" }
                ]}
                """);

            var rows = AffixFamilySeedFile.LoadAll(dir);

            var row = Assert.Single(rows);
            Assert.Equal("atom.good", row.FamilyId);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadAll_ignores_a_file_with_no_entries_array()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.json"), """{ "kind": "affix-family" }""");
            Assert.Empty(AffixFamilySeedFile.LoadAll(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadAll_is_recursive_over_subdirectories()
    {
        var dir = NewTempDir();
        try
        {
            var nested = Path.Combine(dir, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(dir, "root.json"), """
                { "kind": "affix-family", "entries": [
                  { "id": "atom.root", "roles": ["footing"], "frames": ["humanoid"], "side": "both", "kindId": "stat.modify" } ]}
                """);
            File.WriteAllText(Path.Combine(nested, "a.json"), """
                { "kind": "affix-family", "entries": [
                  { "id": "atom.nested", "roles": ["footing"], "frames": ["humanoid"], "side": "both", "kindId": "stat.modify" } ]}
                """);

            var ids = AffixFamilySeedFile.LoadAll(dir).Select(r => r.FamilyId).OrderBy(id => id, StringComparer.Ordinal).ToList();

            Assert.Equal(new[] { "atom.nested", "atom.root" }, ids);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadAll_reads_the_real_shipped_corpus_and_RoleFamilyTable_derives_from_it()
    {
        var dir = Path.Combine(RepoRoot(), "data", "seed", "items", "affix-families");
        var rows = AffixFamilySeedFile.LoadAll(dir);

        // RoleFamilyTableTests.cs's own corpus-count pin: 98 at module 8's original build, up to 112+
        // since (a loose lower bound here, not a re-pin of that file's own exact count -- this test's
        // own job is proving the reader parses the real tree, not tracking corpus growth a second time).
        Assert.True(rows.Count >= 98, $"expected the real affix-family corpus to carry >=98 entries, got {rows.Count}");

        var overrides = FamilyOverrides.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "seed", "items", "_registry", "family-overrides.v1.json")));
        var relocation = RoleRelocationTable.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "seed", "items", "_registry", "role-relocation.v1.json")));

        // The real point of this reader: it must be USABLE by the real, already-shipped, already-tested
        // RoleFamilyTable.Derive without any further translation -- proving the two halves item module 8
        // built on different days (the pure logic, and now this reader) actually compose.
        var cells = RoleFamilyTable.Derive(rows, overrides, relocation);
        Assert.NotEmpty(cells);
        // A real, known-legal cell from spec-affix-legality.md's own measured distribution table
        // ("armament-primary: 45 legal families, 45.9%") -- at least one row must resolve for it.
        Assert.Contains(cells, c => c.RoleId == "armament-primary");
    }
}
