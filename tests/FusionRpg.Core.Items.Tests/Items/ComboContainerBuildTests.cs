using System.Text.Json.Nodes;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Generation;
using FusionRpg.Core.Items.Sockets;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// strain-splice-host SSH4.3 (spec-combo-bind §1): the combination container as a pure function of
/// <c>(comboId, grants, tier)</c>, and the F7 tier bound enforced where tuning loads. Built against a
/// supplied atom lookup (Core reads no file), so the tests own the catalog.
/// </summary>
public class ComboContainerBuildTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static SocketTuning Sockets() => SocketTuning.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Current)));

    static AtomRow Atom(string id) => new() { AtomId = id, FamilyId = id.Split('.')[1] };

    static ComboContainerBuild.ComboContainerLookups Catalog(params string[] ids)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        return new ComboContainerBuild.ComboContainerLookups(id => set.Contains(id) ? Atom(id) : null);
    }

    [Fact]
    public void A_container_carries_one_fixed_atom_per_grant_and_zero_pool_rolls()
    {
        var lookups = Catalog("atom.might.t2", "atom.savagery.t2");
        var container = ComboContainerBuild.TryBuild(
            "combo.strain-might-offense", new[] { "atom.might", "atom.savagery" }, 2,
            lookups, out var refused);

        Assert.Null(refused);
        Assert.NotNull(container);
        Assert.Equal("combo.strain-might-offense-t2", container!.ContainerId);
        Assert.Equal(ContainerKind.Combo, container.Kind);
        Assert.Equal(new[] { "atom.might.t2", "atom.savagery.t2" },
            container.Atoms.Select(a => a.AtomId));
        Assert.Equal(new[] { 0, 1 }, container.Atoms.Select(a => a.Seq));
        // Grants are FIXED atoms: no variance pool, no roll, no rarity.
        Assert.Equal(0, container.PrefixRolls);
        Assert.Equal(0, container.SuffixRolls);
        Assert.Empty(container.Pool);
    }

    [Fact]
    public void A_grant_family_without_an_atom_is_refused_by_name()
    {
        var lookups = Catalog("atom.might.t2");   // atom.savagery.t2 is absent
        var container = ComboContainerBuild.TryBuild(
            "combo.strain-might-offense", new[] { "atom.might", "atom.savagery" }, 2,
            lookups, out var refused);

        Assert.Null(container);
        Assert.NotNull(refused);
        Assert.Contains("atom.savagery", refused);
        Assert.Contains("atom.savagery.t2", refused);
        Assert.Contains("combo.strain-might-offense-t2", refused);
    }

    [Fact]
    public void A_grant_list_that_is_empty_is_refused_rather_than_built()
    {
        var container = ComboContainerBuild.TryBuild(
            "combo.strain-might-offense", Array.Empty<string>(), 1, Catalog(), out var refused);
        Assert.Null(container);
        Assert.Contains("grants no family", refused);
    }

    [Fact]
    public void A_granted_tier_above_the_atom_ladder_throws_at_load()
    {
        // F7: atoms materialise only for tiers 1..FamilyExpansion.TierCount, so a top baseTier plus
        // the attuned bonus above that binds no atom. A THROW at load, never a clamp at bind.
        var sockets = Sockets();
        // The tuning is legal when the top rung (TierCount - attunedTierBonus) + bonus == TierCount.
        var legal = StrainSpliceTuning.Parse(
            StrainSpliceDoc(FamilyExpansion.TierCount - sockets.AttunedTierBonus), sockets);
        Assert.Equal(FamilyExpansion.TierCount - sockets.AttunedTierBonus, legal.BaseTierFor(ComboShape.Strain));

        var tooHigh = StrainSpliceDoc(FamilyExpansion.TierCount + 1);
        var ex = Assert.Throws<InvalidOperationException>(
            () => StrainSpliceTuning.Parse(tooHigh, sockets));
        Assert.Contains("above the atom ladder", ex.Message);
        Assert.Contains(FamilyExpansion.TierCount.ToString(), ex.Message);
    }

    static string StrainSpliceDoc(int topBaseTier)
    {
        var node = JsonNode.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.StrainSplice)))!.AsObject();
        node["recipe"]!["baseTier"]!["strain"] = topBaseTier;
        return node.ToJsonString();
    }
}
