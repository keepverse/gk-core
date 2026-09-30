using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using FusionRpg.Data.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>save-identity SE4.22 ("Zomboss stops being a player row") — `POST /api/zomboss/deploy`
/// against a real in-process host (same pattern as <c>SpeciesBuildEndpointsTests</c>): `matchKey` is
/// required, the save resolves from the match BEFORE the mint (a refused deploy leaves no orphan
/// specimen), and two saves' Zomboss deploys land on their own save, never each other's.</summary>
public class ZombossDeployEndpointsTests : IAsyncLifetime
{
    static readonly CreatureSpeciesDef CatalogSpecies =
        CreatureSpeciesCatalog.All.First(s => s.DeployMode != CreatureDeployMode.HypnoAlly);

    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        // MintForEmpire -> SummonRoller.RollTraits -> FusionRoller.SlotsFor reads StarPolicy.Tuning
        // (data/tuning/fusion.v{n}.json), which this assembly's own [ModuleInitializer] bootstrap does
        // not cover (no prior Server.Tests file minted a creature through the fusion-star path) --
        // configured here from the real shipped file, exactly like BuildSquadEquippedActionsTests'
        // identical comment for the same gap.
        FusionRpg.Core.Creatures.Fusion.StarPolicy.Configure(
            FusionRpg.Core.Creatures.Fusion.FusionTuningLoader.Parse(
                File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "fusion.v2.json"))));

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR(o => o.EnableDetailedErrors = true);
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<UniqueActorService>();
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        _app.MapZombossDeploy();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    /// <summary>Same real path <c>SaveIdentityFixtures.StartRunFor</c> uses (Data.Tests): stamps a real
    /// `board.start` run onto <paramref name="saveId"/>, so `SaveOfMatch` resolves it exactly as a real
    /// match would.</summary>
    void StartRunFor(long saveId, string matchKey)
    {
        Assert.True(_store.SetCurrentPlayer(saveId));
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "board.start",
            MatchKey = matchKey,
            Payload = new { levelName = "test", levelType = "adventure" },
        });
    }

    long RosterCount(long saveId) => _store.ListCreatureRoster(saveId).Items.Count;

    (string PlayerId, string EmpireId) OwnerOf(string instanceId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT player_id, empire_id FROM rpg_unique_actors WHERE instance_id=$i;";
        cmd.Parameters.AddWithValue("$i", instanceId);
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        return (r.GetInt64(0).ToString(), r.IsDBNull(1) ? "" : r.GetString(1));
    }

    [Fact]
    public async Task Missing_matchKey_is_refused_and_mints_nothing()
    {
        var before = RosterCount(1);

        var resp = await _http.PostAsJsonAsync("/api/zomboss/deploy", new
        {
            speciesId = CatalogSpecies.SpeciesId,
            matchSeed = 1UL,
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(before, RosterCount(1));
    }

    [Fact]
    public async Task A_matchKey_that_resolves_to_no_run_is_refused_with_match_unresolved_and_mints_nothing()
    {
        var before = RosterCount(1);

        var resp = await _http.PostAsJsonAsync("/api/zomboss/deploy", new
        {
            speciesId = CatalogSpecies.SpeciesId,
            matchSeed = 1UL,
            matchKey = "no-such-match",
        });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("match_unresolved", body.GetProperty("error").GetString());
        Assert.Equal(before, RosterCount(1));
    }

    [Fact]
    public async Task Zomboss_minted_into_save_1_is_not_save_2s()
    {
        var save2 = _store.CreatePlayer("Second");
        StartRunFor(1, "m-save-1");
        StartRunFor(save2.Id, "m-save-2");

        var resp1 = await _http.PostAsJsonAsync("/api/zomboss/deploy", new
        {
            speciesId = CatalogSpecies.SpeciesId,
            matchSeed = 5UL,
            matchKey = "m-save-1",
        });
        var resp2 = await _http.PostAsJsonAsync("/api/zomboss/deploy", new
        {
            speciesId = CatalogSpecies.SpeciesId,
            matchSeed = 5UL,
            matchKey = "m-save-2",
        });

        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

        var body1 = await resp1.Content.ReadFromJsonAsync<JsonElement>();
        var body2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();
        var instance1 = body1.GetProperty("specimen").GetProperty("actor").GetProperty("instanceId").GetString()!;
        var instance2 = body2.GetProperty("specimen").GetProperty("actor").GetProperty("instanceId").GetString()!;

        var owner1 = OwnerOf(instance1);
        var owner2 = OwnerOf(instance2);
        Assert.Equal("1", owner1.PlayerId);
        Assert.Equal("zomboss", owner1.EmpireId);
        Assert.Equal(save2.Id.ToString(), owner2.PlayerId);
        Assert.Equal("zomboss", owner2.EmpireId);
        Assert.NotEqual(owner1.PlayerId, owner2.PlayerId);
    }

    /// <summary>save-identity SE4.28 ("Injector ownership"): DeployAsync stamps the specimen's own
    /// empire/controller onto the real pvz.spawn.extra command it sends — read off the real inbox a
    /// real Injector polls, never a unit test of the payload-building code in isolation.</summary>
    [Fact]
    public async Task Deploy_sends_empireId_and_controller_on_the_spawn_command()
    {
        StartRunFor(1, "m-se4.28");
        var inbox = _app.Services.GetRequiredService<InjectorCommandInbox>();
        inbox.Drain(); // clear anything queued by earlier setup in this run

        var resp = await _http.PostAsJsonAsync("/api/zomboss/deploy", new
        {
            speciesId = CatalogSpecies.SpeciesId,
            matchSeed = 9UL,
            matchKey = "m-se4.28",
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var commands = inbox.Drain();
        var spawnCmd = Assert.Single(commands, c => c.Name == "pvz.spawn.extra");
        var payload = (JsonElement)spawnCmd.Payload!;
        Assert.Equal("zomboss", payload.GetProperty("empireId").GetString());
        Assert.Equal("ai", payload.GetProperty("controller").GetString());
    }

    [Fact]
    public async Task DeployAsync_records_no_ExtraSpawnFired_for_Zombosss_own_deploy()
    {
        // save-identity SE4.23: the activity rollup is the human save's own play-activity record. Real
        // caller through the real endpoint (T24) -- UniqueActorService.DeployAsync's own guard, not a
        // Data.Tests unit test of the predicate alone.
        StartRunFor(1, "m-activity");
        var before = _store.GetPvzActivityRollup(1)!.ExtraSpawnsFired;

        var resp = await _http.PostAsJsonAsync("/api/zomboss/deploy", new
        {
            speciesId = CatalogSpecies.SpeciesId,
            matchSeed = 7UL,
            matchKey = "m-activity",
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(before, _store.GetPvzActivityRollup(1)!.ExtraSpawnsFired);
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
