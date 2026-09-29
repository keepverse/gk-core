using FusionRpg.Data;
using FusionRpg.Data.Sqlite.Migrations;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// seed-to-concrete T4.1 (spec-rarity-migration.md §4, §7 step 6): <see cref="ShardRungs"/> rewrites
/// owned legacy shard stacks (shard.common/rare/epic/legendary) to their live ten-rung ids
/// (shard.chaff/cultivated/heirloom/sunwoven) — this is the DAL migration's own acceptance bar,
/// stated in the spec as "no player ends the migration with fewer materials than they started with."
///
/// <see cref="RpgStore.Init"/> runs the migration on every boot (idempotent — see
/// <see cref="ShardRungs.Migrate"/>'s own doc comment), so these tests seed a legacy stack directly
/// through the real public write path (<see cref="RpgStore.AddCreatureMaterials"/>, which already accepts
/// legacy ids per <c>CreatureMaterialCatalog.IsKnown</c>) and then re-run <c>Init()</c> on the same store
/// to trigger the migration exactly the way a real boot would.
/// </summary>
[Trait("VerificationId", "data.shard-rungs")]
public class ShardRungsMigrationTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ShardRungsMigrationTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    [Fact]
    public void Legacy_shard_id_resolves_after_migration()
    {
        // "resolvable but unissuable" (spec §4 point 4) — CreatureMaterialCatalog.IsKnown must keep
        // accepting the legacy id even after every owned stack has been rewritten, so a stale
        // client's reference to it does not hard-fail.
        Assert.True(FusionRpg.Core.Creatures.CreatureMaterialCatalog.IsKnown("shard.common"));
        Assert.True(FusionRpg.Core.Creatures.CreatureMaterialCatalog.IsKnown("shard.rare"));
        Assert.True(FusionRpg.Core.Creatures.CreatureMaterialCatalog.IsKnown("shard.epic"));
        Assert.True(FusionRpg.Core.Creatures.CreatureMaterialCatalog.IsKnown("shard.legendary"));
        // ...but never issuable going forward.
        Assert.DoesNotContain("shard.common", FusionRpg.Core.Creatures.CreatureMaterialCatalog.All);
    }

    [Fact]
    public void Migration_never_reduces_a_player_material_count()
    {
        const long playerId = 1;
        _store.AddCreatureMaterials(playerId, new[] { ("shard.common", 7L) });
        _store.AddCreatureMaterials(playerId, new[] { ("shard.rare", 3L) });
        _store.AddCreatureMaterials(playerId, new[] { ("shard.epic", 5L) });
        _store.AddCreatureMaterials(playerId, new[] { ("shard.legendary", 2L) });
        _store.AddCreatureMaterials(playerId, new[] { ("essence.fire", 4L) });

        var before = _store.ListCreatureMaterials(playerId).Sum(m => m.Qty);

        // Re-running Init() on the SAME data dir is exactly what a real boot after the migration
        // ships does — the legacy rows above were seeded before this call, matching a save from
        // before the rename.
        _store.Init();

        var after = _store.ListCreatureMaterials(playerId).ToList();
        Assert.True(after.Sum(m => m.Qty) >= before,
            $"material count dropped across migration: before={before}, after={after.Sum(m => m.Qty)}");

        // The four legacy ids are gone from the OWNED set (qty zeroed, filtered by qty>0)...
        Assert.DoesNotContain(after, m => m.MaterialId is "shard.common" or "shard.rare" or "shard.epic" or "shard.legendary");
        // ...and landed on their live ids, each carrying the seeded quantity forward.
        Assert.Equal(7, after.Single(m => m.MaterialId == "shard.chaff").Qty);
        Assert.Equal(3, after.Single(m => m.MaterialId == "shard.cultivated").Qty);
        Assert.Equal(5, after.Single(m => m.MaterialId == "shard.heirloom").Qty);
        Assert.Equal(2, after.Single(m => m.MaterialId == "shard.sunwoven").Qty);
        Assert.Equal(4, after.Single(m => m.MaterialId == "essence.fire").Qty);
    }

    [Fact]
    public void Merging_stacks_sums_rather_than_overwrites()
    {
        const long playerId = 2;
        // The player already holds the LIVE id (post-migration mint or drop) AND the legacy id
        // (a pre-migration save) at once — the both-held case the spec names explicitly.
        _store.AddCreatureMaterials(playerId, new[] { ("shard.cultivated", 10L) });
        _store.AddCreatureMaterials(playerId, new[] { ("shard.rare", 6L) });

        _store.Init();

        var cultivated = _store.ListCreatureMaterials(playerId).Single(m => m.MaterialId == "shard.cultivated");
        Assert.Equal(16, cultivated.Qty); // summed, not overwritten by either side
        Assert.DoesNotContain(_store.ListCreatureMaterials(playerId), m => m.MaterialId == "shard.rare");
    }

    [Fact]
    public void Migration_is_idempotent_a_second_run_touches_nothing()
    {
        const long playerId = 3;
        _store.AddCreatureMaterials(playerId, new[] { ("shard.common", 9L) });
        _store.Init(); // first migration: shard.common -> shard.chaff

        var afterFirst = _store.ListCreatureMaterials(playerId).Sum(m => m.Qty);
        _store.Init(); // second run: no legacy rows remain, must be a no-op
        var afterSecond = _store.ListCreatureMaterials(playerId).Sum(m => m.Qty);

        Assert.Equal(afterFirst, afterSecond);
        Assert.Equal(9, _store.ListCreatureMaterials(playerId).Single(m => m.MaterialId == "shard.chaff").Qty);
    }

    [Fact]
    public void Every_legacy_id_maps_to_a_live_ten_rung_id()
    {
        Assert.Equal(4, ShardRungs.LegacyToLiveShardId.Count);
        foreach (var (legacy, live) in ShardRungs.LegacyToLiveShardId)
        {
            Assert.True(FusionRpg.Core.Creatures.CreatureMaterialCatalog.IsKnown(legacy));
            Assert.Contains(live, FusionRpg.Core.Creatures.CreatureMaterialCatalog.All);
        }
        Assert.Equal("shard.chaff", ShardRungs.LegacyToLiveShardId["shard.common"]);
        Assert.Equal("shard.cultivated", ShardRungs.LegacyToLiveShardId["shard.rare"]);
        Assert.Equal("shard.heirloom", ShardRungs.LegacyToLiveShardId["shard.epic"]);
        Assert.Equal("shard.sunwoven", ShardRungs.LegacyToLiveShardId["shard.legendary"]);
    }
}
