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

namespace FusionRpg.Server.Tests;

/// <summary>species-build-todo.md T4.3 — `POST /api/species-build/respec` (spec-species-respec.md,
/// read in full this session), against a real in-process host (same pattern as
/// <c>SpeciesAllocationEndpointsTests</c>): free first override and revert, escalation, replay,
/// insufficient balance, never refused for being a respec, and the pre-existing overbudget gate.</summary>
public class SpeciesBuildEndpointsTests : IAsyncLifetime
{
    const int FumeshroomCreatureTypeId = 60007;

    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    long _playerId;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();

        PowerTuningHub.Configure(
            PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "power-scale.v2.json"))));
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
        SpeciesBuildTuningHub.Configure(new SpeciesBuildTuning(
            SchemaVersion: 1, Version: 1,
            ParityFloorPermille: 50, ParityCeilingPermille: 200,
            LeanMinPermille: 350, LeanMaxPermille: 600,
            CrowdingFactor: 633, SecondarySharePermille: 300,
            MaxAptitudesPerSpecies: 5, MinAptitudesPerSpecies: 2,
            RespecBasePrice: 50, RespecEscalationPermille: 500, RespecDecayDays: 3,
            UniqueRespecBasePrice: 50, UniqueRespecEscalationPermille: 500, UniqueRespecDecayDays: 3,
            LeanSignalWeights: LeanSignalWeights.Zero));

        SeedSpeciesLevel(_playerId, FumeshroomCreatureTypeId, level: 21, "fumeshroom"); // source = 20

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
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
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        _app.MapAptitudes();
        _app.MapSpeciesBuild();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
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

    async Task<RespecResponseDto?> Respec(string speciesId, Dictionary<string, long> shares, string correlationId)
    {
        var resp = await _http.PostAsJsonAsync("/api/species-build/respec", new
        {
            playerId = _playerId,
            speciesId,
            shares,
            correlationId
        });
        return await resp.Content.ReadFromJsonAsync<RespecResponseDto>();
    }

    async Task<System.Text.Json.JsonElement> RespecPrice(string speciesId)
    {
        var resp = await _http.GetAsync($"/api/species-build/respec-price/{_playerId}/{speciesId}");
        return await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
    }

    [Fact]
    public async Task RespecPrice_exposesEverRespecced_distinctFromTheDecayedCount()
    {
        // species-build-todo.md T5.1: a client predicting free-vs-priced before attempting a save
        // must read `everRespecced`, not `respecCount == 0` -- the count legitimately decays back to
        // zero, but `everRespecced` never resets once a species has been touched.
        var before = await RespecPrice("fumeshroom");
        Assert.False(before.GetProperty("everRespecced").GetBoolean());

        await Respec("fumeshroom", new() { ["Ferocity"] = 5 }, "free-first"); // free, but marks it touched
        var afterFree = await RespecPrice("fumeshroom");
        Assert.True(afterFree.GetProperty("everRespecced").GetBoolean());

        // Revert to baseline: still free, and the marker must NOT reset (T4.2's own fixed exploit).
        await Respec("fumeshroom", new(), "revert-1");
        var afterRevert = await RespecPrice("fumeshroom");
        Assert.True(afterRevert.GetProperty("everRespecced").GetBoolean());
    }

    [Fact]
    public async Task First_override_is_free()
    {
        _store.AwardSouls(_playerId, 1000, "seed", "bank-1");
        var body = await Respec("fumeshroom", new() { ["Ferocity"] = 10 }, "r-1");

        Assert.NotNull(body);
        Assert.False(body!.Priced);
        Assert.Equal(0, body.PriceAmount);
        Assert.Equal(10, body.Shares["Ferocity"]);
        Assert.Equal(1000, _store.GetSoulBalance(_playerId).Balance); // no charge
    }

    [Fact]
    public async Task Reverting_to_baseline_is_free_and_a_change_afterward_is_priced()
    {
        _store.AwardSouls(_playerId, 1000, "seed", "bank-2");
        await Respec("fumeshroom", new() { ["Ferocity"] = 10 }, "r-first"); // free

        var revert = await Respec("fumeshroom", new(), "r-revert");
        Assert.NotNull(revert);
        Assert.False(revert!.Priced);
        Assert.Equal(1000, _store.GetSoulBalance(_playerId).Balance);

        var change = await Respec("fumeshroom", new() { ["Ferocity"] = 20 }, "r-change");
        Assert.NotNull(change);
        Assert.True(change!.Priced);
        Assert.Equal(50, change.PriceAmount); // count was 0 going in
        Assert.Equal(950, _store.GetSoulBalance(_playerId).Balance);
    }

    [Fact]
    public async Task Escalation_prices_each_successive_change_higher()
    {
        _store.AwardSouls(_playerId, 1000, "seed", "bank-3");
        await Respec("fumeshroom", new() { ["Ferocity"] = 5 }, "free-first");

        var second = await Respec("fumeshroom", new() { ["Ferocity"] = 10 }, "chg-1");
        var third = await Respec("fumeshroom", new() { ["Ferocity"] = 15 }, "chg-2");

        Assert.Equal(50, second!.PriceAmount);
        Assert.Equal(75, third!.PriceAmount);
        Assert.True(third.PriceAmount > second.PriceAmount);
    }

    [Fact]
    public async Task A_replayed_correlation_does_not_spend_again()
    {
        _store.AwardSouls(_playerId, 1000, "seed", "bank-4");
        await Respec("fumeshroom", new() { ["Ferocity"] = 5 }, "free-first");
        await Respec("fumeshroom", new() { ["Ferocity"] = 10 }, "chg-1");
        var afterFirst = _store.GetSoulBalance(_playerId).Balance;

        var replay = await Respec("fumeshroom", new() { ["Ferocity"] = 10 }, "chg-1");
        Assert.NotNull(replay);
        Assert.True(replay!.Replay);
        Assert.Equal(afterFirst, _store.GetSoulBalance(_playerId).Balance);
    }

    [Fact]
    public async Task Insufficient_balance_refuses_with_409_and_never_because_it_is_a_respec()
    {
        await Respec("fumeshroom", new() { ["Ferocity"] = 5 }, "free-first"); // no souls awarded at all

        var resp = await _http.PostAsJsonAsync("/api/species-build/respec", new
        {
            playerId = _playerId,
            speciesId = "fumeshroom",
            shares = new Dictionary<string, long> { ["Ferocity"] = 10 },
            correlationId = "poor-1"
        });

        Assert.Equal(System.Net.HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("souls.insufficient", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Overbudget_is_refused_before_any_spend_or_state_change()
    {
        _store.AwardSouls(_playerId, 1_000_000, "seed", "bank-5");
        await Respec("fumeshroom", new() { ["Ferocity"] = 5 }, "free-first");
        var balanceBefore = _store.GetSoulBalance(_playerId).Balance;

        var resp = await _http.PostAsJsonAsync("/api/species-build/respec", new
        {
            playerId = _playerId,
            speciesId = "fumeshroom",
            shares = new Dictionary<string, long> { ["Ferocity"] = 1_000_000 },
            correlationId = "over-1"
        });

        Assert.Equal(System.Net.HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("aptitudes.overbudget", body.GetProperty("reason").GetString());
        Assert.Equal(balanceBefore, _store.GetSoulBalance(_playerId).Balance); // refused before any spend
    }

    [Fact]
    public async Task Respec_notifies_a_client_joined_as_injector_not_just_web()
    {
        // Same ⛔ this session already found and fixed once for the Commander/species allocation
        // endpoints (AptitudeEndpoints.cs) -- an injector connection only ever joins InjectorGroup, so
        // a WebGroup-only send would leave the injector's own CheatState.SpeciesAllocation stale.
        // Ported from the now-retired `/api/aptitudes/species/allocate` route's own coverage
        // (species-build-todo.md's bypass retirement) so this regression stays covered on the surface
        // that actually writes species overrides now.
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new HubConnectionBuilder()
            .WithUrl($"{_http.BaseAddress}hub/rpg").Build();
        hub.On<object>("AptitudesUpdated", _ => received.TrySetResult(true));
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.InjectorGroup);

        var resp = await _http.PostAsJsonAsync("/api/species-build/respec", new
        {
            playerId = _playerId,
            speciesId = "fumeshroom",
            shares = new Dictionary<string, long> { ["Ferocity"] = 1 },
            correlationId = "notify-injector"
        });
        resp.EnsureSuccessStatusCode();

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(got, "a species respec never reached an injector-group connection " +
            "(the exact WebGroup-only defect this module's spec calls out by name)");
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task Respec_still_notifies_a_client_joined_as_web()
    {
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new HubConnectionBuilder()
            .WithUrl($"{_http.BaseAddress}hub/rpg").Build();
        hub.On<object>("AptitudesUpdated", _ => received.TrySetResult(true));
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.WebGroup);

        var resp = await _http.PostAsJsonAsync("/api/species-build/respec", new
        {
            playerId = _playerId,
            speciesId = "fumeshroom",
            shares = new Dictionary<string, long> { ["Ferocity"] = 1 },
            correlationId = "notify-web"
        });
        resp.EnsureSuccessStatusCode();

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(got, "regression: the web-group notification broke for the respec route");
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task An_arbitrarily_high_respec_count_is_never_refused_for_being_a_respec()
    {
        _store.AwardSouls(_playerId, 1_000_000, "seed", "bank-6");
        await Respec("fumeshroom", new() { ["Ferocity"] = 1 }, "free-first");

        for (var i = 0; i < 10; i++)
        {
            var resp = await _http.PostAsJsonAsync("/api/species-build/respec", new
            {
                playerId = _playerId,
                speciesId = "fumeshroom",
                shares = new Dictionary<string, long> { ["Ferocity"] = i + 2 },
                correlationId = $"grind-{i}"
            });
            Assert.True(resp.IsSuccessStatusCode, $"attempt {i}: {await resp.Content.ReadAsStringAsync()}");
        }
    }

    sealed class RespecResponseDto
    {
        public string SpeciesId { get; set; } = "";
        public long Level { get; set; }
        public bool Priced { get; set; }
        public long PriceAmount { get; set; }
        public long RespecCount { get; set; }
        public long SoulBalance { get; set; }
        public bool Replay { get; set; }
        public Dictionary<string, long> Shares { get; set; } = new();
    }

    // ---- respec-free-counter EP4.10: the routes carry the payment choice --------------------------

    /// <summary>
    /// Seeds the free-respec STOCK exactly as a real empire level leaves it - rows in
    /// `rpg_empire_free_respec_ledger` keyed by the paying level - written on the store's own connection.
    ///
    /// <para><b>Why not through the fact path.</b> `FusionRpg.Server.Tests` has no internals access, so it
    /// cannot call the internal grant applier; and the public alternative was DIAGNOSED here and does not
    /// work in this host: sixteen placements took a species to L21 while no `kind='empire'` row was ever
    /// written, although every input the credit's R1 gate reads was asserted correct (`def.Side='plant'`,
    /// human empire `dave`, `KillAttribution.EmpireOf("plant")='dave'`). That is a real finding about this
    /// host, filed in the ledger. A fixture may fabricate STATE but never a result, so this writes the same
    /// rows the applier would and the route under test still reads them through the public
    /// `FreeRespecStock`.</para>
    /// </summary>
    void SeedFreeRespecStock(int grants = 2)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_empire_free_respec_ledger(save_id, empire_id, level, delta, reason, granted_utc)
            VALUES ($s, $e, $l, 1, 'empire-level', $t);
            """;
        cmd.Parameters.AddWithValue("$s", _playerId);
        cmd.Parameters.AddWithValue("$e", _store.HumanEmpireOf(_playerId).Value);
        cmd.Parameters.AddWithValue("$l", 2L);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        for (var level = 2; level < 2 + grants; level++)
        {
            cmd.Parameters["$l"].Value = (long)level;
            cmd.ExecuteNonQuery();
        }
        Assert.True(_store.FreeRespecStock(HumanEmpire()) >= grants, "the seeded stock must be positive");
    }

    FusionRpg.Core.Saves.EmpireRef HumanEmpire() => new(
        new FusionRpg.Core.Saves.SaveId(_playerId), _store.HumanEmpireOf(_playerId));

    static object RespecBody(long playerId, string? payWith) => new
    {
        playerId,
        speciesId = "fumeshroom",
        correlationId = "ep410-" + Guid.NewGuid().ToString("N"),
        payWith,
        shares = new Dictionary<string, long>(StringComparer.Ordinal) { ["Might"] = 1 },
    };

    /// <summary>A species' FIRST override is free (the pre-existing rule), so every case about the PRICED
    /// path spends that one first and then works on the change after it.</summary>
    async Task TouchSpeciesAsync()
    {
        var resp = await _http.PostAsJsonAsync("/api/species-build/respec", RespecBody(_playerId, payWith: null));
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("", body.GetProperty("paidWith").GetString());   // the first override really was free
    }

    [Fact]
    public async Task With_stock_and_no_payWith_the_respec_returns_409_with_the_two_numbers()
    {
        SeedFreeRespecStock();
        _store.AwardSouls(_playerId, 1000, "seed", "bank-ep410a");
        await TouchSpeciesAsync();
        var stockBefore = _store.FreeRespecStock(HumanEmpire());

        var resp = await _http.PostAsJsonAsync("/api/species-build/respec", RespecBody(_playerId, payWith: null));

        Assert.Equal(System.Net.HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("respec.payment.choice-required", body.GetProperty("reason").GetString());
        Assert.True(body.GetProperty("soulPrice").GetInt64() > 0);                    // the price it would pay
        Assert.Equal(stockBefore, body.GetProperty("freeRespecStock").GetInt64());    // the stock it could spend
        Assert.Equal(stockBefore, _store.FreeRespecStock(HumanEmpire()));              // and nothing was written
    }

    [Fact]
    public async Task A_freeRespec_payWith_spends_the_stock_and_reports_what_was_paid()
    {
        SeedFreeRespecStock();
        await TouchSpeciesAsync();
        var stockBefore = _store.FreeRespecStock(HumanEmpire());

        var resp = await _http.PostAsJsonAsync("/api/species-build/respec", RespecBody(_playerId, "freeRespec"));

        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("freeRespec", body.GetProperty("paidWith").GetString());
        Assert.Equal(stockBefore - 1, body.GetProperty("freeRespecStock").GetInt64());
        Assert.Equal(stockBefore - 1, _store.FreeRespecStock(HumanEmpire()));
        Assert.False(body.GetProperty("priced").GetBoolean());
    }

    [Fact]
    public async Task The_price_route_reports_the_stock_and_whether_it_is_available()
    {
        SeedFreeRespecStock();
        var stock = _store.FreeRespecStock(HumanEmpire());

        var resp = await _http.GetAsync($"/api/species-build/respec-price/{_playerId}/fumeshroom");
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.Equal(stock, body.GetProperty("freeRespecStock").GetInt64());
        Assert.Equal(stock > 0, body.GetProperty("freeAvailable").GetBoolean());
        Assert.True(body.GetProperty("priceAmount").GetInt64() > 0);   // the soul price is still quoted
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
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "collect-class-system-realrun.ps1"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }
}
