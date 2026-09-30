using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using FusionRpg.Data.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// condition-glance CG-A4 / shield-sheet SS-A1–A2 — Hot <see cref="IActorLiveStateStore"/> bag
/// projects onto <c>GET /api/actors/{id}/sheet</c> (liveStatuses + shieldSummary + shieldLayers).
/// </summary>
public class ActorSheetHotLiveStateTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    IActorLiveStateStore _live = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    string _baseUrl = "";
    long _playerId;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        _live = new ActorLiveStateStore();

        PowerTuningHub.Configure(
            PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "power-scale.v2.json"))));
        AptitudeTuningHub.Configure(
            AptitudeTuningLoader.Parse(File.ReadAllText(LatestAptitudesPath())));
        FusionRpg.Core.Progression.ProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.ProgressionTuningLoader.Parse(
                File.ReadAllText(Path.Combine(RepoTuningDir(), "progression.v3.json"))));
        FusionRpg.Core.Creatures.Contracts.ContractPolicy.Configure(
            FusionRpg.Core.Creatures.Contracts.ContractTuningLoader.Parse(
                File.ReadAllText(Path.Combine(RepoTuningDir(), "contracts.v1.json"))));
        FusionRpg.Core.Status.StatusPolicy.Configure(
            FusionRpg.Core.Status.StatusTuningLoader.Parse(
                File.ReadAllText(Path.Combine(RepoTuningDir(), "status.v1.json"))));
        FusionRpg.Core.Stats.Derived.StatsTuningHub.Configure(
            FusionRpg.Core.Stats.Derived.StatsTuningLoader.Parse(
                File.ReadAllText(Path.Combine(RepoTuningDir(), "stats.v1.json"))));
        FusionRpg.Core.Creatures.CreatureSpeciesCatalog.ConfigureFromCompiledDefault();
        FusionRpg.Core.Progression.SpeciesProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.SpeciesProgressionTuningLoader.Parse(
                File.ReadAllText(Path.Combine(RepoTuningDir(), "species-progression.v1.json"))));

        var port = GetFreeTcpPort();
        _baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR(o => o.EnableDetailedErrors = true);
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton(_live);
        builder.Services.AddSingleton<IPowerIndexProvider>(sp =>
            new FusionRpg.Server.Power.ServerPowerIndexProvider(
                sp.GetRequiredService<RpgStore>(), PowerTuningHub.Tuning));
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<EffectGrantSession>();
        builder.Services.AddSingleton<IHotCompactor>(sp => new HotCompactor(sp.GetRequiredService<RpgStore>()));
        builder.Services.AddSingleton<CompactionWorker>();
        builder.Services.AddSingleton<UniqueActorService>();
        builder.Services.AddSingleton<EventIngest>();
        builder.Services.AddSingleton<DelveBattleSessionManager>();
        builder.Services.AddSingleton<PlayerConnectionRegistry>(); // RpgHub ctor dependency (NS1.6)
        builder.WebHost.UseUrls(_baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapAuraDerived();
        _app.MapHub<RpgHub>("/hub/rpg");
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
    public void ProjectSheet_cold_empty_store_yields_empty_live_fields()
    {
        var actor = _store.CreateUniqueActor(_playerId, "plant", typeId: 3);
        var sheet = UniqueActorHubCompose.ProjectSheet(_store, actor, _live);

        Assert.Empty(sheet.LiveStatuses);
        Assert.Null(sheet.ShieldSummary);
        Assert.Empty(sheet.ShieldLayers);
    }

    [Fact]
    public void ProjectSheet_hot_bag_fills_statuses_summary_and_layers()
    {
        var actor = _store.CreateUniqueActor(_playerId, "plant", typeId: 4);
        var layers = new[]
        {
            new ActorShieldLayerDto
            {
                ShieldId = "aura:ice",
                ElementId = "ice",
                Current = 40,
                Max = 80,
                Priority = 30,
                SourceId = "grant:aura-ice",
                IsInnate = false,
                Broken = false
            },
            new ActorShieldLayerDto
            {
                ShieldId = "innate:none",
                ElementId = null,
                Current = 60,
                Max = 120,
                Priority = 10,
                SourceId = "innate:4",
                IsInnate = true,
                Broken = false
            }
        };
        _live.Upsert(actor.InstanceId, new ActorLiveState
        {
            LiveStatuses = new[]
            {
                new ActorStatusGlyphDto { StatusId = "burn", RemainingPermille = 500 },
                new ActorStatusGlyphDto { StatusId = "slow", RemainingPermille = 250 }
            },
            ShieldLayers = layers
        });

        var sheet = UniqueActorHubCompose.ProjectSheet(_store, actor, _live);

        Assert.Equal(2, sheet.LiveStatuses.Count);
        Assert.Equal("burn", sheet.LiveStatuses[0].StatusId);
        Assert.Equal(500, sheet.LiveStatuses[0].RemainingPermille);
        Assert.Equal("slow", sheet.LiveStatuses[1].StatusId);

        Assert.NotNull(sheet.ShieldSummary);
        Assert.Equal("ice", sheet.ShieldSummary!.ElementId);
        Assert.Equal(2, sheet.ShieldSummary.Stacks);
        Assert.Equal(100, sheet.ShieldSummary.Current);
        Assert.Equal(200, sheet.ShieldSummary.Max);

        Assert.Equal(2, sheet.ShieldLayers.Count);
        Assert.Equal("aura:ice", sheet.ShieldLayers[0].ShieldId);
        Assert.Equal("ice", sheet.ShieldLayers[0].ElementId);
        Assert.Equal(40, sheet.ShieldLayers[0].Current);
        Assert.Equal("innate:none", sheet.ShieldLayers[1].ShieldId);
        Assert.Null(sheet.ShieldLayers[1].ElementId);
    }

    [Fact]
    public async Task Post_live_state_then_Get_sheet_projects_hot_fields()
    {
        var actor = _store.CreateUniqueActor(_playerId, "plant", typeId: 5);
        var payload = new ActorLiveState
        {
            LiveStatuses = new[]
            {
                new ActorStatusGlyphDto { StatusId = "poison", RemainingPermille = 800 },
                new ActorStatusGlyphDto { StatusId = "stun", RemainingPermille = null }
            },
            ShieldLayers = new[]
            {
                new ActorShieldLayerDto
                {
                    ShieldId = "skill:fire",
                    ElementId = "fire",
                    Current = 25,
                    Max = 50,
                    Priority = 20,
                    SourceId = "grant:skill-fire",
                    IsInnate = false,
                    Broken = false
                },
                new ActorShieldLayerDto
                {
                    ShieldId = "aura:omni",
                    ElementId = null,
                    Current = 75,
                    Max = 100,
                    Priority = 30,
                    SourceId = "grant:aura",
                    IsInnate = false,
                    Broken = false
                }
            }
        };

        // Drain order on bag is Injector responsibility — post already-ordered layers (front = fire).
        var post = await _http.PostAsJsonAsync($"/api/internal/actors/{actor.InstanceId}/live-state", payload);
        Assert.Equal(HttpStatusCode.NoContent, post.StatusCode);

        var resp = await _http.GetAsync($"/api/actors/{actor.InstanceId}/sheet");
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
        var sheet = await resp.Content.ReadFromJsonAsync<HotSheetDto>();
        Assert.NotNull(sheet);
        Assert.Equal(2, sheet!.LiveStatuses!.Count);
        Assert.Equal("poison", sheet.LiveStatuses[0].StatusId);
        Assert.Equal(800, sheet.LiveStatuses[0].RemainingPermille);

        Assert.NotNull(sheet.ShieldSummary);
        Assert.Equal("fire", sheet.ShieldSummary!.ElementId);
        Assert.Equal(2, sheet.ShieldSummary.Stacks);
        Assert.Equal(100, sheet.ShieldSummary.Current);
        Assert.Equal(150, sheet.ShieldSummary.Max);

        Assert.Equal(2, sheet.ShieldLayers!.Count);
        Assert.Equal("skill:fire", sheet.ShieldLayers[0].ShieldId);
        Assert.Equal("aura:omni", sheet.ShieldLayers[1].ShieldId);
    }

    [Fact]
    public async Task Post_live_state_emits_ActorLiveStateChanged()
    {
        var actor = _store.CreateUniqueActor(_playerId, "zombie", typeId: 2);
        var received = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = new HubConnectionBuilder().WithUrl($"{_baseUrl}/hub/rpg").Build();
        hub.On<JsonElement>("ActorLiveStateChanged", payload =>
        {
            if (payload.TryGetProperty("instanceId", out var id))
                received.TrySetResult(id.GetString());
            else
                received.TrySetResult(null);
        });
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.WebGroup);

        var body = """{"liveStatuses":[],"shieldLayers":[]}""";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var post = await _http.PostAsync($"/api/internal/actors/{actor.InstanceId}/live-state", content);
        Assert.Equal(HttpStatusCode.NoContent, post.StatusCode);

        var seenId = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(actor.InstanceId, seenId);
    }

    sealed class HotSheetDto
    {
        public List<HotStatusDto>? LiveStatuses { get; set; }
        public HotShieldSummaryDto? ShieldSummary { get; set; }
        public List<HotShieldLayerDto>? ShieldLayers { get; set; }
    }

    sealed class HotStatusDto
    {
        public string StatusId { get; set; } = "";
        public int? RemainingPermille { get; set; }
    }

    sealed class HotShieldSummaryDto
    {
        public string? ElementId { get; set; }
        public long? Current { get; set; }
        public long? Max { get; set; }
        public int? Stacks { get; set; }
    }

    sealed class HotShieldLayerDto
    {
        public string ShieldId { get; set; } = "";
        public string? ElementId { get; set; }
        public long Current { get; set; }
        public long Max { get; set; }
    }

    static string RepoTuningDir() => Path.Combine(KeepverseRoots.Core(), "data", "tuning");

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
