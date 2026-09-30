using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Materials;
using FusionRpg.Core.Items.Mutation;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Items.Thresholds;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// species-gear-chain T34d-wire — the coordinator's own ruling (2026-09-20): "unwired is not done."
/// `ItemWorkbench.RecipeContextFor` populates `SpeciesRungIndex`/`BoundSpeciesId`/
/// `BoundSpeciesFamilies`/`TrophyStock` for a real species-bound piece, at the one site every real
/// Enhance/Promote/Repair/RerollOne/RerollAll/SocketImbue call already funnels through.
///
/// <para>This test drives a REAL workbench call end to end against a real owned, species-bound
/// instance — a real `RpgStore`, a real imported `SetDef` naming the container's own species, a real
/// `ItemWorkbench.Enhance` call — and asserts the resolved cost line names the real concrete trophy
/// id the bootstrap-independent, directly-configured trophy registry holds. The live-probe-standard's
/// own bar: a debug API proves nothing; this is the real path (`TryResolve` → `RecipeContextFor` →
/// `MaterialRecipeCatalog.Resolve` → `TrySpendAndApply`).</para>
///
/// <para>species-gear-chain T34d: the REAL shipped `materials.v6.json` now prices a `trophy` leg on
/// `temper` and `elevate` (the two improve verbs `CostClassMatrix.Allows` already permitted) — so this
/// reads the real file directly rather than mutating a synthetic copy. Only the recipe corpus entry
/// is still synthetic: no shipped recipe authors a trophy scope token yet (a separate, larger content
/// initiative — `recipegen` regeneration — outside this module's own Files list).</para>
/// </summary>
public class ItemWorkbenchSpeciesWiringTests : IDisposable
{
    const string Rung = "heirloom"; // AT the real shipped speciesCostThresholdRung -- the trophy leg must apply
    const string ContainerId = "item.species-wiring-test";
    const string RealSpecies = "abyssswordstar"; // perSpecies = 2, real registry row exists
    const string RecipeId = "recipe.species-wiring-test";

    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;
    readonly string _playerKey;
    string _instanceId = "";

    public ItemWorkbenchSpeciesWiringTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        _playerKey = _playerId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // species-gear-chain T34: the real committed trophy registry, configured directly (this test
        // assembly is a separate process from Core.Tests' own ContractTuningTestBootstrap).
        MaterialCatalog.ConfigureTrophyRegistry(
            MaterialCatalog.ParseTrophyRegistryIds(
                File.ReadAllText(Path.Combine(RepoRoot(), "data", "seed", "items", "materials", "trophy-registry.json"))));

        for (var i = 0; i < RarityLadder.RungIds.Count; i++)
            Assert.True(_store.UpsertRarity(new RarityRow(RarityLadder.RungIds[i], (i + 1) * 10, 3, 0, 1, 5)).Ok);

