using System.Net.Http.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Notify;
using FusionRpg.Core.Saves;
using FusionRpg.Core.World;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using FusionRpg.Server.Notifications;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FusionRpg.Server.Tests.Notifications;

/// <summary>
/// world-notify-source spec Testing 6 — the real chain end to end on an in-memory store: a real
/// `POST /api/world/{worldId}/commit` that starves a component produces exactly one live
/// `NotificationBatch` for the world's save, carrying the `loam.shortfall` item, and nothing for any
/// other save. The catalog and tuning are the SHIPPED files (`notification-catalog.v3.json`,
/// `notification.v1.json`), so the words this batch carries are the words the real host would
/// validate — not a fixture catalog that accepts anything.
/// </summary>
[Collection("NotificationHub")]
public class WorldNotifyEndToEndTests : IAsyncLifetime
{
    static readonly string[] AllCommanders = { "dave", "wild", "zomboss" };
    const string WorldId = "wn-e2e";

    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    FakePlayerPush _push = null!;
    WorldTurnNotificationPump _pump = null!;
    long _save;
    long _otherSave;

    public async Task InitializeAsync()
    {
        ConfigureShippedNotificationFiles();
        WorldTuningTestSupport.ConfigureOnce();

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _save = _store.GetCurrentPlayerId();
        _otherSave = _store.CreatePlayer("Second").Id;

        // A component that cannot pay its upkeep: the real first-light template with its homeworld
        // turned into a developed, dangerous, empty sector (LoamForecastTests' own starving shape).
        // Every other element — seats, lanes, legions, the other factions — stays exactly as shipped.
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
        // The REAL source under test, not a fake.
        builder.Services.AddSingleton<IEnumerable<IWorldTurnNotificationSource>>(
            new IWorldTurnNotificationSource[] { new WorldReportNotificationSource(_store, new WorldFactionSaves()) });
        builder.Services.AddSingleton<WorldTurnNotificationPump>();
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        _app.MapWorld();
        await _app.StartAsync();

        _pump = _app.Services.GetRequiredService<WorldTurnNotificationPump>();
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    async Task CommitAsync(string commanderId)
    {
        var open = _store.GetWorldHeader(WorldId)!.CurrentTurn;
        var response = await _http.PostAsJsonAsync($"/api/world/{WorldId}/commit", new { commanderId, turn = open });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Every commander for the open turn, advancing it once — the endpoint runs the pump
    /// after each advancing commit, so the last commit in the round is the one that publishes.</summary>
    async Task CommitOneTurnAsync()
    {
        foreach (var c in AllCommanders.Where(c => c != "dave").Concat(AllCommanders.Where(c => c == "dave")))
            await CommitAsync(c);
    }

    [Fact]
    public async Task A_real_commit_that_starves_a_component_pushes_one_live_batch_for_its_own_save_only()
    {
        await CommitOneTurnAsync(); // R=0: the world's first run only initialises the cursor
        Assert.Empty(_push.Pushes);

        await CommitOneTurnAsync(); // R=1: turn 1's stored report is published

        var push = Assert.Single(_push.Pushes);
        Assert.Equal(new SaveId(_save), push.SaveId);
        Assert.Equal(NotificationEvents.Batch, push.EventName);
        Assert.DoesNotContain(_push.Pushes, p => p.SaveId == new SaveId(_otherSave));

        var batch = (NotificationBatchDto)push.Payload;
        Assert.Equal(_save, batch.PlayerId);
        Assert.Equal(NotifyDelivery.Live, batch.Delivery); // a commit's newest turn, not a catch-up

        var item = Assert.Single(batch.Items, i => i.Category == "loam.shortfall");
        Assert.Equal(1, item.WorldTurn); // the turn that just resolved
        Assert.Equal("world.turn-entry", item.MessageKey);
        Assert.Contains(item.Args, a => a.Name == "entry" && a.Kind == NotifyArgKind.DomainToken);
    }

    [Fact]
    public async Task Running_the_pump_again_over_the_same_turn_pushes_nothing()
    {
        await CommitOneTurnAsync();
        await CommitOneTurnAsync();
        var published = _push.Pushes.Count;
        Assert.Equal(1, published);

        // The catch-up path over an already-published turn: the cursor is already at R.
        _pump.Run(WorldId, WorldTurnTrigger.Boot);

        Assert.Equal(published, _push.Pushes.Count);
    }

    static void ConfigureShippedNotificationFiles()
    {
        var dir = Path.Combine(FindRepoRoot(), "data", "tuning");
        NotificationCatalogHub.Configure(NotificationCatalogLoader.Parse(
            File.ReadAllText(Path.Combine(dir, "notification-catalog.v3.json"))));
        NotificationTuningHub.Configure(NotificationTuningLoader.Parse(
            File.ReadAllText(Path.Combine(dir, "notification.v1.json"))));
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
