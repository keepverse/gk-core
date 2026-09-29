using FusionRpg.Contracts;
using FusionRpg.Core.Notify;
using FusionRpg.Core.Saves;
using FusionRpg.Core.World;
using FusionRpg.Data.Tests;
using FusionRpg.Data;
using FusionRpg.Server.Notifications;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using Xunit;

namespace FusionRpg.Server.Tests.Notifications;

/// <summary>notify-service spec §3 Triggers, Testing 10 - a real `POST /api/world/{worldId}/commit`
/// against a live TestServer (`AptitudesInjectorBroadcastTests` house style). The only change to
/// `WorldEndpoints.cs` for this module is the one `notifyPump.Run(...)` call, guarded so a pump
/// failure never fails the commit response.</summary>
[Collection("NotificationHub")]
public class WorldTurnPumpCommitTests : IAsyncLifetime
{
    static readonly string[] AllCommanders = { "dave", "wild", "zomboss" };

    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    FakePlayerPush _push = null!;
    long _saveId;

    public async Task InitializeAsync()
    {
        NotificationHubFixture.Configure();
        WorldTuningTestSupport.ConfigureOnce();
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _saveId = _store.GetCurrentPlayerId();
        _store.CreateWorld(_saveId, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, (ulong)_saveId, "w"));

        _push = new FakePlayerPush();
        var throwingSource = new FakeWorldTurnSource
        {
            OnCollect = ctx => new[] { new AddressedDraft(new SaveId(_saveId), NotificationHubFixture.Draft($"t{ctx.ResolvedTurn}", worldTurn: ctx.ResolvedTurn)) }
        };

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR(o => o.EnableDetailedErrors = true);
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<IPlayerPush>(_push); // ThrowOnNextPush controls the failure
        builder.Services.AddSingleton(sp => new NotificationContract(NotificationCatalogHub.Catalog));
        builder.Services.AddSingleton<NotificationPublisher>();
        builder.Services.AddSingleton<IEnumerable<IWorldTurnNotificationSource>>(new IWorldTurnNotificationSource[] { throwingSource });
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

    async Task<System.Net.Http.HttpResponseMessage> CommitAsync(string commanderId)
    {
        var open = _store.GetWorldHeader("w")!.CurrentTurn;
        return await _http.PostAsJsonAsync("/api/world/w/commit", new { commanderId, turn = open });
    }

    /// <summary>Runs every commander for the currently open turn, advancing it once; the endpoint
    /// calls `notifyPump.Run` after every advancing commit.</summary>
    async Task<System.Net.Http.HttpResponseMessage> CommitOneTurnAsync()
    {
        System.Net.Http.HttpResponseMessage last = null!;
        foreach (var c in AllCommanders.Where(c => c != "dave").Concat(AllCommanders.Where(c => c == "dave")))
            last = await CommitAsync(c);
        return last;
    }

    [Fact]
    public async Task A_pump_that_throws_still_lets_commit_return_Ok_with_Advanced_true()
    {
        // Round 1 (CurrentTurn 0 -> 1, R=0): the pump's first-ever run for this world meets an
        // absent cursor and only initialises it - no publish, so nothing throws yet.
        (await CommitOneTurnAsync()).EnsureSuccessStatusCode();
        Assert.Empty(_push.Pushes);

        // Round 2 (CurrentTurn 1 -> 2, R=1): cursor is 0, so turn 1 is now in range and the fake
        // source's draft reaches the (throwing) push - this is the real failure this test proves.
        _push.ThrowOnNextPush = true;
        var last = await CommitOneTurnAsync();

        last.EnsureSuccessStatusCode();
        var body = await last.Content.ReadFromJsonAsync<WorldTurnCommitDto>();
        Assert.True(body!.Ok);
        Assert.True(body.Advanced);
        // The push threw, but the append that ran before it already moved the cursor.
        Assert.Equal(1, _store.GetNotificationCursor("world-turn", "w"));
    }

    [Fact]
    public async Task A_normal_advancing_commit_publishes_through_the_real_pump_on_its_second_round()
    {
        (await CommitOneTurnAsync()).EnsureSuccessStatusCode(); // R=0: cursor inits, no publish
        Assert.Empty(_push.Pushes);

        (await CommitOneTurnAsync()).EnsureSuccessStatusCode(); // R=1: turn 1 published
        Assert.Single(_push.Pushes);
        Assert.Equal(1, _store.GetNotificationCursor("world-turn", "w"));
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
