using System.Net.Http.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using FusionRpg.Data.Abstractions;
using FusionRpg.Data.Sqlite;
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

/// <summary>`species-build` T2.2 (module 5, `creature-type-allocation`) — GET/POST
/// `/api/aptitudes/species/*` against a real, minimal in-process host (same pattern as
/// `AptitudeEndpointsTests`/`AptitudesInjectorBroadcastTests`, not a mock): a baseline read with no
/// override, an override round-trip, budget refusal, and the ⛔ both-groups broadcast this module's
/// own spec calls out by name (the exact defect already found once for the Commander endpoint).</summary>
public class SpeciesAllocationEndpointsTests : IAsyncLifetime
{
    const int FumeshroomCreatureTypeId = 60007;

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
        FusionRpg.Core.Progression.SpeciesProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.SpeciesProgressionTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "species-progression.v1.json"))));
        CreatureSpeciesCatalog.ConfigureFromCompiledDefault();
        SpeciesBuildPlanCatalog.Configure(new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal)
        {
            ["fumeshroom"] = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["Might"] = 500, ["Vigor"] = 300, ["Fortitude"] = 200
            }
        });
        // The one remaining test here that mutates state now does so via `POST /api/species-build/respec`
        // (the old `/api/aptitudes/species/allocate` bypass was retired) -- needs the same tuning that
        // endpoint's own SpeciesBuildEndpointsTests.cs configures.
        FusionRpg.Core.Creatures.Generation.SpeciesBuildTuningHub.Configure(new FusionRpg.Core.Creatures.Generation.SpeciesBuildTuning(
            SchemaVersion: 1, Version: 1,
            ParityFloorPermille: 50, ParityCeilingPermille: 200,
            LeanMinPermille: 350, LeanMaxPermille: 600,
            CrowdingFactor: 633, SecondarySharePermille: 300,
            MaxAptitudesPerSpecies: 5, MinAptitudesPerSpecies: 2,
            RespecBasePrice: 50, RespecEscalationPermille: 500, RespecDecayDays: 3,
            UniqueRespecBasePrice: 50, UniqueRespecEscalationPermille: 500, UniqueRespecDecayDays: 3,
            LeanSignalWeights: FusionRpg.Core.Creatures.Generation.LeanSignalWeights.Zero));

        SeedSpeciesLevel(_playerId, FumeshroomCreatureTypeId, level: 21, "fumeshroom"); // source = 20

        var port = GetFreeTcpPort();
        _baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR(o => o.EnableDetailedErrors = true);
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<IPowerIndexProvider>(sp =>
            new FusionRpg.Server.Power.ServerPowerIndexProvider(sp.GetRequiredService<RpgStore>(), PowerTuningHub.Tuning));
        // RpgHub's own constructor deps (RpgHub.cs) -- without these DI resolution fails the moment a
        // client invokes any hub method, which manifests client-side as a silently dead connection
        // rather than a clear server error (found running this test for real, mirroring
        // AptitudesInjectorBroadcastTests' own registration list).
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<EffectGrantSession>();
        builder.Services.AddSingleton<IHotCompactor>(sp => new HotCompactor(sp.GetRequiredService<RpgStore>()));
        builder.Services.AddSingleton<CompactionWorker>();
        builder.Services.AddSingleton<UniqueActorService>();
        builder.Services.AddSingleton<EventIngest>();
        builder.Services.AddSingleton<FusionRpg.Server.DelveBattleSessionManager>();
        builder.WebHost.UseUrls(_baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        _app.MapAptitudes();
        _app.MapSpeciesBuild();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    void SeedSpeciesLevel(long playerId, int creatureTypeId, long level, string speciesId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_actor_progression(
              save_id, empire_id, kind, type_id, level, xp, highest_level, demotion_count, revision, updated_utc, scope_key)
            VALUES ($p, $e, 'species', $tid, $lvl, 0, $lvl, 0, 0, $now, $sk);
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
                cmd.Parameters.AddWithValue("$e", _store.HumanEmpireOf(playerId).Value);
        cmd.Parameters.AddWithValue("$tid", creatureTypeId);
        cmd.Parameters.AddWithValue("$lvl", level);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$sk", speciesId);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Get_withNoOverride_returnsThePlansBaseline_notZero()
    {
        var resp = await _http.GetAsync($"/api/aptitudes/species/{_playerId}/fumeshroom");
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<SpeciesStateDto>();

        Assert.NotNull(body);
        Assert.True(body!.Shares["Might"] > body.Shares["Vigor"]);
        Assert.True(body.Spent > 0); // NOT the silent-zero this module's spec calls out by name
        Assert.True(body.WithinBudget);
    }

    [Fact]
    public async Task Get_unknownSpecies_returnsBadRequest()
    {
        var resp = await _http.GetAsync($"/api/aptitudes/species/{_playerId}/not-a-real-species");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // Allocate_overridesTheBaseline_androundTripsThroughGet, Allocate_overspending_isRefused_scopeLocally,
    // Allocate_notifies_a_client_joined_as_injector_not_just_web and Allocate_still_notifies_a_client_joined_as_web
    // all POSTed to `/api/aptitudes/species/allocate`, RETIRED by species-build-todo.md T4.3/Checkpoint 5's own
    // named follow-up (owner decision, 2026-09-05: "retire it now") — it wrote a CreatureType override with zero
    // pricing awareness, a live bypass of the `species-respec` economy. Their coverage (override round-trips,
    // scope-local budget refusal, both-groups broadcast) now lives on `POST /api/species-build/respec` in
    // `SpeciesBuildEndpointsTests.cs`, which exercises the same real write path through the priced/free surface
    // that replaced this one.

    [Fact]
    public async Task Get_exposesTheShippedBaselineSeparately_soAnOverrideRendersAsADeviation()
    {
        var before = await (await _http.GetAsync($"/api/aptitudes/species/{_playerId}/fumeshroom"))
            .Content.ReadFromJsonAsync<SpeciesStateDto>();
        Assert.False(before!.HasOverride);
        Assert.Equal(before.Shares["Might"], before.Baseline["Might"]); // no override yet: effective == baseline

        var respecResp = await _http.PostAsJsonAsync("/api/species-build/respec", new
        {
            playerId = _playerId,
            speciesId = "fumeshroom",
            shares = new Dictionary<string, long> { ["Ferocity"] = before.Budget },
            correlationId = Guid.NewGuid().ToString("N")
        });
        if (!respecResp.IsSuccessStatusCode) throw new Exception(await respecResp.Content.ReadAsStringAsync());

        var after = await (await _http.GetAsync($"/api/aptitudes/species/{_playerId}/fumeshroom"))
            .Content.ReadFromJsonAsync<SpeciesStateDto>();
        Assert.True(after!.HasOverride);
        Assert.Equal(before.Budget, after.Shares["Ferocity"]); // effective now reflects the override
        Assert.Equal(before.Baseline["Might"], after.Baseline["Might"]); // baseline itself never moves
        Assert.NotEqual(after.Baseline["Might"], after.Shares["Might"]); // deviation is now visible
    }

    sealed class SpeciesStateDto
    {
        public string SpeciesId { get; set; } = "";
        public long Level { get; set; }
        public long Budget { get; set; }
        public long Spent { get; set; }
        public bool WithinBudget { get; set; }
        public bool HasOverride { get; set; }
        public Dictionary<string, long> Shares { get; set; } = new();
        public Dictionary<string, long> Baseline { get; set; } = new();
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
