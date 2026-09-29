using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Data;
using FusionRpg.Data.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>POST /api/debug/game-state -- the ACTIVE counterpart to /lawn/state. Where /lawn/state
/// reconstructs a best guess from the event log (fragile: a clamped query window, a signal that never
/// fires, or a long enough session can all produce a wrong answer -- see lawn-run-state-machine.md),
/// this asks the game to read its own live objects (Board.Instance / InitBoard.Instance /
/// GameAPP.theBoardType / the injector's MatchPhase FSM) and report exactly what it found, right now.
/// Built 2026-09-14 in direct response to "the game injector must answer where is current game state".
/// </summary>
public class GameStateEndpointTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<EffectGrantSession>();
        builder.Services.AddSingleton<IHotCompactor>(sp => new HotCompactor(sp.GetRequiredService<RpgStore>()));
        builder.Services.AddSingleton<EventIngest>();
        builder.Services.AddSingleton<FusionRpg.Server.DelveBattleSessionManager>();
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        _app.MapDebug();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    [Fact]
    public async Task InjectorNotConnected_refusesBeforeQueueing()
    {
        var resp = await _http.PostAsJsonAsync("/api/debug/game-state", new { });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Contains("injector not connected", body!["error"].ToString());
    }

    [Fact]
    public async Task Connected_timesOutHonestlyWhenNoUnityAckArrives()
    {
        _store.Heartbeat(RpgConstants.SourceInjector);
        var resp = await _http.PostAsJsonAsync("/api/debug/game-state", new { });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Contains("debug.game-state did not ack", body!["error"].ToString());
    }

    [Fact]
    public async Task Connected_acknowledgement_returnsTheLivePayloadVerbatim()
    {
        _store.Heartbeat(RpgConstants.SourceInjector);
        var inbox = _app.Services.GetRequiredService<InjectorCommandInbox>();
        var request = _http.PostAsJsonAsync("/api/debug/game-state", new { });

        for (var i = 0; i < 40 && inbox.Count == 0; i++)
            await Task.Delay(25);
        Assert.True(inbox.Count > 0);

        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "debug.game-state",
            Payload = JsonSerializer.SerializeToElement(new
            {
                ok = true,
                hasBoard = false,
                hasInitBoard = false,
                liveState = "AtMainMenuOrNoBoard",
                matchPhase = "Idle"
            })
        });

        var resp = await request;
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.True(((JsonElement)body!["ok"]).GetBoolean());
        var live = (JsonElement)body["live"];
        Assert.Equal("AtMainMenuOrNoBoard", live.GetProperty("liveState").GetString());
        Assert.Equal("Idle", live.GetProperty("matchPhase").GetString());
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
