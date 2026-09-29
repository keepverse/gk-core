using FusionRpg.Contracts;
using FusionRpg.Core.Notify;
using FusionRpg.Core.Saves;
using FusionRpg.Core.World;
using FusionRpg.Data.Sqlite;
using FusionRpg.Data.Tests;
using FusionRpg.Data;
using FusionRpg.Server.Notifications;
using Xunit;

namespace FusionRpg.Server.Tests.Notifications;

/// <summary>notify-service spec §3 Triggers ("Server boot"), Testing 9 - a hosted startup step that
/// runs the pump once per save's active map world, always as `CatchUp`.</summary>
[Collection("NotificationHub")]
public class NotificationBootCatchUpTests : IDisposable
{
    static readonly string[] AllCommanders = { "dave", "wild", "zomboss" };

    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly FakePlayerPush _push;

    public NotificationBootCatchUpTests()
    {
        NotificationHubFixture.Configure();
        WorldTuningTestSupport.ConfigureOnce();
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _push = new FakePlayerPush();
    }

    public void Dispose() => _testStore.Dispose();

    void CommitOneTurn(string worldId, long saveId)
    {
        var open = _store.GetWorldHeader(worldId)!.CurrentTurn;
        foreach (var c in AllCommanders.Where(c => c != "dave").Concat(AllCommanders.Where(c => c == "dave")))
            _store.CommitWorldTurn(worldId, c, open);
    }

    [Fact]
    public async Task Boot_runs_the_pump_once_for_each_saves_active_map_world_as_CatchUp()
    {
        var save1 = _store.GetCurrentPlayerId();
        var save2 = _store.CreatePlayer("Second").Id;
        _store.CreateWorld(save1, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, (ulong)save1, "w1"));
        _store.CreateWorld(save2, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, (ulong)save2, "w2"));
        CommitOneTurn("w1", save1);
        CommitOneTurn("w2", save2);
        // Each world's cursor already sits at R (from a prior Commit-trigger run), so Boot's own
        // catch-up has something new only if a LATER turn resolved after that. Simulate exactly
        // that: advance each world once more so Boot has one real turn to catch up on.
        CommitOneTurn("w1", save1);
        CommitOneTurn("w2", save2);

        var source = new FakeWorldTurnSource
        {
            OnCollect = ctx => new[] { new AddressedDraft(new SaveId(ctx.Header.PlayerId), NotificationHubFixture.Draft($"t{ctx.ResolvedTurn}", worldTurn: ctx.ResolvedTurn)) }
        };
        var pump = new WorldTurnNotificationPump(_store, new NotificationPublisher(_store, new NotificationContract(NotificationCatalogHub.Catalog), _push), new[] { source });
        var boot = new NotificationBootCatchUp(_store, pump);

        await boot.StartAsync(CancellationToken.None);

        // Each world's first Run (as if it were the very first ever) inits its cursor with no
        // publish; this test's two commits per world before Boot mean Boot's own run finds R=1 with
        // an absent cursor too (Boot is the FIRST pump.Run call here) - so it inits, no publish yet.
        Assert.Empty(_push.Pushes);
        Assert.Equal(1, _store.GetNotificationCursor("world-turn", "w1"));
        Assert.Equal(1, _store.GetNotificationCursor("world-turn", "w2"));

        // A THIRD turn per world, then Boot again: now there is something to catch up on.
        CommitOneTurn("w1", save1);
        CommitOneTurn("w2", save2);
        await boot.StartAsync(CancellationToken.None);

        Assert.Equal(2, _push.Pushes.Count); // one CatchUp batch per world's save
        Assert.All(_push.Pushes, p => Assert.Equal(NotifyDelivery.CatchUp, ((NotificationBatchDto)p.Payload).Delivery));
    }

    /// <summary>NS7.1: the boot step walks saves through `ListPlayers()`, whose own filter excludes
    /// archived rows (save-identity SE4.29 / D2). An archived save's world is therefore never visited -
    /// proven by its cursor staying absent, while the live save's world is visited and its cursor is
    /// initialised (the observation the first test in this class establishes).</summary>
    [Fact]
    public async Task An_archived_row_is_never_visited_by_the_boot_catch_up()
    {
        var live = _store.GetCurrentPlayerId();
        var archived = _store.CreatePlayer("Archived").Id;
        _store.CreateWorld(live, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, (ulong)live, "wl"));
        _store.CreateWorld(archived, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, (ulong)archived, "wa"));
        CommitOneTurn("wl", live);
        CommitOneTurn("wa", archived);
        // A second turn per world, the first test's own shape: Boot's run then finds R=1 with an
        // absent cursor and initialises it (a visit), which is the observation this test reads.
        CommitOneTurn("wl", live);
        CommitOneTurn("wa", archived);
        Archive(archived);

        var pump = new WorldTurnNotificationPump(_store, new NotificationPublisher(_store, new NotificationContract(NotificationCatalogHub.Catalog), _push), Array.Empty<IWorldTurnNotificationSource>());
        var boot = new NotificationBootCatchUp(_store, pump);

        await boot.StartAsync(CancellationToken.None);

        Assert.Equal(1, _store.GetNotificationCursor("world-turn", "wl"));    // visited, cursor initialised
        Assert.Null(_store.GetNotificationCursor("world-turn", "wa"));        // never visited
    }

    /// <summary>The migration's own write (save-identity step 6), applied directly: this fixture is the
    /// archived-row SHAPE, not a re-test of the migration (that lives in Data.Tests).</summary>
    void Archive(long playerId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE players SET archived_utc = $t WHERE id = $p;";
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_save_with_no_active_world_is_skipped_without_error()
    {
        _store.CreatePlayer("No world"); // ListPlayers() will include this save
        var pump = new WorldTurnNotificationPump(_store, new NotificationPublisher(_store, new NotificationContract(NotificationCatalogHub.Catalog), _push), Array.Empty<IWorldTurnNotificationSource>());
        var boot = new NotificationBootCatchUp(_store, pump);

        await boot.StartAsync(CancellationToken.None); // must not throw

        Assert.Empty(_push.Pushes);
    }

    [Fact]
    public async Task A_failing_world_never_blocks_the_boot_step_from_starting()
    {
        // No world at all for the current player - GetActiveWorld returns null and is skipped, but
        // this also proves StartAsync completes (the hosted-service contract boot depends on).
        var pump = new WorldTurnNotificationPump(_store, new NotificationPublisher(_store, new NotificationContract(NotificationCatalogHub.Catalog), _push), Array.Empty<IWorldTurnNotificationSource>());
        var boot = new NotificationBootCatchUp(_store, pump);

        await boot.StartAsync(CancellationToken.None);
        await boot.StopAsync(CancellationToken.None);
    }
}
