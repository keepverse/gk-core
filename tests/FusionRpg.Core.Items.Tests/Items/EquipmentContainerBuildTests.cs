using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `EquipmentContainerBuild.From` (party-dungeon-todo.md D4.12/D3.11, `mintAt`'s Equipment-kind arm,
/// 2026-09-07): assembles an ephemeral <see cref="ContainerRow"/> for a drawn Equipment-kind
/// <see cref="LootGrant"/>, drawing its pool candidates from the real, already-shipped
/// `RoleFamilyTable.Derive` legality table. Tested at two levels, mirroring
/// `UniqueContainerBuildTests`'s own "hand-built fixture proves the mechanism, real corpus proves it
/// actually works" split: small, hand-built <see cref="RoleFamilyCell"/>/<see cref="AtomRow"/> fixtures
/// for exact, deterministic assertions, plus one round trip against the real shipped affix-family
/// corpus and the real shipped atom catalog.
/// </summary>
public class EquipmentContainerBuildTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static AtomRow Atom(string familyId, string variant, int tier) => new()
    {
        AtomId = AtomRow.DeriveId(familyId, variant, tier),
        KindId = "stat.modify",
        FamilyId = familyId,
        Variant = variant,
        Tier = tier,
        Name = familyId,
        ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
    };

    static LootGrant EquipGrant(
        string frame = "humanoid", string role = "footing", string baseTypeId = "item.humanoid-feet-a-001",
        int minTier = 1, int maxTier = 3, int prefixRolls = 1, int suffixRolls = 0, int index = 0) =>
        new(index, DropEntryKind.Equipment, RefId: "", Count: 1, AffixChannel: AffixChannels.Drop,
            BaseTypeId: baseTypeId, Frame: frame, Role: role, RarityId: "cultivated", RarityOrdinal: 30,
            ItemLevel: 20, MinTier: minTier, MaxTier: maxTier, PrefixRolls: prefixRolls, SuffixRolls: suffixRolls);

    // ---- guards -------------------------------------------------------------------------------------

    [Fact]
    public void Null_grant_or_lookups_throw()
    {
        var lookups = new EquipmentContainerLookups(Array.Empty<RoleFamilyCell>(), _ => Array.Empty<AtomRow>());
        Assert.Throws<ArgumentNullException>(() => EquipmentContainerBuild.From(null!, lookups));
        Assert.Throws<ArgumentNullException>(() => EquipmentContainerBuild.From(EquipGrant(), null!));
    }

    [Fact]
    public void A_non_equipment_grant_throws()
    {
        var grant = EquipGrant() with { Kind = DropEntryKind.Unique };
        var lookups = new EquipmentContainerLookups(Array.Empty<RoleFamilyCell>(), _ => Array.Empty<AtomRow>());
        var ex = Assert.Throws<ArgumentException>(() => EquipmentContainerBuild.From(grant, lookups));
        Assert.Contains("Unique", ex.Message);
    }

    [Theory]
    [InlineData(null, "humanoid", "footing")]
    [InlineData("item.x", null, "footing")]
    [InlineData("item.x", "humanoid", null)]
    public void A_grant_missing_BaseTypeId_Frame_or_Role_throws_naming_the_grant(string? baseTypeId, string? frame, string? role)
    {
        var grant = new LootGrant(7, DropEntryKind.Equipment, "", 1, AffixChannels.Drop,
            BaseTypeId: baseTypeId, Frame: frame, Role: role, MinTier: 1, MaxTier: 3, PrefixRolls: 1);
        var lookups = new EquipmentContainerLookups(Array.Empty<RoleFamilyCell>(), _ => Array.Empty<AtomRow>());

        var ex = Assert.Throws<ArgumentException>(() => EquipmentContainerBuild.From(grant, lookups));
        Assert.Contains("grant 7", ex.Message);
    }

    // ---- pool assembly, hand-built fixture -----------------------------------------------------------

    [Fact]
    public void A_legal_family_within_the_tier_window_becomes_pool_candidates_at_weight_one()
    {
        var cells = new[] { new RoleFamilyCell("footing", "humanoid", "atom.vigor", MaxTier: 5) };
        var atoms = new Dictionary<string, IReadOnlyList<AtomRow>>(StringComparer.Ordinal)
        {
            ["atom.vigor"] = new[] { Atom("atom.vigor", "", 1), Atom("atom.vigor", "", 2), Atom("atom.vigor", "", 3) },
        };
        var lookups = new EquipmentContainerLookups(cells, f => atoms.TryGetValue(f, out var a) ? a : Array.Empty<AtomRow>());

        var result = EquipmentContainerBuild.From(EquipGrant(minTier: 1, maxTier: 3), lookups);

        Assert.Equal(3, result.Container.Pool.Count);
        Assert.All(result.Container.Pool, p => Assert.Equal(1, p.Weight));
        Assert.Equal(new[] { "affix.vigor.t1", "affix.vigor.t2", "affix.vigor.t3" },
            result.Container.Pool.Select(p => p.AffixId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(3, result.Affixes.Count); // the pool's own backing affix map, one per candidate
    }

    [Fact]
    public void A_role_or_frame_with_no_matching_cell_produces_an_empty_pool_not_a_throw()
    {
        var cells = new[] { new RoleFamilyCell("footing", "humanoid", "atom.vigor", MaxTier: 5) };
        var lookups = new EquipmentContainerLookups(cells,
            f => new[] { Atom(f, "", 1) });

        var result = EquipmentContainerBuild.From(EquipGrant(frame: "plant", role: "footing"), lookups);

        Assert.Empty(result.Container.Pool);
        Assert.Empty(result.Affixes);
    }

    [Fact]
    public void The_tier_window_is_the_intersection_of_the_grants_own_window_and_the_cells_own_ceiling()
    {
        var cells = new[] { new RoleFamilyCell("footing", "humanoid", "atom.vigor", MaxTier: 2) }; // cell caps at 2
        var atoms = new[] { Atom("atom.vigor", "", 1), Atom("atom.vigor", "", 2), Atom("atom.vigor", "", 3), Atom("atom.vigor", "", 4) };
        var lookups = new EquipmentContainerLookups(cells, _ => atoms);

        // grant itself allows up to tier 4, but the cell's own ceiling of 2 must win (Math.Min) for
        // which ATOMS actually enter the pool -- proven directly on the pool's own contents, which is
        // the property that matters (a t3/t4 atom must never be offered here).
        var result = EquipmentContainerBuild.From(EquipGrant(minTier: 1, maxTier: 4), lookups);

        Assert.Equal(new[] { "affix.vigor.t1", "affix.vigor.t2" },
            result.Container.Pool.Select(p => p.AffixId).OrderBy(id => id, StringComparer.Ordinal));
        // The container's own recorded window stays the GRANT's wider envelope (4) -- ContainerRow.MinTier/
        // MaxTier is an upper bound ContainerValidator enforces against every INCLUDED atom, and every
        // included atom already satisfies the tighter per-family ceiling by construction above, so a
        // wider container-level bound is safe, never a silent widening of what can actually be drawn.
        Assert.Equal(4, result.Container.MaxTier);
    }

    [Fact]
    public void An_atom_outside_the_tier_window_is_excluded()
    {
        var cells = new[] { new RoleFamilyCell("footing", "humanoid", "atom.vigor", MaxTier: 5) };
        var atoms = new[] { Atom("atom.vigor", "", 1), Atom("atom.vigor", "", 4), Atom("atom.vigor", "", 5) };
        var lookups = new EquipmentContainerLookups(cells, _ => atoms);

        var result = EquipmentContainerBuild.From(EquipGrant(minTier: 2, maxTier: 4), lookups);

        Assert.Equal(new[] { "affix.vigor.t4" }, result.Container.Pool.Select(p => p.AffixId));
    }

    [Fact]
    public void Two_cells_offering_the_same_atom_do_not_duplicate_the_pool_row()
    {
        // Should not happen in real content (a cell is unique per (role, family)) -- defensive, but a
        // real correctness property of this function's own de-dup, not of the derived table.
        var cells = new[]
        {
            new RoleFamilyCell("footing", "humanoid", "atom.vigor", MaxTier: 5),
            new RoleFamilyCell("footing", "humanoid", "atom.vigor", MaxTier: 5),
        };
        var lookups = new EquipmentContainerLookups(cells, _ => new[] { Atom("atom.vigor", "", 1) });

        var result = EquipmentContainerBuild.From(EquipGrant(minTier: 1, maxTier: 1), lookups);

        Assert.Single(result.Container.Pool);
    }

    [Fact]
    public void The_container_carries_the_grants_own_prefix_and_suffix_rolls_and_frame_and_base_type()
    {
        var lookups = new EquipmentContainerLookups(Array.Empty<RoleFamilyCell>(), _ => Array.Empty<AtomRow>());
        var grant = EquipGrant(frame: "plant", baseTypeId: "item.plant-footing-b-002", prefixRolls: 2, suffixRolls: 1);

        var result = EquipmentContainerBuild.From(grant, lookups);

        Assert.Equal(ContainerKind.Item, result.Container.Kind);
        Assert.Equal("plant", result.Container.Frame);
        Assert.Equal("item.plant-footing-b-002", result.Container.BaseTypeId);
        Assert.Equal(2, result.Container.PrefixRolls);
        Assert.Equal(1, result.Container.SuffixRolls);
        Assert.StartsWith("item.drop-plant-footing-b-002-", result.Container.ContainerId, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_different_grants_produce_two_different_ephemeral_container_ids()
    {
        var lookups = new EquipmentContainerLookups(Array.Empty<RoleFamilyCell>(), _ => Array.Empty<AtomRow>());
        var a = EquipmentContainerBuild.From(EquipGrant(index: 3), lookups);
        var b = EquipmentContainerBuild.From(EquipGrant(index: 4), lookups);
        Assert.NotEqual(a.Container.ContainerId, b.Container.ContainerId);
    }

    // ---- real corpus round trip -----------------------------------------------------------------------

    [Fact]
    public void Against_the_real_shipped_corpus_a_real_role_frame_pair_yields_a_non_empty_pool()
    {
        var families = AffixFamilySeedFile.LoadAll(Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "affix-families"));
        var overrides = FamilyOverrides.Parse(File.ReadAllText(
            Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "_registry", "family-overrides.v1.json")));
        var relocation = RoleRelocationTable.Parse(File.ReadAllText(
            Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "_registry", "role-relocation.v1.json")));
        var cells = RoleFamilyTable.Derive(families, overrides, relocation);

        var atomFiles = new[] { "atoms", "containers" }
            .Select(d => Path.Combine(KeepverseRoots.Content(), "data", "seed", d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.json", SearchOption.AllDirectories))
            .Where(f => !string.Equals(Path.GetFileName(f), "vocabulary.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (f, File.ReadAllText(f)))
            .ToArray();
        var collected = AtomSeedFile.Collect(atomFiles);
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));
        var atomsByFamily = collected.Content.Atoms
            .GroupBy(a => a.FamilyId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AtomRow>)g.ToList(), StringComparer.Ordinal);

        var lookups = new EquipmentContainerLookups(cells,
            family => atomsByFamily.TryGetValue(family, out var list) ? list : Array.Empty<AtomRow>());

        // item.humanoid-feet-a-001: a real, known base type (BaseTypeSeedFileTests' own pinned id),
        // frame=humanoid, role=footing. suffixRolls stays 0 -- a real, direct run against the real
        // corpus found `footing`/[1,5] carries zero drawable SUFFIX-class candidates today (every
        // legal family in that window happens to have no triggered atom) -- a genuine content-shape
        // fact about the real corpus, not something this test should paper over by asserting a budget
        // the real content cannot satisfy. prefixRolls: 1 alone still proves the real claim: a real
        // role/frame/tier-window combo produces a pool the real ContainerValidator accepts.
        var grant = EquipGrant(frame: "humanoid", role: "footing", baseTypeId: "item.humanoid-feet-a-001",
            minTier: 1, maxTier: 5, prefixRolls: 1, suffixRolls: 0);

        var result = EquipmentContainerBuild.From(grant, lookups);

        Assert.NotEmpty(result.Container.Pool);
        Assert.Equal(result.Container.Pool.Count, result.Affixes.Count);

        // The real ContainerValidator (the same safety net TryInstantiate always runs) must accept it --
        // proving this is not just "a non-empty pool" but a genuinely legal container.
        var atomsById = collected.Content.Atoms.ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var check = ContainerValidator.Validate(
            result.Container,
            id => atomsById.TryGetValue(id, out var a) ? a : null,
            id => result.Affixes.TryGetValue(id, out var af) ? af : null);
        Assert.True(check.IsOk, check.Detail);
    }
}
