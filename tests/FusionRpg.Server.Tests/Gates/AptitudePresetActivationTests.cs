using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Power;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using FusionRpg.Server.Gates;
using FusionRpg.Server.Power;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests.Gates;

/// <summary>
/// build-preset BP1.4/BP1.5 (spec-gate-services.md) — proves
/// <see cref="AptitudePresetActivation"/>, lifted out of <c>AptitudePresetEndpoints.cs</c>'s
/// <c>/activate</c> lambda, is byte-identical to what the route did (same allocation, preset-active
/// row, soul ledger and free-respec ledger, same reason), and that its read half
/// <see cref="AptitudePresetActivation.Preview"/> reports exactly what the write charges without
/// writing anything. <c>AptitudePresetEndpointsTests</c> proves the route itself needed no edit;
/// this suite proves the service a build-preset applier will call is that same gate.
/// </summary>
public class AptitudePresetActivationTests : IDisposable
{
    const int FumeshroomCreatureTypeId = 60007;
    const string Species = "fumeshroom";
    /// <summary>Bigger than any budget the preset's 1000‰ proposes, so an activation over it is a
    /// take-back (a priced respec) rather than an add. Only a fixture's starting state.</summary>
    const long TakeBackSeed = 1_000_000;

    readonly List<DataTestStore> _stores = new();

