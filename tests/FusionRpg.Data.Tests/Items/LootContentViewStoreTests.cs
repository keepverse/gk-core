using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// `RpgStore.BuildLiveLootContentView` (party-dungeon-todo.md D4.12/D3.11/D3.3, 2026-09-07) — the real
/// `LootContentView` assembler, confirmed by `grep` to have never existed anywhere for any caller of
/// `LootPipeline.Resolve`. Reuses `DropTableStoreTests.cs`'s own established real-corpus fixture shape
/// for `Sources`/`Tables` (the real shipped `data/seed/loot/tables*.json`, imported through
/// `ImportLootCorpus`) and `RarityBandsStoreTests.cs`'s own hand-built-rung shape for the rarity ladder
/// (no need for the full real content-import bootstrap just to prove this assembler's own composition).
/// </summary>
public class LootContentViewStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public LootContentViewStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static DropVolumeTuning Tuning() => DropVolumeTuning.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "item-drop-volume.v1.json")));

    static LootCorpus ShippedCorpus() => LootCorpusReader.Merge(
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "data", "seed", "loot"), "tables*.json")
            .Select(p => LootCorpusReader.Parse(File.ReadAllText(p))));

    void SeedOneRarityRung(string rarityId, int ordinal, int dropWeight)
    {
        Assert.True(_store.UpsertRarity(new RarityRow(rarityId, ordinal, 0, 1, 1, 1)).Ok);
        _store.SetRarityBudget(rarityId, "drop_weight_default", dropWeight);
    }

    [Fact]
    public void BuildLiveLootContentView_composes_the_real_shipped_corpus_correctly()
    {
        var corpus = ShippedCorpus();
        _store.ImportLootCorpus(corpus, Tuning());
        SeedOneRarityRung("chaff", 10, 60_000);
        SeedOneRarityRung("sprout", 20, 40_000);

        var view = _store.BuildLiveLootContentView();

        Assert.Equal(corpus.Sources.Count, view.Sources.Count);
        Assert.Equal(corpus.Tables.Count, view.Tables.Count);
        Assert.Equal(2, view.Ladder.Count);
        var chaff = view.Ladder.Single(r => r.RarityId == "chaff");
        Assert.Equal(10, chaff.Ordinal);
        Assert.Equal(60_000, chaff.DropWeightPer100k);

        // Sources/Tables are keyed correctly (not just correctly counted).
        var oneSource = corpus.Sources.First();
        Assert.True(view.Sources.ContainsKey(oneSource.Key));
        var oneTable = corpus.Tables.First();
        Assert.True(view.Tables.ContainsKey(oneTable.TableId));
    }

    [Fact]
    public void BuildLiveLootContentView_throws_a_named_exception_if_a_real_rarity_has_no_budget_row()
    {
        // A rarity row exists (ladder-shape), but SetRarityBudget was never called for it -- the
        // real production symptom of "item-rarity.v{n}.json import never ran".
        Assert.True(_store.UpsertRarity(new RarityRow("chaff", 10, 0, 1, 1, 1)).Ok);

        var ex = Assert.Throws<InvalidOperationException>(() => _store.BuildLiveLootContentView());
        Assert.Contains("chaff", ex.Message);
        Assert.Contains("drop_weight_default", ex.Message);
    }

    [Fact]
    public void BuildLiveLootContentView_BaseTypesFor_reads_the_real_item_base_type_table()
    {
        SeedOneRarityRung("chaff", 10, 100_000);
        _store.ImportBaseTypes(new[]
        {
            new BaseTypeSeedRow("item.plant-a-001", "plant", "armament-primary"),
            new BaseTypeSeedRow("item.plant-a-002", "plant", "armament-primary"),
            new BaseTypeSeedRow("item.humanoid-a-001", "humanoid", "armament-primary"),
        });

        var view = _store.BuildLiveLootContentView();

        Assert.Equal(new[] { "item.plant-a-001", "item.plant-a-002" }, view.BaseTypesFor("plant", "armament-primary"));
        Assert.Empty(view.BaseTypesFor("plant", "footing"));
    }

    [Fact]
    public void BuildLiveLootContentView_wires_FirstClearAlreadyGranted_to_the_real_store()
    {
        SeedOneRarityRung("chaff", 10, 100_000);
        var view = _store.BuildLiveLootContentView();

        Assert.NotNull(view.FirstClearAlreadyGranted);
        Assert.False(view.FirstClearAlreadyGranted!("p1", "dungeon-clear", "domain.fire-001"));

        _store.PersistLoot("p1",
            new LootManifest("c1", "drop.dungeon.fire.boss", 1, 1, Array.Empty<LootGrant>(), Array.Empty<string>(),
                "{}", new LootPityState(0, 0), new LootPityState(0, 0), "domain.fire-001", false, null),
            "dungeon-clear", "domain.fire-001", 0, 0, Array.Empty<ItemGenerationRow>());

        Assert.True(view.FirstClearAlreadyGranted!("p1", "dungeon-clear", "domain.fire-001"));
    }

    [Fact]
    public void BuildLiveLootContentView_wires_RecordedManifestFor_to_the_real_store()
    {
        SeedOneRarityRung("chaff", 10, 100_000);
        var view = _store.BuildLiveLootContentView();

        Assert.Null(view.RecordedManifestFor!("p1", "c1"));

        _store.PersistLoot("p1",
            new LootManifest("c1", "drop.dungeon.fire.boss", 1, 1, Array.Empty<LootGrant>(), Array.Empty<string>(),
                "{}", new LootPityState(0, 0), new LootPityState(0, 0), null, false, null),
            "dungeon-clear", "domain.fire-001", 0, 0, Array.Empty<ItemGenerationRow>());

        Assert.NotNull(view.RecordedManifestFor!("p1", "c1"));
    }

    [Fact]
    public void BuildLiveLootContentView_wires_UniqueRarityFor_to_the_real_container_store()
    {
        SeedOneRarityRung("chaff", 10, 100_000);
        var upsert = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.test-unique",
            Kind = ContainerKind.Item,
            Rarity = "chaff",
            Atoms = Array.Empty<ContainerAtomRow>(),
        });
        Assert.True(upsert.IsOk, upsert.ToString());

        var view = _store.BuildLiveLootContentView();

        Assert.Equal("chaff", view.UniqueRarityFor!("item.test-unique"));
        Assert.Null(view.UniqueRarityFor!("item.does-not-exist"));
    }
}
