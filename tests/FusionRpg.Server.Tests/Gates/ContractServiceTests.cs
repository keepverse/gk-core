using System.Net;
using System.Net.Http.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using FusionRpg.Server.Gates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests.Gates;

/// <summary>
/// build-preset BP1.2 (spec-gate-services.md) — proves <see cref="ContractService.BindAsync"/> and
/// <see cref="ContractService.ReleaseAsync"/>, lifted out of <c>ContractEndpoints.cs</c>'s
/// <c>/bind</c> and <c>/release</c> lambdas, are byte-identical to what the routes did: same
/// stored row, same reason. <c>ContractE2ETests</c> (FusionRpg.E2E.Tests) proves the routes
/// themselves needed no edit; this suite proves the extracted service they now call behaves
/// exactly like the routes used to, so a future build-preset field applier calling this same
/// service gets the real broadcast, not a second copy of it.
/// </summary>
public class ContractServiceTests : IDisposable
{
    readonly List<DataTestStore> _stores = new();

    public ContractServiceTests()
    {
        // Same tuning bootstrap AtomPushServicePatronCallbackTests.cs/PatronServiceTests.cs need:
        // MintCreature's real call chain auto-binds a contract, which needs these configured — not
        // covered by this assembly's [ModuleInitializer] bootstrap.
        var tuningDir = Path.Combine(RepoRoot(), "data", "tuning");
        string Read(string name) => File.ReadAllText(Path.Combine(tuningDir, name));
        SummoningTuningHub.Configure(SummoningTuningLoader.Parse(Read("summoning.v1.json")));
        ContractPolicy.Configure(ContractTuningLoader.Parse(Read("contracts.v1.json")));
        SoulEarnPolicy.Configure(SoulEarnTuningLoader.Parse(Read("souls.v1.json")));
    }

    public void Dispose()
    {
        foreach (var s in _stores) s.Dispose();
    }

    RpgStore NewStore()
    {
        var ts = DataTestStore.Create();
        _stores.Add(ts);
        return ts.Store;
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static readonly CreatureSpeciesDef Species = CreatureSpeciesCatalog.All
        .First(s => s.Acquisition != CreatureAcquisition.CaptureOnly);

    static string Mint(RpgStore store, long playerId) =>
        store.MintCreature(playerId, new CreatureMintSpec
        {
            SpeciesId = Species.SpeciesId,
            Side = Species.Side,
            GameTypeId = Species.GameTypeId,
            Rarity = Species.BaseRarity.ToId(),
            Variant = "normal",
            ElementPrimary = Species.ElementPrimary.ToElementId(),
            ElementSecondary = Species.ElementSecondary?.ToElementId(),
            TraitIds = new List<string> { Species.TraitPool[0] },
            Origin = "summon",
        }).Item1.Actor.InstanceId;

    static (RpgStore Store, ContractService Service) NewService(RpgStore store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        var provider = services.BuildServiceProvider();
        var hub = provider.GetRequiredService<IHubContext<RpgHub>>();
        return (store, new ContractService(store, hub));
    }

    static async Task<(WebApplication App, HttpClient Http)> HostRoute(RpgStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<ContractService>();
        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        builder.WebHost.UseUrls(baseUrl);
        var app = builder.Build();
        app.MapContracts();
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(baseUrl) });
    }

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Direct_service_call_and_the_HTTP_route_release_the_same_way_and_return_the_same_reason()
    {
        var storeA = NewStore();
        var creatureA = Mint(storeA, 1); // minting binds for free
        var (_, serviceA) = NewService(storeA);
        var outcomeA = await serviceA.ReleaseAsync(1, creatureA);

        var storeB = NewStore();
        var creatureB = Mint(storeB, 1);
        var (app, http) = await HostRoute(storeB);
        try
        {
            var resp = await http.PostAsJsonAsync("/api/contracts/release", new { instanceId = creatureB });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            Assert.True(outcomeA.Ok);
            Assert.Equal("", outcomeA.Reason);
            Assert.NotNull(outcomeA.Contract);
            Assert.False(outcomeA.Contract!.Bound);

            var rowB = storeB.ListContracts(1).Single(c => c.InstanceId == creatureB);
            Assert.False(rowB.Bound);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Direct_service_call_and_the_HTTP_route_return_the_same_refusal_reason_on_bind()
    {
        var storeA = NewStore();
        var (_, serviceA) = NewService(storeA);
        var outcomeA = await serviceA.BindAsync(1, "no-such-instance");

        var storeB = NewStore();
        var (app, http) = await HostRoute(storeB);
        try
        {
            var resp = await http.PostAsJsonAsync("/api/contracts/bind", new { instanceId = "no-such-instance" });

            Assert.False(outcomeA.Ok);
            Assert.Equal("specimen.missing", outcomeA.Reason);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal(outcomeA.Reason, body.GetProperty("reason").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReleaseAsync_broadcasts_best_effort_with_no_connected_clients_and_never_throws()
    {
        // The route's own NotifyAsync try/catches SignalR failures ("the write is durable, the
        // next read reconciles") — the service must keep that exact shape, or a disconnected web
        // client would turn a successful release into a 500.
        var store = NewStore();
        var creature = Mint(store, 1);
        var (_, service) = NewService(store);

        var outcome = await service.ReleaseAsync(1, creature);

        Assert.True(outcome.Ok);
    }
}
