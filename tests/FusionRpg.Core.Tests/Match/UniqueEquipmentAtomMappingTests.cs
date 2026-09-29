using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests.Match;

/// <summary>
/// T6.1 (`mods-absorption`) — real, tested groundwork, honestly separated from the part that stays
/// blocked. This session found the earlier "no atom exists for these effect ids" conclusion was
/// wrong (`gk-data/packs/fusion/data/seed/atoms/fx-core.json`/`fx-status.json` already carry real atoms for
/// `fx.passive_atk_flat`/`fx.butter_on_hit`/`fx.shield_grant`/`fx.cold_on_hit` — `EffectAtomCatalog.
/// Generated.cs`'s own header already proves they round-trip through `AtomCompiler`), and built
/// `gk-data/packs/fusion/data/seed/containers/unique-equip.json` plus <see cref="UniqueEquipmentCatalog.TryGetAtomBackedContainerId"/>
/// to make that real. What stopped here, found only by actually attempting the write-path wiring:
/// no <see cref="OwnerKind"/> value fits a PERSISTENT `rpg_unique_actor` (`Entity` is explicitly
/// session-scoped and cleared on session end — using it would silently wipe equipment bindings every
/// session; `Player`/`Plant`/`Zombie`/`Sector`/`Slot`/`Match` don't fit either) — `OwnerScope.cs`'s
/// own doc comment calls these "the seven owner scopes a binding may attach to," a closed, reviewed
/// set, the same class of boundary as `CurveInput`'s own "Ask first: adding a curve input." Adding
/// an eighth is a real design decision this task does not make. This file proves what IS real today.
/// </summary>
public class UniqueEquipmentAtomMappingTests
{
    static string RepoRoot()
    {
        // Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/tests/Shared/KeepverseRoots.cs):
        // the content root, so every `gk-data/packs/fusion/data/seed` read below stays valid once content moves to the
        // gk-data pack. Hunting for that directory was a private, split-fragile root signal.
        return FusionRpg.TestSupport.ContentRoot.Path;
    }

    [Theory]
    [InlineData("stub.atk_ring", "item.fx-passive-atk-flat")]
    [InlineData("stub.butter_bead", "item.fx-butter-on-hit")]
    [InlineData("relic.ashen_reliquary", "item.fx-passive-atk-flat")] // shares atk_ring's own EffectId
    [InlineData("relic.sunworn_charm", "item.fx-shield-grant")]
    [InlineData("relic.tidewrack_band", "item.fx-cold-on-hit")]
    // 2026-09-06: fx.entity_atk (a placeholder with no defined effect) migrated to a deliberately
    // EMPTY atom-backed container — closes T6.1's "never both paths" gap for the last two legacy
    // items WITHOUT inventing a real effect or a balance number; the placeholder's own no-op stays
    // exactly as it was, only the delivery mechanism moved.
    [InlineData("stub.hp_charm", "item.fx-entity-atk")]
    [InlineData("relic.cracked_seal", "item.fx-entity-atk")] // shares hp_charm's own EffectId
    public void Items_with_a_real_atom_resolve_to_the_real_container_id(string itemId, string expectedContainerId)
    {
        Assert.True(UniqueEquipmentCatalog.TryGetAtomBackedContainerId(itemId, out var containerId));
        Assert.Equal(expectedContainerId, containerId);
    }

    [Fact]
    public void An_unknown_item_id_is_never_atom_backed()
    {
        Assert.False(UniqueEquipmentCatalog.TryGetAtomBackedContainerId("not-a-real-item", out _));
    }

    [Fact]
    public void The_new_container_seed_file_parses_and_every_atom_it_references_is_present_in_the_real_atom_seed()
    {
        // Real end to end: read gk-data/packs/fusion/data/seed/containers/unique-equip.json through the SAME
        // AtomSeedFile.Collect entry point AtomImporter itself uses, alongside the real
        // fx-core.json/fx-status.json atom seed files — proves the four containers this task
        // added actually resolve against real, already-shipped atom content, not an invented id.
        var root = RepoRoot();
        var files = new (string Path, string Json)[]
        {
            (Path.Combine(root, "data", "seed", "atoms", "fx-core.json"),
                File.ReadAllText(Path.Combine(root, "data", "seed", "atoms", "fx-core.json"))),
            (Path.Combine(root, "data", "seed", "atoms", "fx-status.json"),
                File.ReadAllText(Path.Combine(root, "data", "seed", "atoms", "fx-status.json"))),
            (Path.Combine(root, "data", "seed", "containers", "unique-equip.json"),
                File.ReadAllText(Path.Combine(root, "data", "seed", "containers", "unique-equip.json"))),
        };

        var result = AtomSeedFile.Collect(files);

        Assert.True(result.IsOk, string.Join("; ", result.Errors));
        var containerIds = result.Content.Containers.Select(c => c.ContainerId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("item.fx-passive-atk-flat", containerIds);
        Assert.Contains("item.fx-butter-on-hit", containerIds);
        Assert.Contains("item.fx-shield-grant", containerIds);
        Assert.Contains("item.fx-cold-on-hit", containerIds);
        Assert.Contains("item.fx-entity-atk", containerIds);

        var atomIds = result.Content.Atoms.Select(a => a.AtomId).ToHashSet(StringComparer.Ordinal);
        var shieldGrant = result.Content.Containers.Single(c => c.ContainerId == "item.fx-shield-grant");
        Assert.Equal(3, shieldGrant.Atoms.Count); // the a/b/c coordinated bundle, all three present
        var entityAtk = result.Content.Containers.Single(c => c.ContainerId == "item.fx-entity-atk");
        Assert.Empty(entityAtk.Atoms); // the placeholder's real no-op, preserved deliberately
        foreach (var containerId in containerIds)
        {
            var container = result.Content.Containers.Single(c => c.ContainerId == containerId);
            Assert.All(container.Atoms, a => Assert.Contains(a.AtomId, atomIds));
        }
    }
}