    public AptitudePresetActivationTests()
    {
        var tuningDir = Path.Combine(RepoRoot(), "data", "tuning");
        string Read(string name) => File.ReadAllText(Path.Combine(tuningDir, name));
        PowerTuningHub.Configure(PowerTuningLoader.Parse(Read("power-scale.v2.json")));
        AptitudeTuningHub.Configure(AptitudeTuningLoader.Parse(Read(LatestAptitudesName())));
        AptitudePresetTuningHub.Configure(
            AptitudePresetTuningLoader.Parse(Read("aptitude-presets.v2.json")));
        ProgressionTuningHub.Configure(ProgressionTuningLoader.Parse(Read("progression.v3.json")));
        SpeciesProgressionTuningHub.Configure(
            SpeciesProgressionTuningLoader.Parse(Read("species-progression.v1.json")));
        SoulEarnPolicy.Configure(SoulEarnTuningLoader.Parse(Read("souls.v1.json")));
        CreatureSpeciesCatalog.ConfigureFromCompiledDefault();
        SpeciesBuildPlanCatalog.Configure(new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal)
        {
            [Species] = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["Might"] = 500, ["Vigor"] = 300, ["Fortitude"] = 200
            }
        });
        SpeciesBuildTuningHub.Configure(new SpeciesBuildTuning(
            SchemaVersion: 1, Version: 6,
            ParityFloorPermille: 50, ParityCeilingPermille: 200,
            LeanMinPermille: 350, LeanMaxPermille: 600,
            CrowdingFactor: 150, SecondarySharePermille: 300,
            MaxAptitudesPerSpecies: 5, MinAptitudesPerSpecies: 2,
            RespecBasePrice: 50, RespecEscalationPermille: 500, RespecDecayDays: 3,
            UniqueRespecBasePrice: 50, UniqueRespecEscalationPermille: 500, UniqueRespecDecayDays: 3,
            LeanSignalWeights: LeanSignalWeights.Zero));
    }

    public void Dispose()
    {
        foreach (var s in _stores) s.Dispose();
    }

    // ---- fixtures -------------------------------------------------------------------------------

    RpgStore NewStore()
    {
        var ts = DataTestStore.Create();
        _stores.Add(ts);
        return ts.Store;
    }

    static AptitudePresetActivation NewService(RpgStore store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        var hub = services.BuildServiceProvider().GetRequiredService<IHubContext<RpgHub>>();
        return new AptitudePresetActivation(
            store, new ServerPowerIndexProvider(store, PowerTuningHub.Tuning), hub);
    }

    /// <summary>A preset the store really holds, so the gate is exercised over a real row and never a
    /// fabricated result. Same id on both stores keeps the parity dump comparable.</summary>
    static void SeedPreset(RpgStore store, long playerId, string presetId)
    {
        var row = new RpgAptitudePresetRow(presetId, playerId, "BP preset " + presetId, "player",
            DateTimeOffset.UtcNow.ToString("o"), Revision: 0);
        var entries = AptitudeCatalog.All
            .Select(a => new RpgAptitudePresetEntryRow(
                presetId, a.Id, a.Id == "Might" ? 1000L : 0L, null, null, null, null))
            .ToList();
        Assert.Equal("", store.SaveAptitudePreset(row, entries, isCreate: true));
    }

    static void SeedSpeciesLevel(RpgStore store, long playerId, long level)
    {
        using var db = SqliteConnectionFactory.Open(store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_actor_progression(
              save_id, empire_id, kind, type_id, level, xp, highest_level, demotion_count, revision, updated_utc, scope_key)
            VALUES ($p, $e, 'species', $tid, $lvl, 0, $lvl, 0, 0, $now, $sk);
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$e", store.HumanEmpireOf(playerId).Value);
        cmd.Parameters.AddWithValue("$tid", FumeshroomCreatureTypeId);
        cmd.Parameters.AddWithValue("$lvl", level);
        cmd.Parameters.AddWithValue("$sk", Species);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>The species' own "ever touched by this economy" marker, count 0 — so an override is a
    /// PRICED respec (not a first override) at a deterministic price (<c>PriceOf(0)</c>). Fabricated
    /// state; the store still reads it through its public quote.</summary>
    static void SeedEverTouchedSpecies(RpgStore store, long playerId)
    {
        using var db = SqliteConnectionFactory.Open(store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_species_respec(player_id, species_id, count, last_respec_utc)
            VALUES ($p, $s, 0, $t);
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$s", Species);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    static void SeedFreeRespecStock(RpgStore store, long playerId, int grants)
    {
        using var db = SqliteConnectionFactory.Open(store.HotPath);
        for (var level = 2; level < 2 + grants; level++)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO rpg_empire_free_respec_ledger(save_id, empire_id, level, delta, reason, granted_utc)
                VALUES ($s, $e, $l, 1, 'empire-level', $t);
                """;
            cmd.Parameters.AddWithValue("$s", playerId);
            cmd.Parameters.AddWithValue("$e", store.HumanEmpireOf(playerId).Value);
            cmd.Parameters.AddWithValue("$l", (long)level);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    static void SeedSouls(RpgStore store, long playerId, long amount) =>
        store.AwardSouls(playerId, amount, "test.grant", "bp14-seed");

    static void SeedTakeBack(RpgStore store, AllocationScope scope, string scopeKey) =>
        store.SaveAllocation(scope, scopeKey, AptitudeAllocation.Empty + AptitudeAllocation.Single(scope, "Might", TakeBackSeed));

    static string CommanderKey(long playerId) => $"player:{playerId}";
    static string SpeciesKey(long playerId) =>
        SpeciesAllocation.ScopeKey(playerId, FusionRpg.Core.Commanders.EmpireId.Dave, Species);

    /// <summary>Every row the acceptance names, canonicalised. Identical strings from two stores mean
    /// the same allocation, the same active row, the same soul ledger and the same free-respec ledger.</summary>
    static string Dump(RpgStore store)
    {
        using var db = SqliteConnectionFactory.Open(store.HotPath);
        var sb = new StringBuilder();
        DumpTable(db, sb, "SELECT scope, scope_key, aptitude_id, points FROM rpg_aptitude_allocation ORDER BY scope, scope_key, aptitude_id");
        DumpTable(db, sb, "SELECT player_id, scope, scope_key, preset_id FROM rpg_aptitude_preset_active ORDER BY player_id, scope, scope_key");
        DumpTable(db, sb, "SELECT player_id, delta, reason, dedupe_key FROM rpg_soul_ledger ORDER BY id");
        DumpTable(db, sb, "SELECT save_id, empire_id, level, delta, reason FROM rpg_empire_free_respec_ledger ORDER BY save_id, empire_id, level");
        DumpTable(db, sb, "SELECT player_id, species_id, count FROM rpg_species_respec ORDER BY player_id, species_id");
        return sb.ToString();
    }

    static void DumpTable(SqliteConnection db, StringBuilder sb, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            for (var i = 0; i < r.FieldCount; i++) sb.Append(r.GetValue(i)).Append('|');
            sb.Append('\n');
        }
        sb.Append("--\n");
    }

    static async Task<(WebApplication App, HttpClient Http)> HostRoute(RpgStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IPowerIndexProvider>(sp =>
            new ServerPowerIndexProvider(sp.GetRequiredService<RpgStore>(), PowerTuningHub.Tuning));
        builder.Services.AddSingleton<AptitudePresetActivation>();
        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        builder.WebHost.UseUrls(baseUrl);
        var app = builder.Build();
        app.MapAptitudes();
        app.MapAptitudePresets();
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

    static string LatestAptitudesName()
    {
        var dir = Path.Combine(RepoRoot(), "data", "tuning");
        return Directory.EnumerateFiles(dir, "aptitudes.v*.json")
            .Select(Path.GetFileName)
            .Select(n => (Name: n!, Match: System.Text.RegularExpressions.Regex.Match(n!, @"^aptitudes\.v(\d+)\.json$")))
            .Where(x => x.Match.Success)
            .OrderByDescending(x => int.Parse(x.Match.Groups[1].Value))
            .First().Name;
    }

    // ---- BP1.4: service-versus-route parity -----------------------------------------------------

    /// <summary>Six identical-state pairs. Each drives the service directly on one store and the
    /// <c>/activate</c> route over HTTP on the other, then compares every row the acceptance names and
    /// the reason. The seed is applied to BOTH stores before the call, so the comparison is
    /// state-for-state, not just outcome-for-outcome.</summary>
    [Theory]
    [InlineData("species-first-override", "species", null, false, false, false)]
    [InlineData("species-paid-souls", "species", "souls", true, false, false)]
    [InlineData("species-paid-free", "species", "freeRespec", true, true, false)]
    [InlineData("species-choice-required", "species", null, true, true, false)]
    [InlineData("species-insufficient", "species", "souls", true, false, true)]
    [InlineData("commander-adds", "commander", null, false, false, false)]
    public async Task Service_and_route_write_the_same_rows_and_return_the_same_reason(
        string label, string scope, string? payWith, bool touched, bool stock, bool drain)
    {
        const string presetId = "bp14-preset";
        RespecPayment? payment = payWith is null
            ? null
            : (payWith == "souls" ? RespecPayment.Souls : RespecPayment.FreeRespec);

        RpgStore Seed()
        {
            var store = NewStore();
            var pid = store.GetCurrentPlayerId();
            SeedPreset(store, pid, presetId);
            if (scope == "species")
            {
                SeedSpeciesLevel(store, pid, 21);
                if (touched) SeedEverTouchedSpecies(store, pid);
            }
            if (stock) SeedFreeRespecStock(store, pid, 2);
            if (!drain) SeedSouls(store, pid, 200);
            return store;
        }

        var serviceStore = Seed();
        var routeStore = Seed();
        var pid = serviceStore.GetCurrentPlayerId();
        var before = Dump(serviceStore);
        Assert.Equal(before, Dump(routeStore));

        var scopeKey = scope == "species" ? Species : "";
        var direct = NewService(serviceStore).Activate(pid, presetId, scope, scopeKey, "corr-" + label, payment);

        var (app, http) = await HostRoute(routeStore);
        try
        {
            var resp = await http.PostAsJsonAsync("/api/aptitude-presets/activate", new
            {
                playerId = pid,
                presetId,
                scope,
                scopeKey,
                correlationId = "corr-" + label,
                payWith
            });
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            var bodyReason = body.TryGetProperty("reason", out var rr) ? rr.GetString() : "";

            Assert.Equal(direct.Ok, resp.IsSuccessStatusCode);
            Assert.Equal(direct.Reason, bodyReason);
            if (direct.Ok)
            {
                Assert.Equal(direct.Priced, body.GetProperty("priced").GetBoolean());
                Assert.Equal(direct.PriceAmount, body.GetProperty("priceAmount").GetInt64());
                Assert.Equal(direct.RespecCount, body.GetProperty("respecCount").GetInt64());
            }
            else if (direct.Reason == "respec.payment.choice-required")
            {
                Assert.Equal(direct.PriceAmount, body.GetProperty("soulPrice").GetInt64());
                Assert.Equal(direct.FreeStock, body.GetProperty("freeRespecStock").GetInt64());
            }
            else if (direct.Reason == "souls.insufficient")
            {
                Assert.Equal(direct.PriceAmount, body.GetProperty("priceAmount").GetInt64());
            }
        }
        finally
        {
            http.Dispose();
            await app.StopAsync();
        }

        Assert.Equal(Dump(serviceStore), Dump(routeStore));
    }

    /// <summary>The budget and store refusals reach the route with the same reason through the same
    /// stage — no new rule, and nothing written on either side.</summary>
    [Fact]
    public async Task Refusal_reasons_match_the_route_for_the_budget_and_store_stages()
    {
        const string presetId = "bp14-refusal-preset";
        RpgStore Seed()
        {
            var store = NewStore();
            SeedPreset(store, store.GetCurrentPlayerId(), presetId);
            return store;
        }

        var serviceStore = Seed();
        var routeStore = Seed();
        var pid = serviceStore.GetCurrentPlayerId();

        var unknownScope = NewService(serviceStore).Activate(pid, presetId, "nonsense", "", "corr-x", null);
        Assert.False(unknownScope.Ok);
        Assert.Equal(ActivationStage.Budget, unknownScope.Stage);
        Assert.Equal("presets.scope.unknown", unknownScope.Reason);

        var missing = NewService(serviceStore).Activate(pid, "no-such-preset", "commander", "", "corr-y", null);
        Assert.False(missing.Ok);
        Assert.Equal(ActivationStage.Store, missing.Stage);
        Assert.Equal("presets.notFound", missing.Reason);

        var (app, http) = await HostRoute(routeStore);
        try
        {
            var scopeResp = await http.PostAsJsonAsync("/api/aptitude-presets/activate",
                new { playerId = pid, presetId, scope = "nonsense", scopeKey = "", correlationId = "corr-x" });
            Assert.Equal(HttpStatusCode.BadRequest, scopeResp.StatusCode);
            Assert.Contains("presets.scope.unknown", await scopeResp.Content.ReadAsStringAsync());

            var presetResp = await http.PostAsJsonAsync("/api/aptitude-presets/activate",
                new { playerId = pid, presetId = "no-such-preset", scope = "commander", scopeKey = "", correlationId = "corr-y" });
            Assert.Equal(HttpStatusCode.NotFound, presetResp.StatusCode);
        }
        finally
        {
            http.Dispose();
            await app.StopAsync();
        }

        Assert.Equal(Dump(serviceStore), Dump(routeStore));
    }

    // ---- BP1.5: preview equals activate, and preview writes nothing -----------------------------

    /// <summary>
    /// The seven cases the acceptance names, plus a species first override. For each:
    /// <see cref="AptitudePresetActivation.Preview"/> reports the shares, leftover and the scope's own
    /// quote (soul price, free stock, respec flag), and <see cref="AptitudePresetActivation.Activate"/>
    /// on an identical store writes those same shares and charges that same quote — or, where the store
    /// refuses, returns the same reason carrying the same two numbers.
    /// <paramref name="expectRefusal"/> names the store's own refusal code for the one case that has it.
    /// </summary>
    [Theory]
    // a species target paid with a free respec: the quote's soul price is the ALTERNATIVE, and the free
    // option costs exactly one stock unit.
    [InlineData("species", "freeRespec", "touched-stock", null, true, false)]
    // a species target paid in souls.
    [InlineData("species", "souls", "touched", null, true, false)]
    // a species target with no choice while stock exists: refused by name, nothing written.
    [InlineData("species", null, "touched-stock", "respec.payment.choice-required", true, false)]
    // a species first override: free.
    [InlineData("species", null, "", null, false, true)]
    // a commander target adding points: free.
    [InlineData("commander", null, "", null, false, true)]
    // a commander target taking points back: priced in souls.
    [InlineData("commander", null, "takeback", null, true, false)]
    // a unique target adding points: free.
    [InlineData("unique", null, "", null, false, true)]
    // a unique target taking points back: priced in souls.
    [InlineData("unique", null, "takeback", null, true, false)]
    public void Preview_equals_what_activate_writes_and_charges(
        string scope, string? payWith, string seed, string? expectRefusal, bool expectRespec, bool expectFree)
    {
        const string presetId = "bp15-preset";
        RespecPayment? payment = payWith is null
            ? null
            : (payWith == "souls" ? RespecPayment.Souls : RespecPayment.FreeRespec);

        (RpgStore Store, string UniqueId) Seed()
        {
            var store = NewStore();
            var pid = store.GetCurrentPlayerId();
            SeedPreset(store, pid, presetId);
            SeedSouls(store, pid, 5000);
            var uniqueId = "";
            if (scope == "species")
            {
                SeedSpeciesLevel(store, pid, 21);
                if (seed.Contains("touched")) SeedEverTouchedSpecies(store, pid);
            }
            if (seed.Contains("stock")) SeedFreeRespecStock(store, pid, 2);
            if (scope == "unique")
            {
                uniqueId = store.EnsureUniqueActorForAudit(pid, "ua-bp15", "plant", typeId: 1, level: 10).InstanceId;
                if (seed == "takeback") SeedTakeBack(store, AllocationScope.UniqueCreature, uniqueId);
            }
            if (scope == "commander" && seed == "takeback")
                SeedTakeBack(store, AllocationScope.Commander, CommanderKey(pid));
            return (store, uniqueId);
        }

        var (previewStore, previewUniqueId) = Seed();
        var (applyStore, applyUniqueId) = Seed();
        Assert.Equal(previewUniqueId, applyUniqueId);
        var pid = previewStore.GetCurrentPlayerId();
        var scopeKey = scope == "species" ? Species : scope == "unique" ? applyUniqueId : "";

        var before = Dump(previewStore);
        var preview = NewService(previewStore).Preview(pid, presetId, scope, scopeKey);

        // Test 5 (BP1.5): the read half writes nothing at all.
        Assert.Equal(before, Dump(previewStore));
        Assert.True(preview.Ok, preview.Reason);

        var applied = NewService(applyStore).Activate(pid, presetId, scope, scopeKey, "corr-bp15", payment);

        if (expectRefusal is not null)
        {
            Assert.False(applied.Ok);
            Assert.Equal(expectRefusal, applied.Reason);
            Assert.Equal(preview.SoulPrice, applied.PriceAmount);
            Assert.Equal(preview.FreeStock, applied.FreeStock);
            Assert.Equal(before, Dump(applyStore));
            return;
        }

        Assert.True(applied.Ok, applied.Reason);

        // The shares the preview reported are the shares the write stored.
        var allocScope = scope switch
        {
            "species" => AllocationScope.CreatureType,
            "commander" => AllocationScope.Commander,
            _ => AllocationScope.UniqueCreature
        };
        var allocKey = scope switch
        {
            "species" => SpeciesKey(pid),
            "commander" => CommanderKey(pid),
            _ => scopeKey
        };
        var stored = applyStore.LoadAllocation(allocScope, allocKey);
        foreach (var kv in preview.Shares)
            Assert.Equal(kv.Value, stored.PointsAt(allocScope, kv.Key));

        Assert.Equal(expectRespec, preview.IsRespec);
        if (expectFree)
        {
            Assert.False(applied.Priced);
            Assert.Equal(0, applied.PriceAmount);
            Assert.Equal(0, preview.SoulPrice);
        }
        else if (scope == "species" && payment == RespecPayment.FreeRespec)
        {
            // The free option IS the payment: the soul price the preview quoted is the alternative,
            // and exactly one stock unit was spent.
            Assert.False(applied.Priced);
            Assert.Equal(0, applied.PriceAmount);
            Assert.Equal(preview.FreeStock - 1, applied.FreeStock);
        }
        else
        {
            // Paid in souls (or a commander/unique take-back): the preview's price is the charge.
            Assert.True(applied.Priced);
            Assert.Equal(preview.SoulPrice, applied.PriceAmount);
            if (scope != "species") Assert.Equal(0, preview.FreeStock);
        }
    }
}
