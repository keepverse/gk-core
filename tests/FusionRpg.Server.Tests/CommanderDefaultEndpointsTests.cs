using System.Net;
using System.Net.Http.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Commanders;
using FusionRpg.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>commander-surface default-persistence: GET/POST /api/commanders/default.</summary>
public class CommanderDefaultEndpointsTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    long _playerId;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<FusionRpg.Server.DelveBattleSessionManager>();
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        _app.MapCommanders();
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
    public async Task Get_default_on_fresh_save_returns_implicit_Dave_without_seeded_row()
    {
        var resp = await _http.GetAsync($"/api/commanders/{_playerId}/default");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DefaultLawnCommanderResponse>();
        Assert.Equal("commander:dave", body!.DefaultLawnCommanderId);
    }

    [Fact]
    public async Task Post_set_then_get_round_trips()
    {
        var stable = "commander:dave";
        var post = await _http.PostAsJsonAsync("/api/commanders/default",
            new SetDefaultLawnCommanderRequest { PlayerId = _playerId, CommanderId = stable });
        post.EnsureSuccessStatusCode();
        var posted = await post.Content.ReadFromJsonAsync<DefaultLawnCommanderResponse>();
        Assert.Equal(stable, posted!.DefaultLawnCommanderId);

        var get = await (await _http.GetAsync($"/api/commanders/{_playerId}/default")).Content
            .ReadFromJsonAsync<DefaultLawnCommanderResponse>();
        Assert.Equal(stable, get!.DefaultLawnCommanderId);
    }

    [Fact]
    public async Task Post_invalid_id_returns_400()
    {
        var resp = await _http.PostAsJsonAsync("/api/commanders/default",
            new SetDefaultLawnCommanderRequest { PlayerId = _playerId, CommanderId = "commander:penny" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_zomboss_returns_400_with_not_empire_reason()
    {
        var resp = await _http.PostAsJsonAsync("/api/commanders/default",
            new SetDefaultLawnCommanderRequest
            {
                PlayerId = _playerId,
                CommanderId = "commander:zomboss",
            });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("commander.not-empire", body!["reason"]);
    }

    [Fact]
    public async Task Post_missing_commanderId_returns_400()
    {
        var resp = await _http.PostAsJsonAsync("/api/commanders/default",
            new SetDefaultLawnCommanderRequest { PlayerId = _playerId, CommanderId = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("commander.missing", body!["reason"]);
    }

    [Fact]
    public async Task Post_without_playerId_uses_current_player()
    {
        var stable = "commander:dave";
        var resp = await _http.PostAsJsonAsync("/api/commanders/default",
            new SetDefaultLawnCommanderRequest { CommanderId = stable });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DefaultLawnCommanderResponse>();
        Assert.Equal(stable, body!.DefaultLawnCommanderId);
    }

    [Fact]
    public async Task Post_unknown_player_returns_404()
    {
        var resp = await _http.PostAsJsonAsync("/api/commanders/default",
            new SetDefaultLawnCommanderRequest
            {
                PlayerId = 999999,
                CommanderId = "commander:dave",
            });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Get_unknown_player_returns_404()
    {
        var resp = await _http.GetAsync("/api/commanders/999999/default");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
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
