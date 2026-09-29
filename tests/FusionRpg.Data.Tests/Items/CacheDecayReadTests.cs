using System.Linq;
using FusionRpg.Core.World;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>NS6.1 (notification-ssot spec cache-notify-source §1) — two read-only methods on
/// `RpgStore.CacheDecay.cs`: `ListCacheClocksStarted` and `ListCacheDecayTicks`. In-memory stores
/// only (`DataTestStore.Create()`), matching `CacheDecayTests.cs`'s own convention.</summary>
public class CacheDecayReadTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;
    const ulong WorldSeed = 11;
    const string WorldId = "w-cachedecay-read";

    public CacheDecayReadTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        Assert.True(_store.CreateWorld(
            _playerId, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, WorldSeed, WorldId)).Ok);
    }

    public void Dispose() => _testStore.Dispose();

    /// <summary>`TryStartDecayClockUnlocked` (called by `ResolveOrCreateCacheUnlocked`) stamps
    /// `decay_started_turn` from the owner's MAP world's OWN `current_turn` column at call time —
    /// so to pin an exact started-turn, this test bumps that column directly first (the same
    /// raw-SQL-for-setup convention `CacheDecayTests.cs`'s own `ReadClock`/`InsertStackItem`
    /// helpers already use; the behaviour under test is the two new read methods, not turn
    /// advancement).</summary>
    void SetCurrentTurn(long ownerPlayerId, int turn)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE rpg_worlds SET current_turn = $t WHERE player_id = $p AND kind = 'map';";
        cmd.Parameters.AddWithValue("$t", turn);
        cmd.Parameters.AddWithValue("$p", ownerPlayerId);
        cmd.ExecuteNonQuery();
    }

    string CreateCacheStartedAt(long ownerPlayerId, int turn, string placeRef)
    {
        SetCurrentTurn(ownerPlayerId, turn);
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        return _store.ResolveOrCreateCacheUnlocked(db, null, "lawn", placeRef, "death", ownerPlayerId);
    }

    void InsertStackItem(string cacheId, int seq, long qty = 1)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_corpse_cache_item (cache_id, seq, kind, instance_id, container_id, qty, origin_owner)
            VALUES ($c, $s, 'stack', NULL, 'item.cachedecay-read-test', $q, 'test');
            """;
        cmd.Parameters.AddWithValue("$c", cacheId);
        cmd.Parameters.AddWithValue("$s", seq);
        cmd.Parameters.AddWithValue("$q", qty);
        cmd.ExecuteNonQuery();
    }

    int Tick(long ownerPlayerId, int turn)
    {
        SetCurrentTurn(ownerPlayerId, turn);
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        return _store.TickCorpseCacheDecayForPlayerUnlocked(
            db, null, ownerPlayerId, turn, WorldSeed, "2026-09-19T00:00:00+00:00");
    }

    [Fact]
    public void ListCacheClocksStarted_returns_exactly_the_inclusive_range_for_this_owner()
    {
        var early = CreateCacheStartedAt(_playerId, 4, "lawn-early");
        var atFrom = CreateCacheStartedAt(_playerId, 5, "lawn-at-from");
        var atTo = CreateCacheStartedAt(_playerId, 6, "lawn-at-to");
        var late = CreateCacheStartedAt(_playerId, 7, "lawn-late");

        var rows = _store.ListCacheClocksStarted(_playerId, 5, 6);

        var ids = rows.Select(r => r.CacheId).ToArray();
        Assert.Equal(new[] { atFrom, atTo }, ids);
        Assert.DoesNotContain(early, ids);
        Assert.DoesNotContain(late, ids);
        Assert.All(rows, r => Assert.InRange(r.DecayStartedTurn, 5, 6));
        Assert.All(rows, r => Assert.Equal("lawn", r.PlaceKind));
        Assert.All(rows, r => Assert.Equal("death", r.SourceKind));
        Assert.All(rows, r => Assert.Equal(0, r.ItemCount)); // no items inserted for this test
    }

    [Fact]
    public void ListCacheClocksStarted_reports_the_real_item_count()
    {
        var cacheId = CreateCacheStartedAt(_playerId, 5, "lawn-counted");
        InsertStackItem(cacheId, 0);
        InsertStackItem(cacheId, 1);

        var row = Assert.Single(_store.ListCacheClocksStarted(_playerId, 5, 5));

        Assert.Equal(2, row.ItemCount);
        Assert.Equal("lawn-counted", row.PlaceRef);
    }

    [Fact]
    public void ListCacheClocksStarted_never_returns_another_owners_cache()
    {
        var mine = CreateCacheStartedAt(_playerId, 5, "lawn-mine");

        var otherId = _store.CreatePlayer("other").Id;
        Assert.True(_store.CreateWorld(
            otherId, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, WorldSeed, "w-other")).Ok);
        var theirs = CreateCacheStartedAt(otherId, 5, "lawn-theirs");

        var rows = _store.ListCacheClocksStarted(_playerId, 5, 5);

        Assert.Equal(new[] { mine }, rows.Select(r => r.CacheId).ToArray());
        Assert.DoesNotContain(rows, r => r.CacheId == theirs);
    }

    [Fact]
    public void ListCacheDecayTicks_returns_exactly_the_inclusive_range_and_reconciles_destroyed_and_survived_with_outcomes_json()
    {
        var cacheId = CreateCacheStartedAt(_playerId, 1, "lawn-ticks");
        // Several stack items so a real seeded roll almost certainly splits destroyed/survived
        // across enough ticks; the read method's counts are asserted against a raw re-parse of
        // outcomes_json, not against an assumed split, so the test is correct either way.
        for (var seq = 0; seq < 12; seq++) InsertStackItem(cacheId, seq);

        Tick(_playerId, 4);
        Tick(_playerId, 5);
        Tick(_playerId, 6);
        Tick(_playerId, 9); // outside the [5,6] range this test queries

        var rows = _store.ListCacheDecayTicks(_playerId, 5, 6);

        Assert.Equal(new[] { 5, 6 }, rows.Select(r => r.Tick).ToArray());
        Assert.All(rows, r => Assert.Equal(cacheId, r.CacheId));
        Assert.All(rows, r => Assert.Equal("lawn", r.PlaceKind));
        Assert.All(rows, r => Assert.Equal("lawn-ticks", r.PlaceRef));

        foreach (var row in rows)
        {
            var raw = RawOutcomesJson(cacheId, row.Tick);
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var destroyed = 0;
            var survived = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var outcome = el.GetProperty("outcome").GetString();
                if (outcome == "destroyed") destroyed++;
                else if (outcome == "survived") survived++;
            }
            Assert.Equal(destroyed, row.Destroyed);
            Assert.Equal(survived, row.Remaining);
            Assert.Equal(destroyed + survived, row.Destroyed + row.Remaining); // every item is one or the other
        }
    }

    [Fact]
    public void ListCacheDecayTicks_never_returns_another_owners_tick()
    {
        var mineCache = CreateCacheStartedAt(_playerId, 1, "lawn-mine-ticks");
        InsertStackItem(mineCache, 0);
        Tick(_playerId, 5);

        var otherId = _store.CreatePlayer("other").Id;
        Assert.True(_store.CreateWorld(
            otherId, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, WorldSeed, "w-other-ticks")).Ok);
        var theirCache = CreateCacheStartedAt(otherId, 1, "lawn-their-ticks");
        InsertStackItem(theirCache, 0);
        Tick(otherId, 5);

        var rows = _store.ListCacheDecayTicks(_playerId, 5, 5);

        Assert.Single(rows);
        Assert.Equal(mineCache, rows[0].CacheId);
    }

    string RawOutcomesJson(string cacheId, int tick)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT outcomes_json FROM rpg_corpse_cache_decay_log WHERE cache_id = $c AND tick = $t;";
        cmd.Parameters.AddWithValue("$c", cacheId);
        cmd.Parameters.AddWithValue("$t", tick);
        return (string)cmd.ExecuteScalar()!;
    }
}
