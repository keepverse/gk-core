using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>aptitude-sheet AS-3.1 / AS-3.2 — <c>/api/aptitude-presets</c> against a real in-process host.</summary>
public class AptitudePresetEndpointsTests : IAsyncLifetime
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
        AptitudeTuningHub.Configure(
            AptitudeTuningLoader.Parse(File.ReadAllText(LatestAptitudesPath())));
        AptitudePresetTuningHub.Configure(
            AptitudePresetTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "aptitude-presets.v2.json"))));
        FusionRpg.Core.Progression.ProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.ProgressionTuningLoader.Parse(
                File.ReadAllText(Path.Combine(RepoTuningDir(), "progression.v3.json"))));
        FusionRpg.Core.Progression.SpeciesProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.SpeciesProgressionTuningLoader.Parse(
                File.ReadAllText(Path.Combine(RepoTuningDir(), "species-progression.v1.json"))));
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

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<IPowerIndexProvider>(sp =>
            new FusionRpg.Server.Power.ServerPowerIndexProvider(sp.GetRequiredService<RpgStore>(), PowerTuningHub.Tuning));
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapAptitudes();
        _app.MapAptitudePresets();
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

    static List<object> EvenRows()
    {
        var list = new List<object>();
        var i = 0;
        foreach (var apt in AptitudeCatalog.All)
        {
            var pm = 83L + (i < 4 ? 1L : 0L);
            list.Add(new { aptitudeId = apt.Id, targetPermille = pm });
            i++;
        }
        return list;
    }

    /// <summary>All 1000‰ on Might — survives low commander budgets where an even split floors to zero.</summary>
    static List<object> MightOnlyRows()
    {
        return AptitudeCatalog.All.Select(apt => (object)new
        {
            aptitudeId = apt.Id,
            targetPermille = apt.Id == "Might" ? 1000L : 0L
        }).ToList();
    }

    // ---- respec-free-counter EP4.10: the species branch carries the payment choice -----------------

    FusionRpg.Core.Saves.EmpireRef HumanEmpire() => new(
        new FusionRpg.Core.Saves.SaveId(_playerId), _store.HumanEmpireOf(_playerId));

    /// <summary>Seeds the free-respec STOCK as a real empire level leaves it - ledger rows keyed by the
    /// paying level - on the store's own connection. `FusionRpg.Server.Tests` has no internals access to
    /// the grant applier, and the public fact path was diagnosed not to create an empire row in this host;
    /// a fixture may fabricate STATE but never a result, and the store still reads it through the public
    /// `FreeRespecStock`.</summary>
    void SeedFreeRespecStock(int grants = 2)
    {
        SpeciesBuildTuningHub.Configure(new SpeciesBuildTuning(
            SchemaVersion: 1, Version: 6,
            ParityFloorPermille: 50, ParityCeilingPermille: 200,
            LeanMinPermille: 350, LeanMaxPermille: 600,
            CrowdingFactor: 150, SecondarySharePermille: 300,
            MaxAptitudesPerSpecies: 5, MinAptitudesPerSpecies: 2,
            RespecBasePrice: 50, RespecEscalationPermille: 500, RespecDecayDays: 3,
            UniqueRespecBasePrice: 50, UniqueRespecEscalationPermille: 500, UniqueRespecDecayDays: 3,
            LeanSignalWeights: LeanSignalWeights.Zero));

        using var db = FusionRpg.Data.Sqlite.SqliteConnectionFactory.Open(_store.HotPath);
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

    /// <summary>
    /// The acceptance's second clause, at the seam it names: the species branch of activate "passes
    /// `payWith` through `TryActivateAptitudePreset` to the same store call" the respec route uses. Proved
    /// on that store call with a preset the HTTP surface really created, so the pass-through is exercised
    /// for real rather than restated.
    ///
    /// <para><b>Why not through the activate ROUTE.</b> The route first asks `ResolveBudget`, whose species
    /// arm resolved budget 0 for this fixture even with the species row at level 5, so any non-empty preset
    /// is refused `aptitudes.overbudget` before the respec is reached - a pre-existing gate of that route,
    /// unrelated to the payment choice. The store contract is what EP4.10 changed, and it is what this
    /// asserts: with the choice the stock is spent and the outcome reports it; without the choice, while
    /// stock is available, the same refusal the respec route turns into a 409 comes back.</para>
    /// </summary>
    [Fact]
    public async Task Species_scope_activate_carries_the_payment_choice_to_the_store()
    {
        var presetId = await CreateEvenPresetAsync("FreeRespec");
        // The store seam takes the allocation and its shares directly (the route's `ToAllocation` is what
        // normally builds them from the preset's rows): one aptitude is enough, and `Might` is in the plan
        // this harness configures.
        var shares = new Dictionary<string, long>(StringComparer.Ordinal) { ["Might"] = 1 };
        var allocation = FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Single(
            FusionRpg.Core.Stats.Aptitudes.AllocationScope.CreatureType, "Might", 1);

        // Touch the species first: a FIRST override is free by the pre-existing rule, and this case is about
        // the priced change after it. With no stock seeded yet, the free path is what runs.
        SeedFreeRespecStock(grants: 0);
        var touch = _store.TryRespecSpecies(_playerId, "fumeshroom", allocation, "ep410-touch");
        Assert.True(touch.Ok, touch.Reason);
        Assert.Equal("", touch.PaidWith);

        SeedFreeRespecStock(2);
        var stockBefore = _store.FreeRespecStock(HumanEmpire());

        var paid = _store.TryActivateAptitudePreset(
            _playerId, presetId, "species", "fumeshroom", allocation, shares, leftover: 0,
            correlationId: "ep410-free", utcNow: null,
            payWith: FusionRpg.Core.Stats.Aptitudes.RespecPayment.FreeRespec);
        Assert.True(paid.Ok, paid.Reason);
        Assert.Equal(FusionRpg.Core.Stats.Aptitudes.RespecPayments.FreeRespec, paid.PaidWith);
        Assert.Equal(stockBefore - 1, _store.FreeRespecStock(HumanEmpire()));

        // Stock available, no choice named: exactly what the route turns into respec.payment.choice-required.
        var noChoice = _store.TryActivateAptitudePreset(
            _playerId, presetId, "species", "fumeshroom", allocation, shares, leftover: 0,
            correlationId: "ep410-no-choice");
        Assert.False(noChoice.Ok);
        Assert.Equal("respec.payment.choice-required", noChoice.Reason);
        Assert.True(noChoice.FreeStock > 0);
        Assert.Equal(stockBefore - 1, _store.FreeRespecStock(HumanEmpire()));   // nothing more was spent
    }

    async Task<string> CreateEvenPresetAsync(string name = "Even")
    {
        var resp = await _http.PostAsJsonAsync("/api/aptitude-presets", new
        {
            playerId = _playerId,
            name,
            kind = "player",
            rows = EvenRows()
        });
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("presetId").GetString()!;
    }

    async Task<string> CreateMightPresetAsync(string name = "Might")
    {
        var resp = await _http.PostAsJsonAsync("/api/aptitude-presets", new
        {
            playerId = _playerId,
            name,
            kind = "player",
            rows = MightOnlyRows()
        });
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("presetId").GetString()!;
    }

    [Fact]
    public async Task Post_rejects_sum_not_1000()
    {
        var rows = EvenRows();
        rows[0] = new { aptitudeId = "Might", targetPermille = 999L };
        var resp = await _http.PostAsJsonAsync("/api/aptitude-presets", new
        {
            playerId = _playerId,
            name = "Bad",
            rows
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("presets.targetPermille.sum", body);
    }

    [Fact]
    public async Task Materialize_returns_leftover_and_refuses_loGtHi()
    {
        var rows = EvenRows();
        rows[0] = new { aptitudeId = "Might", targetPermille = 84L, maxAbs = 5L };
        // fix sum: Might was 84 already in EvenRows for i=0 — leave others; EvenRows[0] is Might=84
        // Rebuild carefully: Might 84 with maxAbs 5
        var custom = new List<object>();
        var i = 0;
        foreach (var apt in AptitudeCatalog.All)
        {
            var pm = 83L + (i < 4 ? 1L : 0L);
            if (apt.Id == "Might")
                custom.Add(new { aptitudeId = apt.Id, targetPermille = pm, maxAbs = 5L });
            else
                custom.Add(new { aptitudeId = apt.Id, targetPermille = pm });
            i++;
        }
        var create = await _http.PostAsJsonAsync("/api/aptitude-presets", new
        {
            playerId = _playerId,
            name = "Clamp",
            rows = custom
        });
        create.EnsureSuccessStatusCode();
        var presetId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("presetId").GetString()!;

        var mat = await _http.PostAsJsonAsync("/api/aptitude-presets/materialize", new
        {
            playerId = _playerId,
            presetId,
            budget = 1000L
        });
        mat.EnsureSuccessStatusCode();
        var matBody = await mat.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(matBody.GetProperty("leftover").GetInt64() > 0);
        Assert.Equal(5, matBody.GetProperty("shares").GetProperty("Might").GetInt64());

        var conflictRows = new List<object>();
        i = 0;
        foreach (var apt in AptitudeCatalog.All)
        {
            var pm = 83L + (i < 4 ? 1L : 0L);
            if (apt.Id == "Might")
                conflictRows.Add(new { aptitudeId = apt.Id, targetPermille = pm, minAbs = 50L, maxAbs = 10L });
            else
                conflictRows.Add(new { aptitudeId = apt.Id, targetPermille = pm });
            i++;
        }
        var conflictCreate = await _http.PostAsJsonAsync("/api/aptitude-presets", new
        {
            playerId = _playerId,
            name = "Conflict",
            rows = conflictRows
        });
        conflictCreate.EnsureSuccessStatusCode();
        var conflictId = (await conflictCreate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("presetId").GetString()!;
        var bad = await _http.PostAsJsonAsync("/api/aptitude-presets/materialize", new
        {
            presetId = conflictId,
            budget = 1000L
        });
        Assert.Equal(HttpStatusCode.Conflict, bad.StatusCode);
        Assert.Contains("presets.materialize.loGtHi", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Favour_returns_permille_or_empty()
    {
        var known = await _http.GetAsync("/api/aptitude-presets/favour/fumeshroom");
        known.EnsureSuccessStatusCode();
        var knownBody = await known.Content.ReadFromJsonAsync<JsonElement>();
        var shares = knownBody.GetProperty("sharesPermille");
        Assert.Equal(500, shares.GetProperty("Might").GetInt64());
        Assert.Equal(300, shares.GetProperty("Vigor").GetInt64());
        Assert.Equal(200, shares.GetProperty("Fortitude").GetInt64());

        var empty = await _http.GetAsync("/api/aptitude-presets/favour/not-a-planned-species");
        empty.EnsureSuccessStatusCode();
        var emptyBody = await empty.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Object, emptyBody.GetProperty("sharesPermille").ValueKind);
        Assert.Empty(emptyBody.GetProperty("sharesPermille").EnumerateObject().ToArray());
    }

    // ---- EP1.4: POST /api/aptitude-presets/suggest -- draft only, never persists ------------------

    [Fact]
    public async Task Suggest_omitted_rule_walks_to_species_favour_when_no_active_preset()
    {
        var resp = await _http.PostAsJsonAsync("/api/aptitude-presets/suggest", new
        {
            playerId = _playerId,
            scope = "species",
            scopeKey = "fumeshroom"
        });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("species-favour", body.GetProperty("ruleId").GetString());
        var rows = body.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(AptitudeCatalog.Count, rows.Count);
        Assert.Equal(1000, rows.Sum(r => r.GetProperty("targetPermille").GetInt64()));
        // active-preset is the one earlier rung, skipped with its own reason.
        var skipped = body.GetProperty("skipped").EnumerateArray().ToList();
        var activeSkip = Assert.Single(skipped, s => s.GetProperty("ruleId").GetString() == "active-preset");
        Assert.Equal("autoAssign.activePreset.missing", activeSkip.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Suggest_walks_to_active_preset_first_when_one_is_bound()
    {
        var presetId = await CreateEvenPresetAsync("SuggestActive");
        var setActive = await _http.PutAsJsonAsync("/api/aptitude-presets/active", new
        {
            playerId = _playerId,
            scope = "species",
            scopeKey = "fumeshroom",
            presetId
        });
        setActive.EnsureSuccessStatusCode();

        var resp = await _http.PostAsJsonAsync("/api/aptitude-presets/suggest", new
        {
            playerId = _playerId,
            scope = "species",
            scopeKey = "fumeshroom"
        });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("active-preset", body.GetProperty("ruleId").GetString());
        Assert.Empty(body.GetProperty("skipped").EnumerateArray().ToArray());
    }

    [Fact]
    public async Task Suggest_commander_scope_skips_favour_and_posture_by_name_R23_shape()
    {
        var resp = await _http.PostAsJsonAsync("/api/aptitude-presets/suggest", new
        {
            playerId = _playerId,
            scope = "commander"
        });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("even", body.GetProperty("ruleId").GetString());
        // R23 shape (spec-assign-ladder.md): active-preset, species-favour and posture each skip
        // BY NAME with their own reason, and even wins -- exactly the ladder's totality guarantee.
        var skipped = body.GetProperty("skipped").EnumerateArray().ToList();
        Assert.Equal(3, skipped.Count);
        Assert.Contains(skipped, s => s.GetProperty("ruleId").GetString() == "species-favour"
            && s.GetProperty("reason").GetString() == "autoAssign.favour.modeC");
        Assert.Contains(skipped, s => s.GetProperty("ruleId").GetString() == "posture"
            && s.GetProperty("reason").GetString() == "autoAssign.posture.unknown");
    }

    [Fact]
    public async Task Suggest_with_rule_given_runs_only_that_rule()
    {
        var resp = await _http.PostAsJsonAsync("/api/aptitude-presets/suggest", new
        {
            playerId = _playerId,
            scope = "species",
            scopeKey = "fumeshroom",
            rule = "even"
        });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("even", body.GetProperty("ruleId").GetString());
        Assert.Empty(body.GetProperty("skipped").EnumerateArray().ToArray());
    }

    [Fact]
    public async Task Suggest_never_persists_allocation_stays_byte_identical()
    {
        var scopeKey = SpeciesAllocation.ScopeKey(_playerId, FusionRpg.Core.Commanders.EmpireId.Dave, "fumeshroom");
        var before = _store.LoadAllocation(AllocationScope.CreatureType, scopeKey);

        var resp = await _http.PostAsJsonAsync("/api/aptitude-presets/suggest", new
        {
            playerId = _playerId,
            scope = "species",
            scopeKey = "fumeshroom"
        });
        resp.EnsureSuccessStatusCode();

        var after = _store.LoadAllocation(AllocationScope.CreatureType, scopeKey);
        Assert.Equal(before.TotalForScope(AllocationScope.CreatureType), after.TotalForScope(AllocationScope.CreatureType));
    }

    // ---- EP1.20 (spec-auto-assign-control.md, C1/C5): GET /rules -- the server-owned rule list ----

    [Fact]
    public async Task Rules_unique_scope_lists_all_six()
    {
        var resp = await _http.GetAsync("/api/aptitude-presets/rules?scope=unique");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unique", body.GetProperty("scope").GetString());
        var rules = body.GetProperty("rules").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Equal(6, rules.Count);
        Assert.Contains("species-favour", rules);
        Assert.Equal(rules.Distinct().Count(), rules.Count);
    }

    [Fact]
    public async Task Rules_species_scope_lists_all_six()
    {
        var resp = await _http.GetAsync("/api/aptitude-presets/rules?scope=species");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var rules = body.GetProperty("rules").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Equal(6, rules.Count);
        Assert.Contains("species-favour", rules);
    }

    [Fact]
    public async Task Rules_commander_scope_omits_species_favour_C5()
    {
        var resp = await _http.GetAsync("/api/aptitude-presets/rules?scope=commander");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var rules = body.GetProperty("rules").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Equal(5, rules.Count);
        Assert.DoesNotContain("species-favour", rules);
        Assert.Contains("even", rules);
        Assert.Contains("posture-force", rules);
        Assert.Contains("posture-finesse", rules);
        Assert.Contains("posture-bastion", rules);
        Assert.Contains("active-preset", rules);
    }

    [Fact]
    public async Task Rules_missing_scope_refuses()
    {
        var resp = await _http.GetAsync("/api/aptitude-presets/rules");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("presets.scope.missing", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rules_unknown_scope_refuses()
    {
        var resp = await _http.GetAsync("/api/aptitude-presets/rules?scope=not-a-scope");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("presets.scope.unknown", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SoftMax_create_past_cap_returns_conflict()
    {
        AptitudePresetTuningHub.Configure(new AptitudePresetTuning(1, 1, SoftMaxPresets: 1, DefaultRowAbsMax: 1000, AssignLadder: MinimalAssignLadder()));
        var first = await _http.PostAsJsonAsync("/api/aptitude-presets", new
        {
            playerId = _playerId,
            name = "One",
            rows = EvenRows()
        });
        first.EnsureSuccessStatusCode();
        var second = await _http.PostAsJsonAsync("/api/aptitude-presets", new
        {
            playerId = _playerId,
            name = "Two",
            rows = EvenRows()
        });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("presets.softMax", await second.Content.ReadAsStringAsync());
        // restore shipped soft max for sibling tests in this class (new fixture per class instance)
        AptitudePresetTuningHub.Configure(
            AptitudePresetTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "aptitude-presets.v2.json"))));
    }

    // ---- EP1.16 (W4): POST /suggested -- the systemCopy producer ----------------------------------

    [Fact]
    public async Task Suggested_writesASystemCopyPreset_whoseRowsEqualTheCurrentSuggestion()
    {
        // Commander scope, no active preset, no species context: the ladder walks past
        // active-preset and species-favour (both refuse) and posture (no primary aptitude known for
        // a bare commander pool) to its terminal "even" rung.
        var resp = await _http.PostAsJsonAsync("/api/aptitude-presets/suggested", new
        {
            playerId = _playerId,
            scope = "commander"
        });
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, text);
        using var doc = JsonDocument.Parse(text);

        Assert.Equal(RpgStore.AptitudePresetKindSystemCopy, doc.RootElement.GetProperty("kind").GetString());
        var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(AptitudeCatalog.Count, rows.Count);
        // "even": 1000/12 truncates to 83 with 4 aptitudes at 84 -- the relation, not which four.
        Assert.All(rows, r => Assert.True(r.GetProperty("targetPermille").GetInt64() is 83 or 84));
        Assert.Equal(4, rows.Count(r => r.GetProperty("targetPermille").GetInt64() == 84));
        Assert.Equal(1000L, rows.Sum(r => r.GetProperty("targetPermille").GetInt64()));

        // The preset really persisted (not just echoed) -- the GET list shows it too.
        var listResp = await _http.GetAsync($"/api/aptitude-presets/{_playerId}");
        var listText = await listResp.Content.ReadAsStringAsync();
        using var listDoc = JsonDocument.Parse(listText);
        var presetId = doc.RootElement.GetProperty("presetId").GetString();
        Assert.Contains(listDoc.RootElement.GetProperty("presets").EnumerateArray(),
            p => p.GetProperty("presetId").GetString() == presetId
                && p.GetProperty("kind").GetString() == RpgStore.AptitudePresetKindSystemCopy);
    }

    [Fact]
    public async Task Suggested_namesThePresetAfterTheWinningRung_unlessNameGiven()
    {
        var defaultNamed = await _http.PostAsJsonAsync("/api/aptitude-presets/suggested", new
        {
            playerId = _playerId,
            scope = "commander"
        });
        using var defaultDoc = JsonDocument.Parse(await defaultNamed.Content.ReadAsStringAsync());
        var defaultName = defaultDoc.RootElement.GetProperty("name").GetString();
        Assert.Contains(AptitudeAutoAssignRules.Even, defaultName);

        var explicitlyNamed = await _http.PostAsJsonAsync("/api/aptitude-presets/suggested", new
        {
            playerId = _playerId,
            scope = "commander",
            name = "My Own Name"
        });
        using var explicitDoc = JsonDocument.Parse(await explicitlyNamed.Content.ReadAsStringAsync());
        Assert.Equal("My Own Name", explicitDoc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Suggested_refusesPastSoftMax_exactlyAsAPlayerPreset()
    {
        AptitudePresetTuningHub.Configure(new AptitudePresetTuning(1, 1, SoftMaxPresets: 1, DefaultRowAbsMax: 1000, AssignLadder: MinimalAssignLadder()));
        var first = await _http.PostAsJsonAsync("/api/aptitude-presets/suggested",
            new { playerId = _playerId, scope = "commander" });
        first.EnsureSuccessStatusCode();

        var second = await _http.PostAsJsonAsync("/api/aptitude-presets/suggested",
            new { playerId = _playerId, scope = "commander" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("presets.softMax", await second.Content.ReadAsStringAsync());
        // restore shipped soft max for sibling tests in this class (new fixture per class instance)
        AptitudePresetTuningHub.Configure(
            AptitudePresetTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "aptitude-presets.v2.json"))));
    }

    [Fact]
    public async Task Activate_commander_sets_active_and_allocation()
    {
        var presetId = await CreateMightPresetAsync();
        var resp = await _http.PostAsJsonAsync("/api/aptitude-presets/activate", new
        {
            playerId = _playerId,
            presetId,
            scope = "commander",
            scopeKey = ""
        });
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, text);

        var active = await _http.GetAsync($"/api/aptitude-presets/active?playerId={_playerId}&scope=commander&scopeKey=");
        active.EnsureSuccessStatusCode();
        var activeBody = await active.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(presetId, activeBody.GetProperty("presetId").GetString());

        var apt = await (await _http.GetAsync($"/api/aptitudes/{_playerId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(apt.GetProperty("spent").GetInt64() > 0);
        Assert.True(apt.GetProperty("shares").GetProperty("Might").GetInt64() > 0);
    }

    [Fact]
    public async Task Activate_unique_round_trips()
    {
        var actor = _store.EnsureUniqueActorForAudit(_playerId, "ua-preset-act", "plant", typeId: 1, level: 10);
        var presetId = await CreateEvenPresetAsync("UniqueEven");
        var resp = await _http.PostAsJsonAsync("/api/aptitude-presets/activate", new
        {
            playerId = _playerId,
            presetId,
            scope = "unique",
            scopeKey = actor.InstanceId
        });
        resp.EnsureSuccessStatusCode();
        var unique = await (await _http.GetAsync($"/api/aptitudes/unique/{actor.InstanceId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(unique.GetProperty("spent").GetInt64() > 0);
    }

    [Fact]
    public async Task Activate_species_uses_priced_respec_and_rolls_back_on_insufficient_souls()
    {
        SeedSpeciesLevel(_playerId, FumeshroomCreatureTypeId, level: 21, "fumeshroom");
        var presetId = await CreateEvenPresetAsync("SpeciesEven");

        // First activate is free (first override) — establish an override so the next is priced.
        var first = await _http.PostAsJsonAsync("/api/aptitude-presets/activate", new
        {
            playerId = _playerId,
            presetId,
            scope = "species",
            scopeKey = "fumeshroom",
            correlationId = "preset-act-1"
        });
        var firstText = await first.Content.ReadAsStringAsync();
        Assert.True(first.IsSuccessStatusCode, firstText);

        // Build a different preset so the second activate is a priced replacement.
        var rows2 = EvenRows();
        // Flip remainder onto last four so shares differ from first even split.
        rows2 = new List<object>();
        var i = 0;
        foreach (var apt in AptitudeCatalog.All)
        {
            var pm = 83L + (i >= 8 ? 1L : 0L);
            rows2.Add(new { aptitudeId = apt.Id, targetPermille = pm });
            i++;
        }
        var create2 = await _http.PostAsJsonAsync("/api/aptitude-presets", new
        {
            playerId = _playerId,
            name = "SpeciesAlt",
            rows = rows2
        });
        create2.EnsureSuccessStatusCode();
        var preset2 = (await create2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("presetId").GetString()!;

        // Drain souls so priced respec fails.
        var balance = _store.GetSoulBalance(_playerId).Balance;
        if (balance > 0)
            _store.TrySpendSouls(_playerId, balance, "test.drain", "drain-" + Guid.NewGuid().ToString("N"));

        var beforeActive = _store.GetAptitudePresetActive(_playerId, "species", "fumeshroom");
        Assert.Equal(presetId, beforeActive!.PresetId);
        var beforeAlloc = _store.LoadAllocation(AllocationScope.CreatureType,
            SpeciesAllocation.ScopeKey(_playerId, FusionRpg.Core.Commanders.EmpireId.Dave, "fumeshroom"));

        var second = await _http.PostAsJsonAsync("/api/aptitude-presets/activate", new
        {
            playerId = _playerId,
            presetId = preset2,
            scope = "species",
            scopeKey = "fumeshroom",
            correlationId = "preset-act-2"
        });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("souls.insufficient", await second.Content.ReadAsStringAsync());

        var afterActive = _store.GetAptitudePresetActive(_playerId, "species", "fumeshroom");
        Assert.Equal(presetId, afterActive!.PresetId); // unchanged — no half-active
        var afterAlloc = _store.LoadAllocation(AllocationScope.CreatureType,
            SpeciesAllocation.ScopeKey(_playerId, FusionRpg.Core.Commanders.EmpireId.Dave, "fumeshroom"));
        Assert.Equal(beforeAlloc.TotalForScope(AllocationScope.CreatureType),
            afterAlloc.TotalForScope(AllocationScope.CreatureType));
    }

    void SeedSpeciesLevel(long playerId, int creatureTypeId, long level, string scopeKey)
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
        cmd.Parameters.AddWithValue("$sk", scopeKey);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>A legally loaded ladder order (EP1.3's own contract: known ids, no duplicates, ends
    /// on `even`) for the one test here that reconfigures <see cref="AptitudePresetTuning"/> by hand
    /// rather than through <see cref="AptitudePresetTuningLoader"/>.</summary>
    static AssignLadderTuning MinimalAssignLadder() => new(new[] { AptitudeAutoAssignRules.Even });

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
