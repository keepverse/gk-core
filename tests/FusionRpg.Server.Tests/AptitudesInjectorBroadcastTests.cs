using System.Net.Http.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Power;
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
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>Real bug, found 2026-08-30 verifying aura-skill T5/T6's own "wired end-to-end" claim
/// against a live game: an injector SignalR connection only ever joins <c>RpgConstants.InjectorGroup</c>
/// (<c>RpgHub.cs:27-28</c>), but <c>AptitudeEndpoints.BroadcastBestEffort</c> sent "AptitudesUpdated"
/// to <c>WebGroup</c> only — so the injector's own <c>RpgClient.cs:93</c> handler (which reloads
/// <c>CheatState.CommanderAllocation</c>) could never fire for a live allocation change; only a fresh
/// injector session-start ever picked it up. `PvzStatsUpdated` (`Program.cs:961-962`) already sends to
/// both groups — this proves the fixed endpoint now follows that same, already-established pattern,
/// against a REAL SignalR connection (not a mock), matching this project's own test-house style.</summary>
public class AptitudesInjectorBroadcastTests : IAsyncLifetime
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

        FusionRpg.Core.Power.PowerTuningHub.Configure(
            FusionRpg.Core.Power.PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "power-scale.v2.json"))));
        FusionRpg.Core.Stats.Aptitudes.AptitudeTuningHub.Configure(
            FusionRpg.Core.Stats.Aptitudes.AptitudeTuningLoader.Parse(File.ReadAllText(LatestAptitudesPath())));
        FusionRpg.Core.Progression.ProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.ProgressionTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "progression.v3.json"))));

        var port = GetFreeTcpPort();
        _baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR(o => o.EnableDetailedErrors = true);
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<IPowerIndexProvider>(sp =>
            new FusionRpg.Server.Power.ServerPowerIndexProvider(sp.GetRequiredService<RpgStore>(), PowerTuningHub.Tuning));
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
        _app.MapAptitudes();
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
    public async Task Allocate_notifies_a_client_joined_as_injector_not_just_web()
    {
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new HubConnectionBuilder().WithUrl($"{_baseUrl}/hub/rpg").Build();
        hub.On<object>("AptitudesUpdated", _ => received.TrySetResult(true));
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.InjectorGroup);

        var postResp = await _http.PostAsJsonAsync("/api/aptitudes/allocate",
            new { playerId = _playerId, shares = new Dictionary<string, long> { ["Might"] = 1 } });
        postResp.EnsureSuccessStatusCode();

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(got, "an injector-group connection never received AptitudesUpdated");
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task Allocate_still_notifies_a_client_joined_as_web()
    {
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new HubConnectionBuilder().WithUrl($"{_baseUrl}/hub/rpg").Build();
        hub.On<object>("AptitudesUpdated", _ => received.TrySetResult(true));
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.WebGroup);

        var postResp = await _http.PostAsJsonAsync("/api/aptitudes/allocate",
            new { playerId = _playerId, shares = new Dictionary<string, long> { ["Might"] = 1 } });
        postResp.EnsureSuccessStatusCode();

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(got, "regression: the pre-existing web-group notification broke");
        await hub.DisposeAsync();
    }

    static string RepoTuningDir() => Path.Combine(FindRepoRoot(), "data", "tuning");

    static string LatestAptitudesPath()
    {
        var dir = RepoTuningDir();
        var best = Directory.EnumerateFiles(dir, "aptitudes.v*.json")
            .Select(Path.GetFileName)
            .Select(n => (Name: n!, Match: System.Text.RegularExpressions.Regex.Match(n!, @"^aptitudes\.v(\d+)\.json$")))
            .Where(x => x.Match.Success)
            .OrderByDescending(x => int.Parse(x.Match.Groups[1].Value))
            .First();
        return Path.Combine(dir, best.Name);
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
        return KeepverseRoots.Core();
    }
}
