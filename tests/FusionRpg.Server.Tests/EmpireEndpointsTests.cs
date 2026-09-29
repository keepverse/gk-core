using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Activity;
using FusionRpg.Core.Progression;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using FusionRpg.Data.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using FusionRpg.Core.Effects;
using FusionRpg.Data.Abstractions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// `empire-level` EP4.7 (spec-empire-level.md §"Contracts"): the one empire-level READ, against a REAL,
/// minimal in-process host (`AptitudeEndpointsTests`' own pattern), plus the generic progression route
/// accepting `empire` — which needs no change, because EP4.1 put the kind in the closed vocabulary and
/// that route passes it straight through. The two numbers the route joins (the level row and the
/// free-respec stock) live in different tables, which is the whole reason a dedicated read exists.
/// </summary>
public class EmpireEndpointsTests : IAsyncLifetime
{
    const int FumeshroomGameTypeId = 7;

    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    long _playerId;
    string _empireId = "";
    string _baseUrl = "";

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not locate repo root above " + AppContext.BaseDirectory);
    }

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        _empireId = _store.HumanEmpireOf(_playerId).Value;

        // The route reads two hubs: the progression curve (the empire's own pair) and the empire grant
        // amount. The fact route below also needs the species curve. Configured here rather than ridden on
        // another class, the same discipline this suite's other endpoint tests follow.
        // The shipped document plus a raised `awards.speciesLevelUp`: 25 has no other reader in the tree
        // (EP4.1 added it, the empire credit consumes it), and with the small species curve below it lets
        // ONE placement cross several species levels and therefore credit the empire several times --
        // a real level and a real stock to read back, rather than a fixture that never moves.
        var shipped = ProgressionTuningLoader.Parse(
            File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "progression.v3.json")));
        ProgressionTuningHub.Configure(shipped with
        {
            Awards = shipped.Awards with { SpeciesLevelUp = 25 },
        });
        SpeciesProgressionTuningHub.Configure(new SpeciesProgressionTuning(
            CurveFirst: 1, CurveStep: 1, RunCompletionAward: 1, PlacementAward: 25));
        EmpireLevelTuningHub.Configure(new EmpireLevelTuning(FreeRespecsPerEmpireLevel: 1));
        // The classless fact path resolves a creature species from the roster (`RpgXpAwardMap` and
        // `IsEmpireGeneralSource` both need it), so the roster is configured here exactly as this
        // suite's other endpoint fixtures do rather than depending on another class having done it.
        FusionRpg.Core.Creatures.CreatureSpeciesCatalog.ConfigureFromCompiledDefault();

        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var baseUrl = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<EffectGrantSession>();
        builder.Services.AddSingleton<IHotCompactor>(sp => new HotCompactor(sp.GetRequiredService<RpgStore>()));
        builder.Services.AddSingleton<CompactionWorker>();
        builder.Services.AddSingleton<UniqueActorService>();
        builder.Services.AddSingleton<EventIngest>();
        builder.Services.AddSingleton<DelveBattleSessionManager>();
        builder.Services.AddSingleton<PlayerConnectionRegistry>();
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.MapHub<RpgHub>("/hub/rpg");   // the broadcast below is asserted on the real wire
        _app.MapEmpireRoutes();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _baseUrl = baseUrl;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    long StartRun()
    {
        _store.SetCurrentPlayer(_playerId);
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "board.start",
            MatchKey = "m-" + Guid.NewGuid().ToString("N"),
            Payload = new { levelName = "test", levelType = "adventure" },
        });
        return _store.ListRuns(_playerId).OrderByDescending(r => r.Id).First().Id;
    }

    /// <summary>One placement that crosses several species levels, which is what credits the empire —
    /// EP4.3's own shape. Seeded through the STORE, not through HTTP: the fact-ingest route belongs to
    /// `Program.cs` and this minimal host maps only the route under test, so going through HTTP here
    /// would be testing a fixture. The seeding call is the same `AppendPvzActivityFact` the ingest route
    /// itself calls.</summary>
    IReadOnlyList<RpgProgressionDirty> CreditAnEmpireLevel()
    {
        var runId = StartRun();
        return _store.AppendPvzActivityFact(_playerId, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.PlantPlaced, RunId = runId,
            SourceKind = "creature.progression.v1", SourceId = "general:fumeshroom",
            PayloadJson = "{\"type\":" + FumeshroomGameTypeId + "}", DedupeKey = "place-0",
        }).Progression;
    }

    [Fact]
    public async Task A_fresh_empire_reads_level_one_with_an_empty_stock()
    {
        var body = await GetLevelAsync(_playerId, _empireId);

        Assert.Equal(_empireId, body.GetProperty("empireId").GetString());
        Assert.Equal(1, body.GetProperty("level").GetInt64());
        Assert.Equal(0, body.GetProperty("xp").GetInt64());
        Assert.Equal(1, body.GetProperty("highestLevel").GetInt64());
        Assert.Equal(0, body.GetProperty("freeRespecStock").GetInt64());
        Assert.Equal(1, body.GetProperty("freeRespecsPerLevel").GetInt64());
        // The curve is the loaded one, not a literal: an empire at level 1 needs the first step.
        Assert.Equal(RpgXpCurve.XpToNext(RpgActorKinds.Empire, 1), body.GetProperty("xpToNext").GetInt64());
    }

    [Fact]
    public async Task An_unknown_player_reads_404()
    {
        var resp = await _http.GetAsync($"/api/players/999999/empires/{_empireId}/level");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task A_credited_empire_reads_its_level_and_the_stock_it_paid_for()
    {
        CreditAnEmpireLevel();
        var body = await GetLevelAsync(_playerId, _empireId);

        var level = body.GetProperty("level").GetInt64();
        Assert.True(level > 1, $"the credit should have levelled the empire, read {level}");
        Assert.True(body.GetProperty("freeRespecStock").GetInt64() > 0,
            "a level that pays a grant must show the stock it paid into");
        Assert.Equal(_store.GetRpgActor(_playerId, RpgActorKinds.Empire, 0)!.Xp, body.GetProperty("xp").GetInt64());
        Assert.Equal(RpgXpCurve.XpToNext(RpgActorKinds.Empire, level), body.GetProperty("xpToNext").GetInt64());
    }

    /// <summary>
    /// The generic progression route needs NO change — EP4.1 put `empire` in the closed vocabulary and
    /// the route passes its kind straight through to this same read — but the route itself is mapped in
    /// `Program.cs`, and this minimal host maps only the route under test (the fact-ingest route is
    /// `Program.cs`'s for the same reason). So this asserts the READ that route performs, by the kind
    /// string a caller would pass, and the fragment records that no test in this row builds the full
    /// host to hit the generic route directly.
    /// </summary>
    [Fact]
    public void The_generic_progression_read_accepts_the_empire_kind()
    {
        CreditAnEmpireLevel();

        var row = _store.GetRpgActor(_playerId, RpgActorKinds.Empire, 0);
        Assert.NotNull(row);
        Assert.True(row!.Level > 1);
        Assert.True(RpgActorKinds.IsKnown(RpgActorKinds.Empire));
    }

    async Task<JsonElement> GetLevelAsync(long playerId, string empireId)
    {
        var resp = await _http.GetAsync($"/api/players/{playerId}/empires/{empireId}/level");
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// EP4.7's own second clause, on the REAL wire: one `EmpireLevelUp` per empire level crossed, after
    /// commit, carrying the grants and the stock those grants grew. The production caller is `Program.cs`'s
    /// fact-ingest loop; this drives the same `EmpireLevelBroadcast` seam it calls, with the host's own
    /// `IHubContext` and a real SignalR client joined to the web group, so the payload is asserted rather
    /// than described.
    /// </summary>
    [Fact]
    public async Task A_level_crossing_broadcasts_one_EmpireLevelUp_per_level_with_its_grants()
    {
        var received = new List<JsonElement>();
        await using var client = new HubConnectionBuilder().WithUrl($"{_baseUrl}/hub/rpg").Build();
        client.On<JsonElement>("EmpireLevelUp", p => { lock (received) received.Add(p); });
        await client.StartAsync();
        await client.InvokeAsync("Join", RpgConstants.WebGroup);

        var dirties = CreditAnEmpireLevel();
        var levelUps = dirties.Where(d => d.Kind == RpgActorKinds.Empire)
            .SelectMany(d => d.LevelUps ?? Array.Empty<RpgStore.EmpireLevelUpEvent>()).ToList();
        Assert.True(levelUps.Count >= 1, "the credit should have queued at least one crossing");

        await EmpireLevelBroadcast.SendEmpireLevelUpsAsync(
            _app.Services.GetRequiredService<IHubContext<RpgHub>>(), _store, dirties);

        // Wait for exactly one message per queued crossing, then a beat to prove no extra arrived.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && received.Count < levelUps.Count) await Task.Delay(25);
        await Task.Delay(150);

        Assert.Equal(levelUps.Count, received.Count);
        Assert.Equal(levelUps.Select(l => (l.LevelBefore, l.LevelAfter)).ToArray(),
            received.Select(p => (p.GetProperty("levelBefore").GetInt64(), p.GetProperty("levelAfter").GetInt64())).ToArray());
        Assert.All(received, p => Assert.Equal(_playerId, p.GetProperty("playerId").GetInt64()));
        Assert.All(received, p => Assert.Equal(_empireId, p.GetProperty("empireId").GetString()));
        Assert.All(received, p => Assert.True(p.GetProperty("freeRespecStock").GetInt64() > 0));
        // Every message carries the closed-vocabulary grant list this level paid.
        Assert.All(received, p => Assert.Contains(p.GetProperty("grants").EnumerateArray(),
            g => g.GetProperty("kind").GetString() == "FreeEmpireRespec" && g.GetProperty("amount").GetInt64() == 1));
    }
}
