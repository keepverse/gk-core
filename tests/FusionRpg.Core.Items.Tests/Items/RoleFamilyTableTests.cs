using System.Text.Json;
using FusionRpg.Core.Items;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `affix-legality` (item module 8) against the REAL, shipped affix families (98 at module 8's
/// build, 100 since `g-punisher.json` landed 2026-09-06), `family-overrides.v1.json` and
/// `role-relocation.v1.json`.
/// </summary>
public class RoleFamilyTableTests
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

    static List<AffixFamilySource> LoadFamilies()
    {
        var dir = Path.Combine(RepoRoot(), "data", "seed", "items", "affix-families");
        var result = new List<AffixFamilySource>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                var roles = e.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToList();
                var frames = e.GetProperty("frames").EnumerateArray().Select(r => r.GetString()!).ToList();
                result.Add(new AffixFamilySource(
                    e.GetProperty("id").GetString()!, roles, frames,
                    e.GetProperty("side").GetString()!, e.GetProperty("kindId").GetString()!));
            }
        }

        return result;
    }

    static FamilyOverrides LoadOverrides() =>
        FamilyOverrides.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "seed", "items", "_registry", "family-overrides.v1.json")));

    static RoleRelocationTable LoadRelocation() =>
        RoleRelocationTable.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "seed", "items", "_registry", "role-relocation.v1.json")));

    static IReadOnlyList<RoleFamilyCell> Derive() => RoleFamilyTable.Derive(LoadFamilies(), LoadOverrides(), LoadRelocation());

    /// <summary>
    /// Contract, not a count: the corpus is generator-authored and grows every generation, so a
    /// literal family total is stale the moment content ships. What this asserts instead is the
    /// structural invariant the old number was a proxy for — the corpus file set is non-empty, every
    /// family parses into the required shape, and family ids are unique (a duplicate id would silently
    /// collapse two families into one derived cell set). A DROP still fails: an empty parse or a
    /// duplicate id is the defect, not the total.
    /// </summary>
    [Fact]
    public void The_whole_shipped_affix_family_corpus_loads()
    {
        var families = LoadFamilies();
        Assert.NotEmpty(families);
        // Every family parses with a non-empty id, at least one role, at least one frame, and a side.
        Assert.All(families, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.FamilyId));
            Assert.NotEmpty(f.Roles);
            Assert.NotEmpty(f.Frames);
            Assert.False(string.IsNullOrWhiteSpace(f.Side));
        });
        // Unique ids — the invariant a count pin was standing in for.
        var duplicateIds = families.GroupBy(f => f.FamilyId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicateIds);
    }

    [Fact]
    public void The_relocation_artefact_exists_and_covers_every_dropped_family_with_zero_orphans()
    {
        // The artefact is DERIVED from the corpus, so its row count tracks the corpus and is not a
        // literal to pin — the generator changes the corpus on every run. Assert the derivation
        // invariant instead: every (dropped role, family) pair legal in the corpus has at least one
        // relocation row, and every row's host is genuinely legal on that family (the exact rules
        // `RoleRelocationRowMissing` / `RoleRelocationHostNotLegal` enforce in ItemSeedValidator).
        var families = LoadFamilies();
        var byId = families.ToDictionary(f => f.FamilyId, f => f.Roles, StringComparer.Ordinal);

        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "data", "seed", "items", "_registry", "role-relocation.v1.json")));
        var rows = doc.RootElement.GetProperty("relocations").EnumerateArray()
            .Select(r => (
                Dropped: r.GetProperty("droppedRole").GetString()!,
                Family: r.GetProperty("familyId").GetString()!,
                Host: r.GetProperty("hostRole").GetString()!))
            .ToList();
        Assert.NotEmpty(rows);

        var covered = rows.Select(r => (r.Dropped, r.Family)).ToHashSet();
        var droppedRoles = new[] { "head-guard", "sense", "ward-array" };
        foreach (var f in families)
            foreach (var dropped in droppedRoles.Where(f.Roles.Contains))
                Assert.True(covered.Contains((dropped, f.FamilyId)),
                    $"{f.FamilyId} is legal on dropped role '{dropped}' but has no relocation row — "
                    + "it would keep its full max_tier on every surviving hybrid-core host");

        foreach (var r in rows)
        {
            Assert.True(byId.TryGetValue(r.Family, out var roles),
                $"relocation names '{r.Family}', not a family in the corpus");
            Assert.Contains(r.Host, roles!);
        }

        var relocation = LoadRelocation();
        Assert.Equal(new[] { "head-guard", "sense", "ward-array" }, relocation.DroppedRoles.OrderBy(s => s));
    }

    [Fact]
    public void A_relocated_family_carries_a_reduced_max_tier_on_its_host()
    {
        var relocation = LoadRelocation();
        // ward-array's atom.shield-capacity is legal on armament-secondary and jewel-major (its own
        // roles list carries no core-guard cell -- ssot-equip-slots.md §4.2's core-guard example is
        // the mechanism's precedent, not a literal claim about this family's roles). Both surviving
        // hosts carry the same reduced ceiling, matching that precedent's shape (5 -> 3).
        Assert.Equal(3, relocation.ReducedMaxTier("armament-secondary", "atom.shield-capacity"));
        Assert.Equal(3, relocation.ReducedMaxTier("jewel-major", "atom.shield-capacity"));
    }

    [Fact]
    public void Head_guard_has_legal_families()
    {
        var families = LoadFamilies();
        var headGuardFamilies = families.Where(f => f.Roles.Contains("head-guard")).ToList();
        // The count (45 today) is a growing content population, never pinned (population-pin SE3.3,
        // 2026-09-19).
        Assert.NotEmpty(headGuardFamilies);
    }

    [Fact]
    public void Both_minor_jewels_cap_every_family_at_tier_3()
    {
        var cells = Derive();
        foreach (var role in new[] { "jewel-minor-a", "jewel-minor-b" })
        {
            var roleCells = cells.Where(c => c.RoleId == role).ToList();
            Assert.NotEmpty(roleCells);
            Assert.All(roleCells, c => Assert.True(c.MaxTier <= 3, $"{role}/{c.FamilyId} has max tier {c.MaxTier}"));
        }
    }

    [Fact]
    public void Bulwark_and_savagery_are_absent_from_both_minor_jewels()
    {
        var cells = Derive();
        foreach (var role in new[] { "jewel-minor-a", "jewel-minor-b" })
        {
            Assert.DoesNotContain(cells, c => c.RoleId == role && c.FamilyId == "atom.bulwark");
            Assert.DoesNotContain(cells, c => c.RoleId == role && c.FamilyId == "atom.savagery");
        }
    }

    [Fact]
    public void Jewel_major_is_untouched_by_the_minor_jewel_override()
    {
        // The role-cap and removedFamilies overrides target the twins only. jewel-major's own
        // atom.bulwark cell is separately reduced to 3 by the D3 relocation (bulwark is ALSO legal on
        // both head-guard and sense, two of the three dropped roles) -- that reduction is real and
        // expected, and distinct from the minor-jewel override this test isolates.
        var overrides = LoadOverrides();
        Assert.Null(overrides.RoleCap("jewel-major"));
        Assert.False(overrides.IsRemoved("jewel-major", "atom.bulwark"));
        Assert.False(overrides.IsRemoved("jewel-major", "atom.savagery"));
    }

    [Fact]
    public void The_only_max_tier_overrides_are_the_declared_list()
    {
        var overrides = LoadOverrides();
        Assert.Equal(3, overrides.RoleCap("jewel-minor-a"));
        Assert.Equal(3, overrides.RoleCap("jewel-minor-b"));
        Assert.Null(overrides.RoleCap("jewel-major"));
        Assert.Null(overrides.RoleCap("armament-primary"));
    }

    [Fact]
    public void Item_role_family_is_derived_with_no_authored_cells()
    {
        // Contract, not a count: the (role, family) pair total is corpus-derived and moves on every
        // generation, so pinning it is stale by construction. What matters structurally is that
        // Derive() produces exactly the corpus's own distinct (role, family) pairs minus the cells the
        // minor-jewel override removes — no authored cell, no dropped cell, and no phantom cell.
        var families = LoadFamilies();
        var rawPairs = families.SelectMany(f => f.Roles.Select(r => (Role: r, Family: f.FamilyId)))
            .Distinct().ToHashSet();
        Assert.NotEmpty(rawPairs);

        var expected = rawPairs
            .Where(p => p.Role is not ("jewel-minor-a" or "jewel-minor-b")
                        || p.Family is not ("atom.bulwark" or "atom.savagery"))
            .Select(p => (Role: p.Role, Family: p.Family))
            .ToHashSet();

        var derivedPairs = Derive().Select(c => (Role: c.RoleId, Family: c.FamilyId)).Distinct().ToHashSet();
        Assert.Equal(expected.OrderBy(x => x.Role).ThenBy(x => x.Family),
                     derivedPairs.OrderBy(x => x.Role).ThenBy(x => x.Family));
    }

    [Fact]
    public void The_role_group_matrix_has_sixteen_distinct_roles()
    {
        var families = LoadFamilies();
        var roles = families.SelectMany(f => f.Roles).Distinct(StringComparer.Ordinal).ToHashSet();
        // Formula, not a literal (population-pin SE3.3, 2026-09-19): every ItemRole is a legal role id.
        Assert.Equal(Enum.GetValues<ItemRole>().Length, roles.Count);
        Assert.Contains("standard", roles);
    }

    [Fact]
    public void No_family_is_orphaned_by_the_three_dropped_hybrid_roles()
    {
        var families = LoadFamilies();
        var hybridCore = new HashSet<string>
        {
            "armament-primary", "core-guard", "armament-secondary", "jewel-major", "manipulator",
            "mantle", "girdle", "footing", "infusion", "retinue", "jewel-minor-a", "jewel-minor-b",
        };

        foreach (var dropped in new[] { "ward-array", "head-guard", "sense" })
            foreach (var f in families.Where(f => f.Roles.Contains(dropped)))
                Assert.True(f.Roles.Any(hybridCore.Contains), $"{f.FamilyId} (dropped via '{dropped}') has no surviving hybrid-core host");
    }
}
