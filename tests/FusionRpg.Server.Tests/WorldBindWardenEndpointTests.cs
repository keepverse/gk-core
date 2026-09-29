using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Server.Tests.Notifications;

namespace FusionRpg.Server.Tests;

/// <summary>
/// world-stage W29: the `POST /api/world/{worldId}/bind-warden` endpoint — the first production
/// caller of <see cref="RpgStore.BindAsWarden"/>. This file used to prove the two-step failure mode
/// (step 1 charged the soul fee, step 2's admission refused, step 1 was not rolled back).
/// warden-freeze-fix (RulesetVersion 13) retired the verb: admission refuses every bind with
/// `warden.retired`, and the endpoint now asks admission before step 1. So the contract proven here
/// is that a bind is refused by name and nothing is charged, bound, or filed — including the shape
/// that used to succeed and the retry that used to land it.
/// </summary>
[Trait("VerificationId", "server.world-warden")]
[Collection("NotificationHub")]
public class WorldBindWardenEndpointTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    const string WorldId = "w29-bind-warden";

    static readonly CreatureSpeciesDef Species = CreatureSpeciesCatalog.All
        .First(s => s.Acquisition != CreatureAcquisition.CaptureOnly && s.TraitPool.Count > 0);

    public async Task InitializeAsync()
    {
        ConfigureWorldTuningOnce();

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _store.AwardSouls(1, 50_000, "seed", "ops-bank");

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<FusionRpg.Server.DelveBattleSessionManager>();
        builder.WebHost.UseUrls(baseUrl);
        builder.Services.AddNotificationEndpointStubs(); // NS3.4 regression: MapWorld's /commit route now needs these to resolve
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        _app.MapWorld();
        _app.MapWorldWarden();
        var test = _app.MapGroup("/api/test");
        test.MapWorldTest();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

        var created = await _http.PostAsJsonAsync("/api/test/world/create", new
        {
            worldId = WorldId, templateId = "first-light", seed = "7"
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    /// <summary>An unbound creature with a free capacity slot — <c>MintCreature</c> auto-binds up to base
    /// capacity, so a plain bindable creature needs a slot freed first, matching
    /// <c>WardenContractTests.cs</c>'s own established fixture.</summary>
    string MintUnboundWithFreeSlot()
    {
        string Mint()
        {
            var (specimen, _) = _store.MintCreature(1, new CreatureMintSpec
            {
                SpeciesId = Species.SpeciesId,
                Side = Species.Side,
                GameTypeId = Species.GameTypeId,
                Rarity = Species.BaseRarity.ToId(),
                Variant = "normal",
                ElementPrimary = Species.ElementPrimary.ToElementId(),
                ElementSecondary = Species.ElementSecondary?.ToElementId(),
                TraitIds = new List<string> { Species.TraitPool[0] },
                Origin = "summon"
            });
            return specimen.Actor.InstanceId;
        }

        var bound = new List<string>();
        for (var i = 0; i < ContractPolicy.BaseSlots; i++) bound.Add(Mint());
        var id = Mint();
        Assert.Null(_store.GetContract(id));
        Assert.True(_store.ReleaseContract(1, bound[0]).Ok);
        return id;
    }

    async Task<(int Status, BindWardenResultDto Body)> Call(string commanderId, string sectorId, string instanceId)
    {
        var res = await _http.PostAsJsonAsync($"/api/world/{WorldId}/bind-warden", new
        {
            commanderId, sectorId, instanceId
        });
        return ((int)res.StatusCode, (await res.Content.ReadFromJsonAsync<BindWardenResultDto>())!);
    }

    [Fact]
    public async Task A_bind_is_refused_as_retired_before_any_soul_fee_contract_or_order()
    {
        var instanceId = MintUnboundWithFreeSlot();
        var before = _store.GetSoulBalance(1).Balance;

        // The exact call RulesetVersion 12 accepted: the real commander, its own homeworld, a
        // bindable creature with a free slot. Then the old failure shape (an unknown commander,
        // which used to charge before refusing), then a retry of the first. Every one is refused by
        // name, and the store reads back unchanged each time.
        foreach (var commander in new[] { "dave", "nobody", "dave" })
        {
            var (status, body) = await Call(commander, "homeworld", instanceId);

            Assert.Equal(400, status);
            Assert.False(body.Ok);
            Assert.Equal("warden.retired", body.Reason);
            Assert.Null(_store.GetContract(instanceId));
            Assert.Equal(before, _store.GetSoulBalance(1).Balance);
            Assert.Empty(_store.ListWorldCommands(WorldId, 0));
        }
    }

    static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    static bool _tuningConfigured;

    static void ConfigureWorldTuningOnce()
    {
        if (_tuningConfigured) return;
        var tuningDir = Path.Combine(FindRepoRoot(), "data", "tuning");
        string Read(string name) => File.ReadAllText(Path.Combine(tuningDir, name));
        FusionRpg.Core.World.Loam.LoamPolicy.Configure(
            FusionRpg.Core.World.Loam.LoamTuningLoader.Parse(Read("loam.v5.json")));
        FusionRpg.Core.World.WorldTuningHub.Configure(
            FusionRpg.Core.World.WorldTuningLoader.Parse(Read("world.v6.json")));
        FusionRpg.Core.World.Ai.WorldAiPolicy.Configure(
            FusionRpg.Core.World.Ai.WorldAiTuningLoader.Parse(Read("ai.v3.json")));
        // Server.Tests' own PowerAndAptitudeTuningTestBootstrap module initializer configures
        // Power/Aptitude/DerivedStat/Rung/Aura only — ContractPolicy (this file's own MintCreature /
        // BindAsWarden fixtures) needs its own configure, matching every other Policy this file reads.
        ContractPolicy.Configure(ContractTuningLoader.Parse(Read("contracts.v1.json")));
        _tuningConfigured = true;
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
