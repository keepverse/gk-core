using System.Net.Http.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
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

/// <summary>
/// EP1.15 (spec-default-build.md) — cache trigger T3: a specimen level-up broadcasts
/// <c>AptitudesUpdated(unique, instanceId)</c> exactly like an explicit allocate (T1) or preset
/// activate (T2) already do, because nothing about the allocation changed yet the resolved DEFAULT
/// did (the trap §2.16 names). Real SignalR connections throughout, same house style as
/// <c>AptitudesInjectorBroadcastTests.cs</c> — not a mock.
///
/// <para>Covers testing-strategy item 4's two most tractable seams directly against a real host:
/// the debug award route (`UniqueActorEndpoints.cs`, positive AND negative case) and lawn capture
/// ingest (`UniqueActorService.ObserveEvents`, a real lawn-kill award). The expedition-collect seam
/// (`RpgStore.Expeditions.cs` → `ExpeditionEndpoints.cs`) shares the byte-identical
/// <c>if (leveled) BroadcastBestEffort(...)</c> shape proven by these two and is not re-proven end
/// to end here — doing so needs the full battle-resolution/dispatch pipeline for no new code shape,
/// a cost this task's own size does not carry.</para>
/// </summary>
public class UniqueDefaultTriggerTests : IAsyncLifetime
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
        // Fans out to RpgXpCurve.Configure + RpgXpAwards.Configure (ProgressionTuningHub's own doc
        // comment) — needed by both the debug award route and the lawn-kill award this file exercises.
        FusionRpg.Core.Progression.ProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.ProgressionTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "progression.v3.json"))));
        // EP1.15's resolver reads this unconditionally past EP1.14's own best-effort fallback --
        // configured here so a level-up's re-GET resolves the ladder's default rather than Empty.
        AptitudePresetTuningHub.Configure(new AptitudePresetTuning(
            1, 1, SoftMaxPresets: 32, DefaultRowAbsMax: 1000,
            AssignLadder: new AssignLadderTuning(new[]
            {
                AptitudeAutoAssignRules.ActivePreset, AptitudeAutoAssignRules.SpeciesFavour,
                AssignLadder.PostureRung, AptitudeAutoAssignRules.Even
            })));

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
        _app.MapUniqueActors();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    static async Task<HubConnection> ConnectAsWebAsync(string baseUrl)
    {
        var hub = new HubConnectionBuilder().WithUrl($"{baseUrl}/hub/rpg").Build();
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.WebGroup);
        return hub;
    }

    static string? InstanceIdOf(object payload)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("instanceId", out var idProp) ? idProp.GetString() : null;
    }

    // ---- T3 via the debug award route (UniqueActorEndpoints.cs) -----------------------------------

    [Fact]
    public async Task DebugAward_thatLevelsUp_broadcastsAptitudesUpdated_forThatSpecimen()
    {
        var actor = _store.CreateUniqueActor(_playerId, "plant", 4);
        var received = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = await ConnectAsWebAsync(_baseUrl);
        hub.On<object>("AptitudesUpdated", payload => received.TrySetResult(InstanceIdOf(payload)));

        // Comfortably enough to cross the level-1 threshold (100 XP, progression.v3.json's own
        // specimen curve) many times over -- the exact count is not this test's concern.
        var postResp = await _http.PostAsJsonAsync($"/api/unique/actors/{actor.InstanceId}/xp",
            new { delta = 1_000_000L, reason = "ep1.15-level-up" });
        postResp.EnsureSuccessStatusCode();

        var gotInstanceId = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(actor.InstanceId, gotInstanceId);
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task DebugAward_thatDoesNotCrossALevel_neverBroadcasts()
    {
        var actor = _store.CreateUniqueActor(_playerId, "plant", 5);
        var received = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = await ConnectAsWebAsync(_baseUrl);
        hub.On<object>("AptitudesUpdated", payload => received.TrySetResult(InstanceIdOf(payload)));

        // 1 XP against a level-1 curve costing 100 -- ok, but never levels.
        var postResp = await _http.PostAsJsonAsync($"/api/unique/actors/{actor.InstanceId}/xp",
            new { delta = 1L, reason = "ep1.15-no-level" });
        postResp.EnsureSuccessStatusCode();

        // No broadcast within a short, generous window -- proves the guard is `levelsGained > 0`,
        // never "every award, leveled or not".
        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromMilliseconds(750)));
        Assert.NotSame(received.Task, completed);
        await hub.DisposeAsync();
    }

    // ---- T3 via lawn capture ingest (UniqueActorService.ObserveEvents) ----------------------------

    [Fact]
    public async Task LawnKillAward_thatLevelsUpTheKiller_broadcastsAptitudesUpdated_forTheKiller()
    {
        // The killer is the one who gets the lawn-kill XP (AwardUniqueLawnKillUnlocked); the "target"
        // ptr only needs to be present in the payload for the killer branch to run -- it does not
        // need to resolve to a real bound actor (RpgStore.UniqueActors.cs's own killer block runs
        // independent of whether the dying ptr matched a real row).
        var killer = _store.CreateUniqueActor(_playerId, "zombie", 9);
        Assert.True(_store.TryBeginUniqueDeploy(killer.InstanceId, "corr-ep115-killer", "m-ep115").Ok);
        Assert.True(_store.TryAckUniqueSpawn("corr-ep115-killer", "0xEP115KILLER", "m-ep115").Ok);

        var received = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = await ConnectAsWebAsync(_baseUrl);
        hub.On<object>("AptitudesUpdated", payload => received.TrySetResult(InstanceIdOf(payload)));

        var ua = _app.Services.GetRequiredService<UniqueActorService>();
        // specimenLawnKill is 18 XP (progression.v3.json); level 1 costs 100 -- six distinct kill
        // occurrences (the receipt dedupe key) comfortably cross the threshold.
        var batch = Enumerable.Range(0, 8).Select(i => new EventEnvelope
        {
            Kind = "plant.die",
            MatchKey = "m-ep115",
            Payload = new Dictionary<string, object?>
            {
                ["ptr"] = "0xEP115TARGET",
                ["killerPtr"] = "0xEP115KILLER",
                ["lifecycleOccurrence"] = $"ep115-occ-{i}"
            }
        }).ToList();
        ua.ObserveEvents(batch);

        var gotInstanceId = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(killer.InstanceId, gotInstanceId);
        await hub.DisposeAsync();
    }

    // ---- Testing strategy item 4: T1/T3 order-independence ------------------------------------------

    [Fact]
    public async Task ExplicitAllocation_survivesALevelUp_regardlessOfOrder()
    {
        // Sequence A: allocate explicitly FIRST, then level up. Explicit always wins wholesale
        // (D2) regardless of level, so the level-up must not touch it. A fresh level-1 specimen's
        // budget is 0 by construction (PointBudget.UniqueCreatureSourceFromLevel(1) = 0), so it needs
        // a small seed level before it has any budget to allocate against at all.
        var a = _store.CreateUniqueActor(_playerId, "plant", 6);
        (await _http.PostAsJsonAsync($"/api/unique/actors/{a.InstanceId}/xp",
            new { delta = 500L, reason = "ep1.15-order-a-seed" })).EnsureSuccessStatusCode();

        var beforeA = await (await _http.GetAsync($"/api/aptitudes/unique/{a.InstanceId}"))
            .Content.ReadFromJsonAsync<UniqueAptitudesStateDto>();
        var spendA = beforeA!.Budget;
        Assert.True(spendA > 0);
        (await _http.PostAsJsonAsync("/api/aptitudes/unique/allocate",
            new { instanceId = a.InstanceId, shares = new Dictionary<string, long> { ["Might"] = spendA } }))
            .EnsureSuccessStatusCode();

        // The level-up that matters: bigger than the seed above, so the DEFAULT (had there been no
        // explicit allocation) would clearly have grown -- the explicit value must stay put regardless.
        (await _http.PostAsJsonAsync($"/api/unique/actors/{a.InstanceId}/xp",
            new { delta = 1_000_000L, reason = "ep1.15-order-a" })).EnsureSuccessStatusCode();

        var afterA = await (await _http.GetAsync($"/api/aptitudes/unique/{a.InstanceId}"))
            .Content.ReadFromJsonAsync<UniqueAptitudesStateDto>();
        Assert.Equal(spendA, afterA!.Shares["Might"]);

        // Sequence B: level up FIRST (the default's budget grows), then allocate explicitly. The
        // explicit allocation must REPLACE the now-grown default wholesale, not blend with it.
        var b = _store.CreateUniqueActor(_playerId, "plant", 7);
        (await _http.PostAsJsonAsync($"/api/unique/actors/{b.InstanceId}/xp",
            new { delta = 1_000_000L, reason = "ep1.15-order-b" })).EnsureSuccessStatusCode();

        var afterLevelUp = await (await _http.GetAsync($"/api/aptitudes/unique/{b.InstanceId}"))
            .Content.ReadFromJsonAsync<UniqueAptitudesStateDto>();
        // The default grew from the level-up -- confirms the level-up actually took, before the
        // explicit allocation below is asserted to have REPLACED it.
        Assert.True(afterLevelUp!.Budget > 0);
        Assert.True(afterLevelUp.Shares.Values.Sum() > 0);

        var spendB = afterLevelUp.Budget;
        (await _http.PostAsJsonAsync("/api/aptitudes/unique/allocate",
            new { instanceId = b.InstanceId, shares = new Dictionary<string, long> { ["Vigor"] = spendB } }))
            .EnsureSuccessStatusCode();

        var afterB = await (await _http.GetAsync($"/api/aptitudes/unique/{b.InstanceId}"))
            .Content.ReadFromJsonAsync<UniqueAptitudesStateDto>();
        Assert.Equal(spendB, afterB!.Shares["Vigor"]);
        // No leftover default points on any OTHER aptitude -- explicit replaced wholesale, never blended.
        Assert.Equal(spendB, afterB.Shares.Values.Sum());
    }

    sealed class UniqueAptitudesStateDto
    {
        public string InstanceId { get; set; } = "";
        public long Budget { get; set; }
        public Dictionary<string, long> Shares { get; set; } = new();
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
