using FusionRpg.Contracts;
using FusionRpg.Core.Notify;
using FusionRpg.Core.Saves;
using FusionRpg.Core.World;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using FusionRpg.Data.Tests;
using FusionRpg.Server.Notifications;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using Xunit;

namespace FusionRpg.Server.Tests.Notifications;

/// <summary>
/// map Gate G3, first half (cache-notify-source success criterion 1) — a legion death that starts a
/// cache produces <c>cache.created</c>, and a destroying tick produces <c>cache.decayed</c>, each in
/// the **same** `NotificationBatch` as that turn's world items: one pump run, one batch per save
/// (R-N6). Both sources are the REAL ones, over a real in-memory store and the real commit route, and
/// the catalog/tuning are the SHIPPED files, so both domains' words are validated as the host does.
/// </summary>
[Collection("NotificationHub")]
public class CacheNotifyEndToEndTests : IAsyncLifetime
{
    static readonly string[] AllCommanders = { "dave", "wild", "zomboss" };
    const string WorldId = "w-cache-e2e";

    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    FakePlayerPush _push = null!;
    long _save;

    public async Task InitializeAsync()
    {
        ConfigureShippedNotificationFiles();
        WorldTuningTestSupport.ConfigureOnce();
        ConfigureDeploymentHierarchyTuningOnce();

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _save = _store.GetCurrentPlayerId();

        // A world item to ride the batch: the first-light homeworld turned into a developed,
        // dangerous, empty sector (LoamForecastTests' own starving shape), everything else real.
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, (ulong)_save, WorldId);
        world = world with
        {
            Sectors = world.Sectors
                .Select(s => s.SectorId == "homeworld"
                    ? s with { LoamStock = 0, DevelopmentLevel = 10, DangerBand = 4 + 2 * 10 }
                    : s)
                .ToList()
        };
        _store.CreateWorld(_save, world);

        _push = new FakePlayerPush();
        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR(o => o.EnableDetailedErrors = true);
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<IPlayerPush>(_push);
        builder.Services.AddSingleton(sp => new NotificationContract(NotificationCatalogHub.Catalog));
        builder.Services.AddSingleton<NotificationPublisher>();
        builder.Services.AddSingleton<IWorldFactionSaves, WorldFactionSaves>();
        // BOTH real sources, in the order the host registers them.
        builder.Services.AddSingleton<IEnumerable<IWorldTurnNotificationSource>>(new IWorldTurnNotificationSource[]
        {
            new CacheNotificationSource(_store),
            new WorldReportNotificationSource(_store, new WorldFactionSaves())
        });
        builder.Services.AddSingleton<WorldTurnNotificationPump>();
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        _app.MapWorld();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    [Fact]
    public async Task Cache_items_ride_the_same_batch_as_that_turns_world_items()
    {
        await CommitOneTurnAsync(); // R=0: the world's first run only initialises the cursor
        Assert.Empty(_push.Pushes);

        // Seed the cache half inside the window the publishing run reads. `ctx.ResolvedTurn` is 1, so
        // the source reads [1, 2]: the clock started at the turn the first round just resolved, and the
        // destroying tick is stamped 2 (a tick runs after CommitWorldTurn's own advance — the tick
        // number `CacheNotificationSource`'s window comment names).
        var cacheId = CreateCacheStartedThisTurn();
        InsertStackItem(cacheId, seq: 0);
        InsertStackItem(cacheId, seq: 1);
        InsertDestroyingTick(cacheId, tick: 2, destroyed: 2);

        await CommitOneTurnAsync(); // R=1: ONE pump run, with both sources collecting

        var push = Assert.Single(_push.Pushes); // one batch, not one per source (R-N6)
        var batch = (NotificationBatchDto)push.Payload;
        Assert.Equal(_save, batch.PlayerId);
        Assert.Equal(NotifyDelivery.Live, batch.Delivery); // a commit's newest turn, not a catch-up

        var categories = batch.Items.Select(i => i.Category).ToList();
        Assert.Contains("loam.shortfall", categories); // the turn's own world item
        Assert.Contains("cache.created", categories);  // and the cache's two, in the SAME batch
        Assert.Contains("cache.decayed", categories);

        // Each carries the turn the batch is about, so the rail's `worldLatestTurn` filter holds.
        Assert.All(
            batch.Items.Where(i => i.Category is "cache.created" or "cache.decayed"),
            i => Assert.Equal(1, i.WorldTurn));
    }

