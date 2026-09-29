using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Notify;
using FusionRpg.Core.Saves;
using FusionRpg.Data.Abstractions;
using FusionRpg.Data.Notifications;
using FusionRpg.Data.Tests;
using FusionRpg.Data;
using FusionRpg.Server.Notifications;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using Xunit;

namespace FusionRpg.Server.Tests.Notifications;

/// <summary>Map Gate G1 - routed and durable, end to end: routing (player-routing), the store
/// (notify-store), the publisher and the pump (notify-service) composed together, with a REAL
/// SignalR `HubPlayerPush` and a REAL REST catch-up GET (`AptitudesInjectorBroadcastTests`/
/// `PlayerRoutingTests` house style - real connections, no mock).</summary>
[Collection("NotificationHub")]
public class NotificationG1Tests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    string _baseUrl = "";
    HttpClient _http = null!;
    long _save1;
    long _save2;

    public async Task InitializeAsync()
    {
        NotificationHubFixture.Configure(retainPerCategory: 2);
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _save1 = _store.GetCurrentPlayerId();
        _save2 = _store.CreatePlayer("Second").Id;

        var port = GetFreeTcpPort();
        _baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR(o => o.EnableDetailedErrors = true);
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<EffectGrantSession>();
        builder.Services.AddSingleton<IHotCompactor>(sp => new HotCompactor(sp.GetRequiredService<RpgStore>()));
        builder.Services.AddSingleton<CompactionWorker>();
        builder.Services.AddSingleton<UniqueActorService>();
        builder.Services.AddSingleton<EventIngest>();
        builder.Services.AddSingleton<DelveBattleSessionManager>();
        builder.Services.AddSingleton<PlayerConnectionRegistry>();
        builder.Services.AddSingleton<IPlayerPush, HubPlayerPush>(); // the REAL SignalR push
        builder.Services.AddSingleton(sp => new NotificationContract(NotificationCatalogHub.Catalog));
        builder.Services.AddSingleton<NotificationPublisher>();
        builder.WebHost.UseUrls(_baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        _app.MapNotifications();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    HubConnection NewClient() => new HubConnectionBuilder().WithUrl($"{_baseUrl}/hub/rpg").Build();
    NotificationPublisher Publisher => _app.Services.GetRequiredService<NotificationPublisher>();

    [Fact]
    public async Task A_draft_for_save_A_is_stored_before_any_push_and_reaches_only_player_A()
    {
        var hubA = NewClient();
        var hubB = NewClient();
        var receivedA = new TaskCompletionSource<NotificationBatchDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedB = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        hubA.On<NotificationBatchDto>(NotificationEvents.Batch, b => receivedA.TrySetResult(b));
        hubB.On<NotificationBatchDto>(NotificationEvents.Batch, _ => receivedB.TrySetResult(true));
        await hubA.StartAsync();
        await hubB.StartAsync();
        Assert.True(await hubA.InvokeAsync<bool>("JoinPlayer", _save1));
        Assert.True(await hubB.InvokeAsync<bool>("JoinPlayer", _save2));

        Publisher.Publish(new SaveId(_save1), new[] { NotificationHubFixture.Draft("g1-1", worldTurn: 1) }, NotifyDelivery.Live);

        var batch = await receivedA.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("g1-1", Assert.Single(batch.Items).DedupKey);
        // Already durable at the moment the push fired: the row is readable independent of the push.
        Assert.Single(_store.ListNotificationsByCategory(new SaveId(_save1), NotificationHubFixture.TestCategory, null, 10).Items);

        await Task.Delay(200);
        Assert.False(receivedB.Task.IsCompleted, "save B's connection received save A's push");

        await hubA.DisposeAsync();
        await hubB.DisposeAsync();
    }

    [Fact]
    public async Task Republishing_the_same_draft_including_after_its_row_was_pruned_stores_and_pushes_nothing()
    {
        var hub = NewClient();
        var pushes = new List<NotificationBatchDto>();
        hub.On<NotificationBatchDto>(NotificationEvents.Batch, b => pushes.Add(b));
        await hub.StartAsync();
        await hub.InvokeAsync<bool>("JoinPlayer", _save1);

        Publisher.Publish(new SaveId(_save1), new[] { NotificationHubFixture.Draft("g1-2", worldTurn: 1) }, NotifyDelivery.Live);
        await Task.Delay(200);
        Assert.Single(pushes);

        // Prune it away (retainPerCategory=2): two more distinct keys in the same category.
        Publisher.Publish(new SaveId(_save1), new[] { NotificationHubFixture.Draft("g1-3", worldTurn: 1) }, NotifyDelivery.Live);
        Publisher.Publish(new SaveId(_save1), new[] { NotificationHubFixture.Draft("g1-4", worldTurn: 1) }, NotifyDelivery.Live);
        Assert.DoesNotContain(_store.ListNotificationsByCategory(new SaveId(_save1), NotificationHubFixture.TestCategory, null, 10).Items,
            r => r.DedupKey == "g1-2"); // g1-2's row is gone

        // Re-publishing the pruned key stores and pushes nothing.
        Publisher.Publish(new SaveId(_save1), new[] { NotificationHubFixture.Draft("g1-2", worldTurn: 1) }, NotifyDelivery.Live);
        await Task.Delay(200);
        Assert.Equal(3, pushes.Count); // g1-2, g1-3, g1-4 - never a fourth for the repeat
        Assert.DoesNotContain(_store.ListNotificationsByCategory(new SaveId(_save1), NotificationHubFixture.TestCategory, null, 10).Items,
            r => r.DedupKey == "g1-2");

        await hub.DisposeAsync();
    }

    [Fact]
    public async Task A_crash_between_commit_and_push_leaves_rows_and_cursor_and_the_catchup_GET_delivers_them()
    {
        var throwing = new ThrowOncePush(_app.Services.GetRequiredService<IPlayerPush>());
        var publisher = new NotificationPublisher(_store, new NotificationContract(NotificationCatalogHub.Catalog), throwing);
        var cursor = new NotificationCursorAdvance("g1-source", "w", 5);

        Assert.Throws<InvalidOperationException>(() =>
            publisher.PublishTurn(new[] { new AddressedDraft(new SaveId(_save1), NotificationHubFixture.Draft("g1-crash", worldTurn: 1)) },
                NotifyDelivery.Live, cursor));

        Assert.Single(_store.ListNotificationsByCategory(new SaveId(_save1), NotificationHubFixture.TestCategory, null, 10).Items);
        Assert.Equal(5, _store.GetNotificationCursor("g1-source", "w"));

        var page = await _http.GetFromJsonAsync<NotificationPageDto>($"/api/notifications/{_save1}");
        Assert.Contains(page!.Items, i => i.DedupKey == "g1-crash");
    }

    sealed class ThrowOncePush : IPlayerPush
    {
        readonly IPlayerPush _inner;
        bool _thrown;
        public ThrowOncePush(IPlayerPush inner) => _inner = inner;
        public void Push(SaveId saveId, string eventName, object payload)
        {
            if (!_thrown) { _thrown = true; throw new InvalidOperationException("forced (G1 test)"); }
            _inner.Push(saveId, eventName, payload);
        }
    }

    [Fact]
    public async Task A_connection_that_joins_after_the_push_still_gets_the_row_through_the_catchup_GET()
    {
        Publisher.Publish(new SaveId(_save1), new[] { NotificationHubFixture.Draft("g1-late", worldTurn: 1) }, NotifyDelivery.Live);

        // No connection was ever joined to save1's group when the push fired - proving the GET is
        // the correctness path independent of any live push having reached anyone.
        var page = await _http.GetFromJsonAsync<NotificationPageDto>($"/api/notifications/{_save1}");
        Assert.Contains(page!.Items, i => i.DedupKey == "g1-late");
    }

    [Fact]
    public async Task A_boot_catch_up_run_produces_no_toast_delivery_label()
    {
        var source = new FakeWorldTurnSource
        {
            OnCollect = ctx => new[] { new AddressedDraft(new SaveId(ctx.Header.PlayerId), NotificationHubFixture.Draft($"g1-boot-{ctx.ResolvedTurn}", worldTurn: ctx.ResolvedTurn)) }
        };
        WorldTuningTestSupport.ConfigureOnce();
        _store.CreateWorld(_save1, FusionRpg.Core.World.WorldTemplateCatalog.Build(FusionRpg.Core.World.WorldTemplateCatalog.FirstLightId, (ulong)_save1, "g1-w"));
        CommitAllCommanders("g1-w"); // R=0, first pump run inits cursor
        var pump = new WorldTurnNotificationPump(_store, Publisher, new[] { source });
        pump.Run("g1-w", WorldTurnTrigger.Commit); // inits cursor, no publish (absent-cursor rule)
        CommitAllCommanders("g1-w"); // R=1, now boot has something to catch up

        var boot = new NotificationBootCatchUp(_store, pump);
        NotificationBatchDto? seen = null;
        var hub = NewClient();
        hub.On<NotificationBatchDto>(NotificationEvents.Batch, b => seen = b);
        await hub.StartAsync();
        await hub.InvokeAsync<bool>("JoinPlayer", _save1);

        await boot.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        Assert.NotNull(seen);
        Assert.Equal(NotifyDelivery.CatchUp, seen!.Delivery);
        await hub.DisposeAsync();
    }

    static readonly string[] AllCommanders = { "dave", "wild", "zomboss" };
    void CommitAllCommanders(string worldId)
    {
        var open = _store.GetWorldHeader(worldId)!.CurrentTurn;
        foreach (var c in AllCommanders.Where(c => c != "dave").Concat(AllCommanders.Where(c => c == "dave")))
            _store.CommitWorldTurn(worldId, c, open);
    }

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
