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

/// <summary>GET /api/debug/lawn/state -- answers "where and when" without eyeballing the game or a
/// single HTTP response. Built 2026-09-14 after a real incident: an agent read a `/lawn/quick-start`
/// { ok: true } response with real ptrs and declared the board recovered, while the operator was
/// looking at a still-showing defeat screen. Neither side was wrong -- the simulation had moved on,
/// the screen had not -- and there was no single query that could have said so.</summary>
public class LawnStateEndpointTests : IAsyncLifetime
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

    void Insert(string kind, object payload, DateTime? at = null) => _store.InsertEvent(new EventEnvelope
    {
        T = (at ?? DateTime.UtcNow).ToString("o"),
        Kind = kind,
        Payload = JsonSerializer.SerializeToElement(payload)
    });

    [Fact]
    public async Task NoEventsAtAll_reportsUnknown_notAGuess()
    {
        var resp = await _http.GetAsync("/api/debug/lawn/state");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("Unknown", body!["state"].ToString());
    }

    [Fact]
    public async Task RecentBoardEconomy_reportsInMatch()
    {
        Insert("board.economy", new { sun = 100 });
        var resp = await _http.GetAsync("/api/debug/lawn/state");
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("InMatch", body!["state"].ToString());
        Assert.NotNull(body["asOf"]);
    }

    [Fact]
    public async Task LatestMatchResultIsDefeat_reportsDefeated_notInMatch()
    {
        Insert("board.economy", new { sun = 100 }, DateTime.UtcNow.AddSeconds(-5));
        Insert("match.result", new { result = "defeat" });
        var resp = await _http.GetAsync("/api/debug/lawn/state");
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("Defeated", body!["state"].ToString());
        Assert.Equal("defeat", body["latestMatchResult"].ToString());
    }

    [Fact]
    public async Task BoardEconomyAfterDefeat_reportsInMatch_defeatIsStale()
    {
        // The whole point of using timestamps, not "does a match.result:defeat event exist anywhere":
        // a later board.economy proves the board moved on since the defeat (e.g. debug.reset-board
        // + a fresh spawn), so InMatch must win, not a lingering Defeated read from a stale event.
        Insert("match.result", new { result = "defeat" }, DateTime.UtcNow.AddSeconds(-5));
        Insert("board.economy", new { sun = 100 });
        var resp = await _http.GetAsync("/api/debug/lawn/state");
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("InMatch", body!["state"].ToString());
    }

    [Fact]
    public async Task MatchLoseWithNoMatchResult_reportsDefeated()
    {
        // match.lose (GameLose.HandleGameLose) carries no data but is an unambiguous pulse -- it must
        // decide Defeated on its own, not require match.result's result string to also be present.
        Insert("match.lose", new { });
        var resp = await _http.GetAsync("/api/debug/lawn/state");
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("Defeated", body!["state"].ToString());
    }

    [Fact]
    public async Task MatchWinWithNoMatchResult_reportsVictorious()
    {
        Insert("match.win", new { });
        var resp = await _http.GetAsync("/api/debug/lawn/state");
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("Victorious", body!["state"].ToString());
    }

    [Fact]
    public async Task MatchResultNonDefeat_reportsVictorious()
    {
        Insert("match.result", new { result = "victory" });
        var resp = await _http.GetAsync("/api/debug/lawn/state");
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("Victorious", body!["state"].ToString());
    }

    [Fact]
    public async Task NewerMatchWinOverridesOlderMatchResultDefeat_reportsVictorious()
    {
        // Whichever real terminal signal is most recent decides -- cross-checking match.result
        // against the unambiguous pulses, not trusting one source exclusively.
        Insert("match.result", new { result = "defeat" }, DateTime.UtcNow.AddSeconds(-5));
        Insert("match.win", new { });
        var resp = await _http.GetAsync("/api/debug/lawn/state");
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("Victorious", body!["state"].ToString());
    }

    [Fact]
    public async Task CatalogZombiesWithNoEconomy_reportsLevelEntryPending()
    {
        // catalog.zombies (InitZombieList.InitZombie) fires before Board.Awake -- the earliest real
        // signal a level entry has begun, likely the seed-picker screen (medium confidence, not
        // certain -- see lawn-run-state-machine.md).
        Insert("catalog.zombies", new { levelType = "Advanture", levelNumber = 1 });
        var resp = await _http.GetAsync("/api/debug/lawn/state");
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("LevelEntryPending", body!["state"].ToString());
    }

    [Fact]
    public async Task EconomyNewerThanCatalogZombies_reportsInMatch_notLevelEntryPending()
    {
        Insert("catalog.zombies", new { levelType = "Advanture", levelNumber = 1 }, DateTime.UtcNow.AddSeconds(-5));
        Insert("board.economy", new { sun = 100 });
        var resp = await _http.GetAsync("/api/debug/lawn/state");
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("InMatch", body!["state"].ToString());
    }

    [Fact]
    public async Task MatchResultFromBeforeLatestInjectorHello_isDiscarded_reportsUnknownNotDefeated()
    {
        // Real bug found live 2026-09-14: a fresh game process (new injector.hello) sitting idle at
        // the main menu read as "Defeated", because the classifier read a match.result event from a
        // PREVIOUS, already-dead game process with nothing invalidating it. Same fix
        // FindLatestLiveBoardStart already applies to board.start: any lifecycle signal older than
        // the newest injector.hello belongs to a process that is gone.
        Insert("match.result", new { result = "defeat" }, DateTime.UtcNow.AddMinutes(-10));
        Insert("injector.hello", new { game = "pvzrh-3.9", version = "1.0.0" });
        var resp = await _http.GetAsync("/api/debug/lawn/state");
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotEqual("Defeated", body!["state"].ToString());
        Assert.Equal("Unknown", body["state"].ToString());
    }

    [Fact]
    public async Task MatchResultAfterLatestInjectorHello_stillReportsDefeated()
    {
        // The guard must not over-correct: a defeat from the CURRENT process (after the latest
        // hello) is still real and must still be reported.
        Insert("injector.hello", new { game = "pvzrh-3.9", version = "1.0.0" }, DateTime.UtcNow.AddSeconds(-10));
        Insert("match.result", new { result = "defeat" });
        var resp = await _http.GetAsync("/api/debug/lawn/state");
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("Defeated", body!["state"].ToString());
    }

    [Fact]
    public async Task InjectorHelloFoundAcrossMoreThan500IntermediateEvents_stillDiscardsStaleDefeat()
    {
        // Real bug found live 2026-09-14, one layer deeper than the staleness check itself:
        // RpgStore.ListEvents hardcodes Math.Clamp(limit, 1, 500) in its SQL regardless of what a
        // caller asks for -- so FindLatestKind's own claimed 2000-row window silently was only ever
        // 500 rows, and on a long-running server (500+ events between an old match.result and the
        // injector.hello that should invalidate it) the hello fell outside the window, so the stale
        // defeat was never discarded. This reproduces exactly that shape: >500 filler events between
        // the stale defeat and the fresh hello.
        Insert("match.result", new { result = "defeat" }, DateTime.UtcNow.AddMinutes(-10));
        for (var i = 0; i < 600; i++) Insert("cheat.apply", new { note = $"filler {i}" });
        Insert("injector.hello", new { game = "pvzrh-3.9", version = "1.0.0" });

        var resp = await _http.GetAsync("/api/debug/lawn/state");
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotEqual("Defeated", body!["state"].ToString());
        Assert.Equal("Unknown", body["state"].ToString());
    }

    [Fact]
    public async Task ThreeRecentBoardEnds_reportsCycling()
    {
        for (var i = 0; i < 3; i++) Insert("board.end", new { });
        var resp = await _http.GetAsync("/api/debug/lawn/state");
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("Cycling", body!["state"].ToString());
        Assert.True(((JsonElement)body["recentBoardEnds"]).GetInt32() >= 3);
    }

    [Fact]
    public async Task StaleBoardEconomy_doesNotReportInMatch()
    {
        // A board.economy from 5 minutes ago is not evidence of a live match right now.
        Insert("board.economy", new { sun = 100 }, DateTime.UtcNow.AddMinutes(-5));
        var resp = await _http.GetAsync("/api/debug/lawn/state");
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotEqual("InMatch", body!["state"].ToString());
    }

    [Fact]
    public async Task Response_alwaysCarriesTheVisualCaveat()
    {
        // The one thing this endpoint must never let a caller forget: it answers what the simulation
        // recorded, never what is rendered on screen.
        var resp = await _http.GetAsync("/api/debug/lawn/state");
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Contains("rendered on screen", body!["note"].ToString());
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