        SeedItem();
    }

    public void Dispose() => _testStore.Dispose();

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

    static string Tuning(string file) => File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", file));

    void SeedItem()
    {
        var upsert = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = ContainerId,
            Kind = ContainerKind.Item,
            Slot = ItemRoles.Id(ItemRole.ArmamentPrimary),
            Rarity = Rung,
            Atoms = Array.Empty<ContainerAtomRow>(),
        });
        Assert.True(upsert.IsOk, upsert.ToString());

        // species-gear-chain T28/T34d-wire: the container's OWN species binding, via a real imported
        // set naming it -- the exact join SetCorpus.SpeciesIdFor (ItemWorkbench's own delegate) reads.
        _store.ImportSetCorpus(new[]
        {
            new SetDef(
                "set.species-wiring-test", "Species Wiring Test Set",
                new[] { new SetMemberDef(ContainerId, ItemRole.ArmamentPrimary, ItemFrame.Humanoid) },
                Array.Empty<SetTierDef>(),
                SpeciesId: RealSpecies),
        });

        _instanceId = _store.SaveInstance(new InstanceRow
        {
            ContainerId = ContainerId,
            RollSeed = 4242,
            CatalogRevision = _store.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop,
            Atoms = Array.Empty<InstanceAtomRow>(),
        });

        Assert.True(_store.AcquireItem(new RpgItemRow
        {
            InstanceId = _instanceId,
            PlayerId = _playerKey,
            AcquiredUtc = "2026-09-06T00:00:00Z",
            OriginKind = "drop",
        }).IsOk);

        var rungOrdinal = _store.ListRarities().First(r => r.RarityId == Rung).Ordinal;
        _store.PersistLoot(
            _playerKey,
            new LootManifest("wiring-drop", "table.wiring", 7UL, 24, Array.Empty<LootGrant>(),
                Array.Empty<string>(), "{}", LootPityState.Empty, LootPityState.Empty, null, false, null),
            "test", "wiring", _store.GetCatalogRevision(), 1,
            new[]
            {
                new ItemGenerationRow(_instanceId, 0, "item.wiring-base", rungOrdinal, 24, "humanoid",
                    ItemRoles.Id(ItemRole.ArmamentPrimary), "drop"),
            });
    }

    /// <summary>species-gear-chain T34d: the REAL shipped `materials.v6.json` — it prices `temper`'s
    /// (and `elevate`'s) `trophy` leg for real now, so no mutation is needed any more.</summary>
    static MaterialTuning RealTuningWithTrophyLeg() => MaterialTuning.Parse(Tuning(SocketTuningFiles.Materials));

    static MaterialRecipeCatalog RecipesWithTrophyLeg() => MaterialRecipeCatalog.Load(new[]
    {
        $$"""
        {"entries": [{
            "id": "{{RecipeId}}", "operation": "temper", "outputKind": "mutation", "outputQty": 1,
            "frame": "any", "costLines": [{"material": "trophy.species.1", "costBand": "cheap"}]
        }]}
        """,
    }, RealTuningWithTrophyLeg());

    [Fact]
    public void A_real_enhance_call_on_a_real_species_bound_instance_resolves_the_real_concrete_trophy_id()
    {
        var recipes = RecipesWithTrophyLeg();
        var enhancement = EnhancementTuning.Parse(Tuning("enhancement.v1.json"));
        var sockets = SocketTuning.Parse(Tuning(SocketTuningFiles.Current));

        var bench = new ItemWorkbench(
            _store, recipes.Tuning, recipes, enhancement, sockets,
            // species-gear-chain T34d-wire: the three new delegates, wired for real against a real
            // imported SetDef and a fixed rung (any real per-species/per-family gate this fixture
            // does not need to exercise).
            speciesIdForContainer: containerId => SetCorpus.SpeciesIdFor(containerId, _store.ListSets()),
            speciesRungIndexFor: _ => RarityLadder.RungIndexOf(Rung));

        // A real owned balance -- TrophyStock is a real per-id RpgStore.GetMaterialQty read
        // (ItemWorkbench.RecipeContextFor), so the spend must find a real credited row, never an
        // assumed one.
        _store.GrantMaterials(_playerId,
            new[] { (MaterialCatalog.ComposeTrophyId("species", RealSpecies, 1), 1L) });

        var outcome = bench.Enhance(_playerId, _instanceId, RecipeId, "wiring-correlation-1");

        Assert.True(outcome.Ok, outcome.Reason);
        var line = Assert.Single(outcome.Spent, l => l.Class == nameof(MaterialClass.Trophy));
        Assert.Equal(MaterialCatalog.ComposeTrophyId("species", RealSpecies, 1), line.MaterialId);
        Assert.Equal(1, line.Qty);

        // And the debit really happened -- read the store back, never trust the DTO alone.
        Assert.Equal(0L, _store.GetMaterialQty(_playerId, line.MaterialId));
    }

    [Fact]
    public void A_species_less_instance_resolves_no_trophy_leg_and_costs_exactly_as_before()
    {
        // No SetDef import for this container -- SpeciesIdForContainer returns None, and the whole
        // trophy leg vanishes (Success criterion 2: species-less costs exactly as before).
        var otherContainerId = "item.species-wiring-test-none";
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = otherContainerId, Kind = ContainerKind.Item,
            Slot = ItemRoles.Id(ItemRole.ArmamentPrimary), Rarity = Rung,
            Atoms = Array.Empty<ContainerAtomRow>(),
        }).IsOk);
        var otherInstanceId = _store.SaveInstance(new InstanceRow
        {
            ContainerId = otherContainerId, RollSeed = 99,
            CatalogRevision = _store.GetCatalogRevision(), Origin = InstanceOrigin.Drop,
            Atoms = Array.Empty<InstanceAtomRow>(),
        });
        Assert.True(_store.AcquireItem(new RpgItemRow
        {
            InstanceId = otherInstanceId, PlayerId = _playerKey,
            AcquiredUtc = "2026-09-06T00:00:00Z", OriginKind = "drop",
        }).IsOk);
        var rungOrdinal = _store.ListRarities().First(r => r.RarityId == Rung).Ordinal;
        _store.PersistLoot(_playerKey,
            new LootManifest("wiring-drop-2", "table.wiring", 8UL, 24, Array.Empty<LootGrant>(),
                Array.Empty<string>(), "{}", LootPityState.Empty, LootPityState.Empty, null, false, null),
            "test", "wiring", _store.GetCatalogRevision(), 1,
            new[] { new ItemGenerationRow(otherInstanceId, 0, "item.wiring-base", rungOrdinal, 24,
                "humanoid", ItemRoles.Id(ItemRole.ArmamentPrimary), "drop") });

        var recipes = RecipesWithTrophyLeg();
        var enhancement = EnhancementTuning.Parse(Tuning("enhancement.v1.json"));
        var sockets = SocketTuning.Parse(Tuning(SocketTuningFiles.Current));
        var bench = new ItemWorkbench(
            _store, recipes.Tuning, recipes, enhancement, sockets,
            speciesIdForContainer: containerId => SetCorpus.SpeciesIdFor(containerId, _store.ListSets()),
            speciesRungIndexFor: _ => RarityLadder.RungIndexOf(Rung));

        var outcome = bench.Enhance(_playerId, otherInstanceId, RecipeId, "wiring-correlation-2");

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.DoesNotContain(outcome.Spent, l => l.Class == nameof(MaterialClass.Trophy));
    }

    [Fact]
    public void Two_sets_disagreeing_about_the_same_containers_species_refuse_by_name_rather_than_guess()
    {
        // The SAME container as the constructor's own set, but ALSO named by a second set naming a
        // DIFFERENT species -- SetCorpus.SpeciesIdFor sees two distinct SpeciesId values for one
        // container and must refuse rather than pick either (T34d-wire's own acceptance criterion).
        const string OtherSpecies = "bigpumpkin";
        _store.ImportSetCorpus(new[]
        {
            new SetDef(
                "set.species-wiring-test", "Species Wiring Test Set",
                new[] { new SetMemberDef(ContainerId, ItemRole.ArmamentPrimary, ItemFrame.Humanoid) },
                Array.Empty<SetTierDef>(),
                SpeciesId: RealSpecies),
            new SetDef(
                "set.species-wiring-test-conflict", "Species Wiring Conflict Set",
                new[] { new SetMemberDef(ContainerId, ItemRole.ArmamentPrimary, ItemFrame.Humanoid) },
                Array.Empty<SetTierDef>(),
                SpeciesId: OtherSpecies),
        });

        var recipes = RecipesWithTrophyLeg();
        var enhancement = EnhancementTuning.Parse(Tuning("enhancement.v1.json"));
        var sockets = SocketTuning.Parse(Tuning(SocketTuningFiles.Current));
        var bench = new ItemWorkbench(
            _store, recipes.Tuning, recipes, enhancement, sockets,
            speciesIdForContainer: containerId => SetCorpus.SpeciesIdFor(containerId, _store.ListSets()),
            speciesRungIndexFor: _ => RarityLadder.RungIndexOf(Rung));

        var outcome = bench.Enhance(_playerId, _instanceId, RecipeId, "wiring-correlation-3");

        Assert.False(outcome.Ok);
        Assert.Contains("item.species-ambiguous", outcome.Reason);
        Assert.Contains(ContainerId, outcome.Reason);
    }
}
