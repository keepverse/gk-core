using System.Net.Http.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Effects;
using FusionRpg.Data;
using FusionRpg.Data.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>commander-surface P2: POST default must notify InjectorGroup so session cache refreshes
/// before the next board.start — same class of bug fixed for AptitudesUpdated 2026-08-30.</summary>
[Collection(CommanderDirectoryCollection.Name)]
public class CommanderSnapshotBroadcastTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    string _baseUrl = "";
    long _playerId;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();

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
        builder.Services.AddSingleton<FusionRpg.Server.DelveBattleSessionManager>();
        builder.Services.AddSingleton<PlayerConnectionRegistry>(); // RpgHub ctor dependency (NS1.6)
        builder.WebHost.UseUrls(_baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        _app.MapCommanders();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    [Fact]
    public async Task Post_default_notifies_a_client_joined_as_injector_not_just_web()
    {
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new HubConnectionBuilder().WithUrl($"{_baseUrl}/hub/rpg").Build();
        hub.On<object>("CommandersUpdated", _ => received.TrySetResult(true));
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.InjectorGroup);

        var postResp = await _http.PostAsJsonAsync("/api/commanders/default",
            new SetDefaultLawnCommanderRequest
            {
                PlayerId = _playerId,
                CommanderId = "commander:dave",
            });
        postResp.EnsureSuccessStatusCode();

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(got, "an injector-group connection never received CommandersUpdated");
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task Post_default_still_notifies_a_client_joined_as_web()
    {
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new HubConnectionBuilder().WithUrl($"{_baseUrl}/hub/rpg").Build();
        hub.On<object>("CommandersUpdated", _ => received.TrySetResult(true));
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.WebGroup);

        var postResp = await _http.PostAsJsonAsync("/api/commanders/default",
            new SetDefaultLawnCommanderRequest
            {
                PlayerId = _playerId,
                CommanderId = "commander:dave",
            });
        postResp.EnsureSuccessStatusCode();

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(got, "regression: the web-group notification broke");
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task List_after_reset_returns_implicit_Dave_for_cache_poll()
    {
        _store.Reset();
        _playerId = _store.GetCurrentPlayerId();

        var body = await (await _http.GetAsync($"/api/commanders/{_playerId}"))
            .Content.ReadFromJsonAsync<CommanderListResponse>();

        Assert.NotNull(body);
        Assert.Equal("commander:dave", body!.DefaultLawnCommanderId);
        Assert.Single(body.Commanders);
        Assert.True(body.Commanders[0].IsDefault);
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
