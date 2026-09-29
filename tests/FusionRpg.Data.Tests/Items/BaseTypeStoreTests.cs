using FusionRpg.Core.Items.Drops;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// `RpgStore.ImportBaseTypes`/`BaseTypeIdsFor`/`GetBaseType` (party-dungeon-todo.md D4.12, 2026-09-07)
/// — the `item_base_type` table `BuildLiveLootContentView`'s own `BaseTypesFor` now reads, closing the
/// last honest gap in that assembler.
/// </summary>
public class BaseTypeStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public BaseTypeStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static readonly BaseTypeSeedRow[] TwoRows =
    {
        new("item.plant-a-001", "plant", "armament-primary"),
        new("item.plant-a-002", "plant", "armament-primary"),
    };

    [Fact]
    public void ImportBaseTypes_null_rows_throws()
    {
        Assert.Throws<ArgumentNullException>(() => _store.ImportBaseTypes(null!));
    }

    [Fact]
    public void BaseTypeIdsFor_an_unimported_pair_returns_empty_not_null()
    {
        var ids = _store.BaseTypeIdsFor("plant", "armament-primary");
        Assert.NotNull(ids);
        Assert.Empty(ids);
    }

    [Fact]
    public void GetBaseType_an_unimported_id_returns_null()
    {
        Assert.Null(_store.GetBaseType("item.does-not-exist"));
    }

    [Fact]
    public void ImportBaseTypes_then_BaseTypeIdsFor_returns_every_id_for_that_pair_ordinal()
    {
        _store.ImportBaseTypes(TwoRows);

        var ids = _store.BaseTypeIdsFor("plant", "armament-primary");

        Assert.Equal(new[] { "item.plant-a-001", "item.plant-a-002" }, ids);
    }

    [Fact]
    public void BaseTypeIdsFor_never_crosses_frame_or_role_boundaries()
    {
        _store.ImportBaseTypes(new[]
        {
            new BaseTypeSeedRow("item.plant-a-001", "plant", "armament-primary"),
            new BaseTypeSeedRow("item.humanoid-a-001", "humanoid", "armament-primary"),
            new BaseTypeSeedRow("item.plant-b-001", "plant", "footing"),
        });

        Assert.Equal(new[] { "item.plant-a-001" }, _store.BaseTypeIdsFor("plant", "armament-primary"));
        Assert.Equal(new[] { "item.humanoid-a-001" }, _store.BaseTypeIdsFor("humanoid", "armament-primary"));
        Assert.Equal(new[] { "item.plant-b-001" }, _store.BaseTypeIdsFor("plant", "footing"));
        Assert.Empty(_store.BaseTypeIdsFor("humanoid", "footing"));
    }

    [Fact]
    public void GetBaseType_resolves_the_reverse_direction()
    {
        _store.ImportBaseTypes(TwoRows);

        var got = _store.GetBaseType("item.plant-a-002");

        Assert.NotNull(got);
        Assert.Equal("plant", got!.Value.Frame);
        Assert.Equal("armament-primary", got.Value.Role);
    }

    [Fact]
    public void ImportBaseTypes_is_a_whole_corpus_replace_not_an_accumulate()
    {
        _store.ImportBaseTypes(TwoRows);
        _store.ImportBaseTypes(new[] { new BaseTypeSeedRow("item.plant-c-001", "plant", "footing") });

        // The first import's own rows must be GONE, not merged -- a re-import mirrors the real corpus
        // on disk exactly, the same "delete then insert every row" contract ImportLootCorpus already has.
        Assert.Empty(_store.BaseTypeIdsFor("plant", "armament-primary"));
        Assert.Equal(new[] { "item.plant-c-001" }, _store.BaseTypeIdsFor("plant", "footing"));
    }
}