    async Task CommitOneTurnAsync()
    {
        foreach (var c in AllCommanders.Where(c => c != "dave").Concat(AllCommanders.Where(c => c == "dave")))
        {
            var open = _store.GetWorldHeader(WorldId)!.CurrentTurn;
            var response = await _http.PostAsJsonAsync($"/api/world/{WorldId}/commit", new { commanderId = c, turn = open });
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>Raw SQL, not the internal `ResolveOrCreateCacheUnlocked` (no `InternalsVisibleTo` grant
    /// from `FusionRpg.Data` to `FusionRpg.Server.Tests`) — the same convention `CacheNotifySourceTests`
    /// and `CacheDecayTests` already use. The clock is stamped at the world's CURRENT turn, exactly as
    /// a real death resolution would stamp it.</summary>
    string CreateCacheStartedThisTurn()
    {
        var cacheId = "cc_e2e_" + Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("o");
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_corpse_cache
                (cache_id, place_kind, place_ref, source_kind, created_utc, decay_started_utc, decay_started_turn, owner_player_id, in_void, revision)
            VALUES ($id, 'world_sector', 'ash-waste', 'death', $now, $now, $t, $p, 0, 0);
            """;
        cmd.Parameters.AddWithValue("$id", cacheId);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$t", _store.GetWorldHeader(WorldId)!.CurrentTurn);
        cmd.Parameters.AddWithValue("$p", _save);
        cmd.ExecuteNonQuery();
        return cacheId;
    }

    void InsertStackItem(string cacheId, int seq)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_corpse_cache_item (cache_id, seq, kind, instance_id, container_id, qty, origin_owner)
            VALUES ($c, $s, 'stack', NULL, 'item.cache-e2e-test', 1, 'test');
            """;
        cmd.Parameters.AddWithValue("$c", cacheId);
        cmd.Parameters.AddWithValue("$s", seq);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The tick's OWN row shape (`rpg_corpse_cache_decay_log`, whose `outcomes_json` is the array
    /// `ListCacheDecayTicks` reduces), seeded rather than rolled: survival is 994/1000, so a real roll
    /// could not be counted on to destroy anything inside ONE publishing run — and retrying ticks would
    /// publish more turns, which is the opposite of what this test asserts. The row also makes the real
    /// tick loop skip this cache (its own `NOT EXISTS ... tick = $t` guard), so nothing double-inserts.
    /// </summary>
    void InsertDestroyingTick(string cacheId, int tick, int destroyed)
    {
        var outcomes = System.Text.Json.JsonSerializer.Serialize(
            Enumerable.Range(0, destroyed).Select(_ => new { outcome = "destroyed" }));
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_corpse_cache_decay_log (cache_id, tick, rolled_utc, outcomes_json)
            VALUES ($c, $t, $now, $json);
            """;
        cmd.Parameters.AddWithValue("$c", cacheId);
        cmd.Parameters.AddWithValue("$t", tick);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$json", outcomes);
        cmd.ExecuteNonQuery();
    }

    static void ConfigureShippedNotificationFiles()
    {
        var dir = Path.Combine(FindRepoRoot(), "data", "tuning");
        NotificationCatalogHub.Configure(NotificationCatalogLoader.Parse(
            File.ReadAllText(Path.Combine(dir, "notification-catalog.v3.json"))));
        NotificationTuningHub.Configure(NotificationTuningLoader.Parse(
            File.ReadAllText(Path.Combine(dir, "notification.v1.json"))));
    }

    /// <summary>The decay tick a real commit runs reads `DeploymentHierarchyTuningHub`; the Server
    /// test module initializer does not configure it (CacheNotifySourceTests' own named gap).</summary>
    static bool _deploymentHierarchyConfigured;
    static void ConfigureDeploymentHierarchyTuningOnce()
    {
        if (_deploymentHierarchyConfigured) return;
        var json = File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "deployment-hierarchy.v5.json"));
        FusionRpg.Core.Items.Materials.DeploymentHierarchyTuningHub.Configure(
            FusionRpg.Core.Items.Materials.DeploymentHierarchyTuningLoader.Parse(json));
        _deploymentHierarchyConfigured = true;
    }

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not find repo root above " + AppContext.BaseDirectory);
    }
}
