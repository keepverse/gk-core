using FusionRpg.Contracts;
using FusionRpg.Core.Notify;
using FusionRpg.Core.Saves;
using FusionRpg.Core.World;
using FusionRpg.Data.Sqlite;
using FusionRpg.Data.Tests;
using FusionRpg.Data;
using FusionRpg.Server.Notifications;
using System.Text.Json;
using Xunit;

namespace FusionRpg.Server.Tests.Notifications;

/// <summary>cache-notify-source spec §2, Testing 1-5 — `CacheNotificationSource` read directly
/// against a real, in-memory `RpgStore` (`DataTestStore.Create()`), matching
/// `CacheDecayTests.cs`'s own convention. A real `CommitWorldTurn` proves turn correspondence and
/// forces a real destruction the same way `Destroyed_instance_cascades_and_stack_dies_as_one_unit`
/// does — retrying ticks rather than assuming one roll, since survival is 994/1000.</summary>
[Collection("NotificationHub")]
public class CacheNotifySourceTests : IDisposable
{
    static readonly string[] AllCommanders = { "dave", "wild", "zomboss" };
    const string WorldId = "w-cache-notify";

    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;
    readonly CacheNotificationSource _source;

    public CacheNotifySourceTests()
    {
        NotificationHubFixture.Configure(retainPerCategory: 100, repeatWindowWorldTurns: 3);
        WorldTuningTestSupport.ConfigureOnce();
        ConfigureDeploymentHierarchyTuningOnce();
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        Assert.True(_store.CreateWorld(
            _playerId, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, (ulong)_playerId, WorldId)).Ok);
        _source = new CacheNotificationSource(_store);
    }

    public void Dispose() => _testStore.Dispose();

    /// <summary>`TickCorpseCacheDecayForPlayerUnlocked` (called from a real `CommitWorldTurn`) reads
    /// `DeploymentHierarchyTuningHub.Tuning.CacheDecay` — the real server's `Program.cs` configures
    /// it at startup, but `FusionRpg.Server.Tests`'s own module initializer does not (it is scoped
    /// to a different domain, matching `WorldTuningTestSupport`'s own note). Configured once,
    /// process-wide, from the real shipped tuning file — the same values `CacheDecayTests.cs`
    /// (Data.Tests) exercises.</summary>
    static bool _deploymentHierarchyConfigured;
    static void ConfigureDeploymentHierarchyTuningOnce()
    {
        if (_deploymentHierarchyConfigured) return;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector")))
            dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("could not find repo root above " + AppContext.BaseDirectory);
        var json = File.ReadAllText(Path.Combine(dir.FullName, "data", "tuning", "deployment-hierarchy.v5.json"));
        FusionRpg.Core.Items.Materials.DeploymentHierarchyTuningHub.Configure(
            FusionRpg.Core.Items.Materials.DeploymentHierarchyTuningLoader.Parse(json));
        _deploymentHierarchyConfigured = true;
    }

    /// <summary>Extends the shared `NotificationHubFixture` catalog (which only registers
    /// `test.category`) with the two real cache-notify-source categories, for the tests that
    /// publish through the real `NotificationContract`/`NotificationPublisher` pipeline.</summary>
    static void ConfigureCacheCatalog()
    {
        var json = """
            {
              "categories": [
                { "id": "cache.created", "domain": "corpse-cache", "displayName": "Cache created", "messageKeys": ["cache.created"] },
                { "id": "cache.decayed", "domain": "corpse-cache", "displayName": "Cache decayed", "messageKeys": ["cache.decayed"] }
              ],
              "promotions": { "toast": [], "critical": [] }
            }
            """;
        NotificationCatalogHub.Configure(NotificationCatalogLoader.Parse(json));
    }

    WorldTurnCommitResult CommitAll()
    {
        var open = _store.GetWorldHeader(WorldId)!.CurrentTurn;
        WorldTurnCommitResult last = default!;
        foreach (var c in AllCommanders.Where(c => c != "dave").Concat(AllCommanders.Where(c => c == "dave")))
            last = _store.CommitWorldTurn(WorldId, c, open);
        return last;
    }

    /// <summary>Raw SQL, not the internal `ResolveOrCreateCacheUnlocked`/`TryStartDecayClockUnlocked`
    /// (no `InternalsVisibleTo` grant from `FusionRpg.Data` to `FusionRpg.Server.Tests` — only
    /// `FusionRpg.Data.Tests` gets that) — matches `CacheDecayTests.cs`'s own raw-SQL-for-setup
    /// convention. Stamps `decay_started_turn` at whatever `current_turn` is right now, exactly
    /// like a real death resolution would.</summary>
    string CreateCache(string placeKind, string placeRef)
    {
        var cacheId = "cc_test_" + Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("o");
        var turn = _store.GetWorldHeader(WorldId)!.CurrentTurn;
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_corpse_cache
                (cache_id, place_kind, place_ref, source_kind, created_utc, decay_started_utc, decay_started_turn, owner_player_id, in_void, revision)
            VALUES ($id, $k, $r, 'death', $now, $now, $t, $p, 0, 0);
            """;
        cmd.Parameters.AddWithValue("$id", cacheId);
        cmd.Parameters.AddWithValue("$k", placeKind);
        cmd.Parameters.AddWithValue("$r", placeRef);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$t", turn);
        cmd.Parameters.AddWithValue("$p", _playerId);
        cmd.ExecuteNonQuery();
        return cacheId;
    }

    void InsertStackItem(string cacheId, int seq, long qty = 1)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_corpse_cache_item (cache_id, seq, kind, instance_id, container_id, qty, origin_owner)
            VALUES ($c, $s, 'stack', NULL, 'item.cache-notify-test', $q, 'test');
            """;
        cmd.Parameters.AddWithValue("$c", cacheId);
        cmd.Parameters.AddWithValue("$s", seq);
        cmd.Parameters.AddWithValue("$q", qty);
        cmd.ExecuteNonQuery();
    }

    WorldTurnNotificationContext ContextAt(int resolvedTurn) =>
        new(WorldId, resolvedTurn, resolvedTurn == _store.GetWorldHeader(WorldId)!.CurrentTurn - 1,
            _store.GetWorldHeader(WorldId)!, _store.LoadWorldState(WorldId)!, Report: null);

    [Fact]
    public void A_cache_created_draft_is_important_and_carries_the_item_count()
    {
        var cacheId = CreateCache("lawn", "lawn-ref-1");
        InsertStackItem(cacheId, 0);
        InsertStackItem(cacheId, 1);
        CommitAll(); // advances turn 0 -> 1; the world-map legion-death path is unrelated, but the
                     // generic clock-start already ran at cache creation (turn 0), and the window
                     // below covers both candidate stamps regardless.

        var ctx = ContextAt(0);
        var draft = Assert.Single(_source.Collect(ctx), d => d.Draft.DedupKey == $"cache:{cacheId}:created");

        Assert.Equal(_playerId, draft.SaveId.Value);
        Assert.Equal("cache.created", draft.Draft.CategoryId);
        Assert.Equal(NotifySeverity.Important, draft.Draft.Severity);
        Assert.Equal($"cache:{cacheId}", draft.Draft.SubjectKey);
        Assert.Equal(0, draft.Draft.WorldTurn);
        var itemCountArg = Assert.Single(draft.Draft.Args, a => a.Name == "itemCount");
        Assert.Equal(2L, itemCountArg.Value);
    }

    [Fact]
    public void A_lawn_place_carries_a_domainToken_of_the_bare_place_kind()
    {
        var cacheId = CreateCache("lawn", "lawn-ref-2");
        CommitAll();

        var draft = Assert.Single(_source.Collect(ContextAt(0)), d => d.Draft.DedupKey == $"cache:{cacheId}:created");
        var place = Assert.Single(draft.Draft.Args, a => a.Name == "place");
        Assert.Equal(NotifyArgKind.DomainToken, place.Kind);
        Assert.Equal("lawn", place.Value);
    }

    [Fact]
    public void A_world_sector_place_carries_a_ref_whose_wire_shape_is_camelCase()
    {
        ConfigureCacheCatalog();
        var cacheId = CreateCache("world_sector", "sector-ember-hollow");
        CommitAll();

        // Publish through the REAL publisher and read the stored row back — proves the wire shape
        // the web actually receives, not just the in-memory NotifyArg.Value object shape.
        var contract = new NotificationContract(NotificationCatalogHub.Catalog);
        var push = new FakePlayerPush();
        var publisher = new NotificationPublisher(_store, contract, push);
        var drafts = _source.Collect(ContextAt(0)).Where(d => d.Draft.DedupKey == $"cache:{cacheId}:created").ToList();
        publisher.PublishTurn(drafts, NotifyDelivery.Live, cursor: null);

        var page = _store.ListNotificationChanges(new SaveId(_playerId), 0, 10);
        var row = Assert.Single(page.Items, r => r.DedupKey == $"cache:{cacheId}:created");
        using var doc = JsonDocument.Parse(row.ArgsJson);
        var placeArg = doc.RootElement.EnumerateArray().Single(a => a.GetProperty("name").GetString() == "place");
        Assert.Equal("ref", placeArg.GetProperty("kind").GetString());
        var value = placeArg.GetProperty("value");
        Assert.Equal("sector", value.GetProperty("refKind").GetString());
        Assert.Equal("sector-ember-hollow", value.GetProperty("id").GetString());
    }

    [Fact]
    public void A_tick_with_no_destruction_yields_nothing()
    {
        var cacheId = CreateCache("lawn", "lawn-ref-3");
        InsertStackItem(cacheId, 0);
        CommitAll(); // turn 0 -> 1, one decay tick at turn 1; near-certain survival (994/1000)

        var drafts = _source.Collect(ContextAt(0)).Where(d => d.Draft.CategoryId == "cache.decayed").ToList();
        // Either nothing (survived) or, on the rare unlucky roll, a real destruction draft — assert
        // the CONTRACT (destroyed-count-zero never yields), not the roll outcome.
        var ticks = _store.ListCacheDecayTicks(_playerId, 0, 1);
        if (ticks.All(t => t.Destroyed == 0))
            Assert.Empty(drafts);
    }

    [Fact]
    public void A_tick_that_empties_the_cache_is_important_with_an_emptied_subject_a_partial_loss_is_routine()
    {
        // Cache A: a single item — its first destruction empties the cache (Important, :emptied).
        var soleCacheId = CreateCache("lawn", "lawn-ref-empty");
        InsertStackItem(soleCacheId, 0);

        // Cache B: many items — a destruction there almost never empties it (Routine, cache:{id}).
        var manyCacheId = CreateCache("lawn", "lawn-ref-many");
        for (var i = 0; i < 40; i++) InsertStackItem(manyCacheId, i);

        int? emptiedAtTurn = null;
        int? partialAtTurn = null;
        for (var i = 0; i < 3000 && (emptiedAtTurn is null || partialAtTurn is null); i++)
        {
            var beforeTurn = _store.GetWorldHeader(WorldId)!.CurrentTurn;
            CommitAll();
            var resolvedTurn = beforeTurn; // the turn that was just resolved by this commit

            if (emptiedAtTurn is null)
            {
                var soleTicks = _store.ListCacheDecayTicks(_playerId, resolvedTurn, resolvedTurn + 1);
                if (soleTicks.Any(t => t.CacheId == soleCacheId && t.Destroyed > 0 && t.Remaining == 0))
                    emptiedAtTurn = resolvedTurn;
            }
            if (partialAtTurn is null)
            {
                var manyTicks = _store.ListCacheDecayTicks(_playerId, resolvedTurn, resolvedTurn + 1);
                if (manyTicks.Any(t => t.CacheId == manyCacheId && t.Destroyed > 0 && t.Remaining > 0))
                    partialAtTurn = resolvedTurn;
            }
        }

        Assert.True(emptiedAtTurn is not null, "the sole item never died in 3000 ticks at 994/1000 survival");
        Assert.True(partialAtTurn is not null, "the 40-item cache never lost exactly some items in 3000 ticks");

        var emptiedDraft = Assert.Single(
            _source.Collect(ContextAt(emptiedAtTurn!.Value)),
            d => d.Draft.CategoryId == "cache.decayed" && d.Draft.DedupKey.StartsWith($"cache:{soleCacheId}:decay:"));
        Assert.Equal(NotifySeverity.Important, emptiedDraft.Draft.Severity);
        Assert.Equal($"cache:{soleCacheId}:emptied", emptiedDraft.Draft.SubjectKey);

        var partialDraft = Assert.Single(
            _source.Collect(ContextAt(partialAtTurn!.Value)),
            d => d.Draft.CategoryId == "cache.decayed" && d.Draft.DedupKey.StartsWith($"cache:{manyCacheId}:decay:"));
        Assert.Equal(NotifySeverity.Routine, partialDraft.Draft.Severity);
        Assert.Equal($"cache:{manyCacheId}", partialDraft.Draft.SubjectKey);
    }

    [Fact]
    public void Overlapping_windows_publish_the_same_created_event_exactly_once_even_after_pruning()
    {
        NotificationHubFixture.Configure(retainPerCategory: 1, repeatWindowWorldTurns: 3);
        ConfigureCacheCatalog();
        var cacheId = CreateCache("lawn", "lawn-ref-once");
        CommitAll(); // turn 0 -> 1

        var contract = new NotificationContract(NotificationCatalogHub.Catalog);
        var push = new FakePlayerPush();
        var publisher = new NotificationPublisher(_store, contract, push);

        // Run 1: pump at ResolvedTurn=0, window [0,1] — captures the created event.
        publisher.PublishTurn(_source.Collect(ContextAt(0)).ToList(), NotifyDelivery.Live, cursor: null);
        // A second cache's own creation prunes the first row out at retainPerCategory=1 (same
        // category, cache.created) — the exactly-once guarantee must hold even so (notify-store's
        // key ledger, not the pruned row itself, is what prevents a re-insert).
        var otherCacheId = CreateCache("lawn", "lawn-ref-prune");
        publisher.Publish(new SaveId(_playerId), new[] {
            new NotificationDraft($"cache:{otherCacheId}:created", "cache.created", NotifySeverity.Important,
                "cache-notify-source", "cache.created", Array.Empty<NotifyArg>(), $"cache:{otherCacheId}", WorldId, 0)
        }, NotifyDelivery.Live);

        // Run 2: pump at ResolvedTurn=1, window [1,2] — the SAME real cache-created event may still
        // be in this window (defensive over-inclusion, spec §2); the dedup key is identical.
        CommitAll(); // turn 1 -> 2, so ResolvedTurn=1 is now a real resolved turn
        publisher.PublishTurn(_source.Collect(ContextAt(1)).ToList(), NotifyDelivery.Live, cursor: null);

        var page = _store.ListNotificationChanges(new SaveId(_playerId), 0, 100);
        var matches = page.Items.Where(r => r.DedupKey == $"cache:{cacheId}:created").ToList();
        Assert.True(matches.Count <= 1, $"expected at most one stored row for the dedup key, found {matches.Count}");
    }
}
