using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// T5.1 (`world-seed`, `spec-world-seed.md`) — the DAL half: rolled once at player creation, never
/// regenerated, and every legacy row (pre-dating this column) is backfilled with a real, distinct
/// value rather than left at the 0 sentinel.
/// </summary>
public class WorldSeedStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WorldSeedStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    [Fact]
    public void A_new_player_gets_a_real_nonzero_world_seed_at_creation()
    {
        var player = _store.CreatePlayer("Alice");

        Assert.NotEqual(0, player.WorldSeed);
    }

    [Fact]
    public void Two_players_created_in_the_same_run_get_different_world_seeds()
    {
        var a = _store.CreatePlayer("Alice");
        var b = _store.CreatePlayer("Bob");

        Assert.NotEqual(a.WorldSeed, b.WorldSeed);
    }

    [Fact]
    public void World_seed_is_created_once_and_never_regenerated_on_reload()
    {
        var created = _store.CreatePlayer("Alice");

        var reloaded = _store.ListPlayers().Single(p => p.Id == created.Id);

        Assert.Equal(created.WorldSeed, reloaded.WorldSeed);
    }

    [Fact]
    public void The_default_seeded_player_from_a_fresh_database_already_has_a_real_world_seed()
    {
        // SeedPlayerIfEmpty's own direct INSERT (Init's own bootstrap path, distinct from
        // CreatePlayer) must not leave player 1 at the 0 sentinel — BackfillWorldSeedsUnlocked's
        // whole reason to run inside Init() itself, not just as a lazy per-call fix.
        var player = _store.GetCurrentPlayer();

        Assert.NotNull(player);
        Assert.NotEqual(0, player!.WorldSeed);
    }

    [Fact]
    public void Reinitializing_the_store_never_changes_an_already_assigned_world_seed()
    {
        var created = _store.CreatePlayer("Alice");
        var before = created.WorldSeed;

        // A second Init() call (a real restart) must never touch a real, already-assigned seed.
        _store.Init();

        var after = _store.ListPlayers().Single(p => p.Id == created.Id).WorldSeed;
        Assert.Equal(before, after);
    }

    [Fact]
    public void The_derived_roll_seed_reproduces_from_the_stored_world_seed_and_a_catalog_revision_alone()
    {
        // §3.6's own reproducibility property, against a REAL stored player row rather than a
        // hand-typed constant.
        var player = _store.CreatePlayer("Alice");
        var revision = _store.GetCatalogRevision();
        var targetId = $"species-passive.conezombie@{revision}";

        var first = WorldSeed.DeriveRollSeed(player.WorldSeed, "affix.draw", targetId);
        var reloaded = _store.ListPlayers().Single(p => p.Id == player.Id);
        var second = WorldSeed.DeriveRollSeed(reloaded.WorldSeed, "affix.draw", targetId);

        Assert.Equal(first, second);
    }
}
