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
/// build-preset BP1.1 (spec-gate-services.md) — proves <see cref="PatronService.SetAsync"/>, lifted
/// out of <c>PatronEndpoints.cs</c>'s <c>/set</c> lambda, is byte-identical to what the route did:
/// same stored row, same reason, and the same injector <c>patron.aura</c> push with its inbox
/// fallback. <c>PatronE2ETests</c> (FusionRpg.E2E.Tests) proves the route itself needed no edit;
/// this suite proves the extracted service the route now calls behaves exactly like the route used
/// to compute inline, and that a future build-preset applier calling this same service gets the
/// real broadcast + injector push, not a second, possibly-drifting copy of it.
/// </summary>
public class PatronServiceTests : IDisposable
{
    readonly List<DataTestStore> _stores = new();

    public PatronServiceTests()
    {
        // Same tuning bootstrap AtomPushServicePatronCallbackTests.cs needs for the same reason:
        // MintCreature's real call chain auto-binds a contract, and RefreshRuntimeState's Compute
        // reads the player's own Θ and the real PatronPolicy.Aura formula — none of it covered by
        // this assembly's [ModuleInitializer] bootstrap.
        var tuningDir = Path.Combine(RepoRoot(), "data", "tuning");
        string Read(string name) => File.ReadAllText(Path.Combine(tuningDir, name));
        SummoningTuningHub.Configure(SummoningTuningLoader.Parse(Read("summoning.v1.json")));
        ContractPolicy.Configure(ContractTuningLoader.Parse(Read("contracts.v1.json")));
        SoulEarnPolicy.Configure(SoulEarnTuningLoader.Parse(Read("souls.v1.json")));
        Core.Progression.ProgressionTuningHub.Configure(
            Core.Progression.ProgressionTuningLoader.Parse(Read("progression.v3.json")));
        Core.Creatures.Patron.PatronPolicy.Configure(
            Core.Creatures.Patron.PatronTuningLoader.Parse(Read("patron.v1.json")));
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
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
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

    static (RpgStore Store, IHubContext<RpgHub> Hub, InjectorCommandInbox Inbox, PatronService Service) NewService(RpgStore store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        var provider = services.BuildServiceProvider();
        var hub = provider.GetRequiredService<IHubContext<RpgHub>>();
        var inbox = new InjectorCommandInbox();
        return (store, hub, inbox, new PatronService(store, hub, inbox));
    }

    static async Task<(WebApplication App, string BaseUrl, HttpClient Http)> HostRoute(RpgStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<PatronService>();
        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        builder.WebHost.UseUrls(baseUrl);
        var app = builder.Build();
        app.MapPatron();
        await app.StartAsync();
        return (app, baseUrl, new HttpClient { BaseAddress = new Uri(baseUrl) });
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
    public async Task Direct_service_call_and_the_HTTP_route_store_the_same_row_and_return_the_same_reason_on_success()
    {
        var storeA = NewStore();
        var creatureA = Mint(storeA, 1);
        var (_, _, _, serviceA) = NewService(storeA);
        var outcomeA = await serviceA.SetAsync(1, creatureA, "corr-a");
        var rowA = storeA.GetPatron(1);

        var storeB = NewStore();
        var creatureB = Mint(storeB, 1);
        var (app, _, http) = await HostRoute(storeB);
        try
        {
            var resp = await http.PostAsJsonAsync("/api/patron/set", new { instanceId = creatureB, correlationId = "corr-b" });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var rowB = storeB.GetPatron(1);

            Assert.True(outcomeA.Ok);
            Assert.Equal("", outcomeA.Reason);
            Assert.NotNull(rowA);
            Assert.NotNull(rowB);
            // Not the same instance id across two independent stores — the same SHAPE of outcome:
            // a fresh (never-before-set) designation is free (Revision 1), stored against the
            // creature just minted in each store.
            Assert.Equal(creatureA, rowA!.InstanceId);
            Assert.Equal(creatureB, rowB!.InstanceId);
            Assert.Equal(rowA.Revision, rowB.Revision);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Direct_service_call_and_the_HTTP_route_return_the_same_refusal_reason()
    {
        var storeA = NewStore();
        var (_, _, _, serviceA) = NewService(storeA);
        var outcomeA = await serviceA.SetAsync(1, "no-such-instance", "corr-a");

        var storeB = NewStore();
        var (app, _, http) = await HostRoute(storeB);
        try
        {
            var resp = await http.PostAsJsonAsync("/api/patron/set", new { instanceId = "no-such-instance", correlationId = "corr-a" });

            Assert.False(outcomeA.Ok);
            Assert.Equal("specimen.missing", outcomeA.Reason);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal(outcomeA.Reason, body.GetProperty("reason").GetString());
            Assert.Null(storeA.GetPatron(1));
            Assert.Null(storeB.GetPatron(1));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task SetAsync_enqueues_the_injector_patron_aura_command_exactly_as_the_route_did()
    {
        var store = NewStore();
        var creature = Mint(store, 1);
        var (_, _, inbox, service) = NewService(store);

        var outcome = await service.SetAsync(1, creature, "corr-1");
        Assert.True(outcome.Ok);

        var expected = PatronEndpoints.TryBuildPatronCommand(store);
        Assert.NotNull(expected);
        var expectedPayload = Assert.IsType<Dictionary<string, object?>>(expected!.Payload);

        var drained = inbox.Drain();
        var cmd = Assert.Single(drained, c => c.Name == "patron.aura");
        // InjectorCommandInbox.Enqueue round-trips the payload through JSON (MakeDurable) so a
        // request-scoped JsonElement never outlives the request — the drained shape is a JsonElement,
        // not the Dictionary the route/service built it from.
        var cmdPayload = Assert.IsType<System.Text.Json.JsonElement>(cmd.Payload);

        Assert.Equal(Convert.ToInt64(expectedPayload["playerId"]), cmdPayload.GetProperty("playerId").GetInt64());
        Assert.Equal((string?)expectedPayload["elementPrimary"], cmdPayload.GetProperty("elementPrimary").GetString());
        Assert.Equal(Convert.ToInt64(expectedPayload["powerMilli"]), cmdPayload.GetProperty("powerMilli").GetInt64());
        Assert.Equal(Convert.ToInt64(expectedPayload["defenseMilli"]), cmdPayload.GetProperty("defenseMilli").GetInt64());
    }
}
