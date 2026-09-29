using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.World.LegionCargo;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using FusionRpg.Data.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Server.Tests.Notifications;

namespace FusionRpg.Server.Tests.WorldClaimEndpoints;

// Task 4A.3 (`claim-endpoints`, empire-inventory-surfaces module 2): the four routes over the real
// HTTP mapping (`WorldEndpoints.cs`) against a warm in-memory store — presence-gated listing,
// hidden-until-found on the body, unknown-vs-empty, foreign-legion refusal, filer idempotency,
// file-vs-resolve separation, one-scope reads, verbatim refusals, no-weight-on-wire, and the
// stored (never replayed) claim line.
//
// All stores are in-memory (`DataTestStore.Create`); no temp dir, nothing to delete. Every test
// builds its own world (`w-claim-*`), so no test shares turn state, positions, or caches with
// another — xUnit runs tests in one class sequentially, but isolation here is by construction,
// not by ordering.
[Collection("NotificationHub")]
public class ClaimEndpointsTests : IAsyncLifetime
{
    const string DaveLegion = "e-dave-legion-1"; // template: standing at "homeworld"
    const string ForeignLegion = "e-zomboss-band-1"; // template: zomboss warband at "black-gate"
    const string HomeSector = "homeworld";
    const string AwaySector = "ash-waste";
    const string ForeignSector = "black-gate";
    const string HomeLane = "l-home-ember"; // homeworld <-> ember-hollow

    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        WorldPolicyTestBootstrap.EnsureConfigured();

        // Same tuning every cargo suite configures (100 mass / 2 slots per member): identical
        // values keep the suites order-independent under parallel runs.
        ScopedInventoryPolicy.Configure(new ScopedInventoryTuning(1, 1, CargoWeightPerUnit: 100, CargoSlotsPerUnit: 2));

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

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
        var test = _app.MapGroup("/api/test");
        test.MapWorldTest();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _testStore.Dispose();
    }

    static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ---- fixture helpers (raw SQL against the in-memory store — these tables are owned by
    // sibling modules, not by the routes under test) ------------------------------------------

    async Task<string> CreateWorldAsync()
    {
        var worldId = "w-claim-" + Guid.NewGuid().ToString("N");
        var created = await _http.PostAsJsonAsync("/api/test/world/create", new { worldId, seed = "1" });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return worldId;
    }

    string SeedCache(string worldId, string placeKind, string placeRef, int items = 1, int inVoid = 0)
    {
        // Caches live outside any world row (no world_id column): ids are unique per call, so
        // per-test worlds never observe each other's caches even though the table is shared.
        var cacheId = "cc_" + Guid.NewGuid().ToString("N");
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using (var ins = db.CreateCommand())
        {
            ins.CommandText = """
                INSERT INTO rpg_corpse_cache
                  (cache_id, place_kind, place_ref, source_kind, created_utc, decay_started_utc, in_void, revision)
                VALUES ($id, $k, $r, 'legion_death', $now, NULL, $v, 0);
                """;
            ins.Parameters.AddWithValue("$id", cacheId);
            ins.Parameters.AddWithValue("$k", placeKind);
            ins.Parameters.AddWithValue("$r", placeRef);
            ins.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            ins.Parameters.AddWithValue("$v", inVoid);
            Assert.Equal(1, ins.ExecuteNonQuery());
        }
        for (var seq = 0; seq < items; seq++)
        {
            using var item = db.CreateCommand();
            item.CommandText = """
                INSERT INTO rpg_corpse_cache_item
                  (cache_id, seq, kind, instance_id, container_id, qty, origin_owner)
                VALUES ($id, $s, 'instance', $inst, NULL, NULL, 'e-dead-legion');
                """;
            item.Parameters.AddWithValue("$id", cacheId);
            item.Parameters.AddWithValue("$s", seq);
            item.Parameters.AddWithValue("$inst", "inst-" + Guid.NewGuid().ToString("N"));
            Assert.Equal(1, item.ExecuteNonQuery());
        }
        return cacheId;
    }

    void MoveLegion(string worldId, string entityId, string sectorId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE rpg_world_entities
            SET at_sector_id = $s, on_lane_id = NULL,
                on_lane_toward_sector_id = NULL, lane_progress_milli = 0
            WHERE world_id = $w AND entity_id = $e;
            """;
        cmd.Parameters.AddWithValue("$s", sectorId);
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    void MoveLegionToLane(string worldId, string entityId, string laneId, string towardSectorId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE rpg_world_entities
            SET at_sector_id = NULL, on_lane_id = $l,
                on_lane_toward_sector_id = $t, lane_progress_milli = 500
            WHERE world_id = $w AND entity_id = $e;
            """;
        cmd.Parameters.AddWithValue("$l", laneId);
        cmd.Parameters.AddWithValue("$t", towardSectorId);
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    void CorruptLegionPosition(string worldId, string entityId, bool bothSet)
    {
        // Violates WorldState's own "at a sector, or on a lane — never both, never neither"
        // invariant: the list read must see nothing rather than a guess.
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = bothSet
            ? """
                UPDATE rpg_world_entities
                SET at_sector_id = $s, on_lane_id = $l
                WHERE world_id = $w AND entity_id = $e;
                """
            : """
                UPDATE rpg_world_entities
                SET at_sector_id = NULL, on_lane_id = NULL
                WHERE world_id = $w AND entity_id = $e;
                """;
        if (bothSet)
        {
            cmd.Parameters.AddWithValue("$s", HomeSector);
            cmd.Parameters.AddWithValue("$l", HomeLane);
        }
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    void SeedCargoRow(string worldId, string entityId, int seq)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_world_entity_cargo
              (world_id, entity_id, seq, kind, instance_id, container_id, qty, weight_each)
            VALUES ($w, $e, $s, 'instance', $inst, NULL, NULL, 10);
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        cmd.Parameters.AddWithValue("$s", seq);
        cmd.Parameters.AddWithValue("$inst", "inst-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    void SeedStorageRow(string worldId, string sectorId, int seq)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_world_sector_storage
              (world_id, sector_id, seq, kind, instance_id, container_id, qty)
            VALUES ($w, $s, $seq, 'instance', $inst, NULL, NULL);
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$s", sectorId);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$inst", "inst-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    static string ReasonOf(JsonElement result) =>
        result.GetProperty("reason").GetString() ?? "";

    // ---- AC 1: presence-gated listing ----------------------------------------------------------

    [Fact]
    public async Task Legion_on_cache_sector_lists_it_with_count_only()
    {
        var worldId = await CreateWorldAsync();
        var cacheId = SeedCache(worldId, "world_sector", HomeSector, items: 2);

        var listed = await _http.GetAsync($"/api/world/{worldId}/legions/{DaveLegion}/claimable-caches");
        Assert.True(listed.IsSuccessStatusCode, await listed.Content.ReadAsStringAsync());
        var body = await listed.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(worldId, body.GetProperty("worldId").GetString());
        Assert.Equal(DaveLegion, body.GetProperty("entityId").GetString());
        Assert.Equal(0, body.GetProperty("asOfTurn").GetInt32());
        var row = Assert.Single(body.GetProperty("caches").EnumerateArray());
        Assert.Equal(cacheId, row.GetProperty("cacheId").GetString());
        Assert.Equal("world_sector", row.GetProperty("placeKind").GetString());
        Assert.Equal(HomeSector, row.GetProperty("placeRef").GetString());
        Assert.Equal("legion_death", row.GetProperty("sourceKind").GetString());
        Assert.Equal(2, row.GetProperty("itemCount").GetInt32());
        Assert.NotEmpty(row.GetProperty("createdUtc").GetString() ?? "");
        Assert.Equal(
            FusionRpg.Core.World.WorldTuningHub.Tuning.Movement.ClaimCostMilli,
            body.GetProperty("claimCostMilli").GetInt32());
    }

    [Fact]
    public async Task Legion_one_sector_away_lists_nothing()
    {
        var worldId = await CreateWorldAsync();
        SeedCache(worldId, "world_sector", HomeSector);
        MoveLegion(worldId, DaveLegion, AwaySector);

        var listed = await _http.GetAsync($"/api/world/{worldId}/legions/{DaveLegion}/claimable-caches");
        Assert.True(listed.IsSuccessStatusCode, await listed.Content.ReadAsStringAsync());
        var body = await listed.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Empty(body.GetProperty("caches").EnumerateArray());
    }

    [Fact]
    public async Task Legion_on_lane_lists_lane_cache_but_not_sector_cache()
    {
        var worldId = await CreateWorldAsync();
        var laneCache = SeedCache(worldId, "world_lane", HomeLane);
        SeedCache(worldId, "world_sector", HomeSector);
        MoveLegionToLane(worldId, DaveLegion, HomeLane, "ember-hollow");

        var listed = await _http.GetAsync($"/api/world/{worldId}/legions/{DaveLegion}/claimable-caches");
        Assert.True(listed.IsSuccessStatusCode, await listed.Content.ReadAsStringAsync());
        var body = await listed.Content.ReadFromJsonAsync<JsonElement>();

        var row = Assert.Single(body.GetProperty("caches").EnumerateArray());
        Assert.Equal(laneCache, row.GetProperty("cacheId").GetString());
    }

    [Theory]
    [InlineData(true)] // both-set: at a sector AND on a lane
    [InlineData(false)] // neither-set: nowhere at all
    public async Task Corrupt_position_lists_nothing(bool bothSet)
    {
        var worldId = await CreateWorldAsync();
        SeedCache(worldId, "world_sector", HomeSector);
        CorruptLegionPosition(worldId, DaveLegion, bothSet);

        var listed = await _http.GetAsync($"/api/world/{worldId}/legions/{DaveLegion}/claimable-caches");
        Assert.True(listed.IsSuccessStatusCode, await listed.Content.ReadAsStringAsync());
        var body = await listed.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Empty(body.GetProperty("caches").EnumerateArray());
    }

    // ---- AC 2: hidden-until-found holds on the body ---------------------------------------------

    [Fact]
    public async Task No_reachable_cache_response_carries_zero_cache_ids()
    {
        var worldId = await CreateWorldAsync();
        var cacheId = SeedCache(worldId, "world_sector", HomeSector);
        MoveLegion(worldId, DaveLegion, AwaySector);

        var listed = await _http.GetAsync($"/api/world/{worldId}/legions/{DaveLegion}/claimable-caches");
        var raw = await listed.Content.ReadAsStringAsync();
        Assert.True(listed.IsSuccessStatusCode, raw);

        // Asserted on the body, not on FE behavior: the id is absent, never masked.
        Assert.DoesNotContain(cacheId, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("visible", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Claimable_cache_dto_has_no_visibility_member()
    {
        // Structural: there is no `visible: false` member for a client to misread — unreachable
        // caches are absent, not masked (§Design 2).
        var names = typeof(ClaimableCacheDto).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name);
        Assert.DoesNotContain(names, n => n.Contains("visible", StringComparison.OrdinalIgnoreCase));
    }

    // ---- AC 3: unknown-vs-empty -----------------------------------------------------------------

    [Fact]
    public async Task Unknown_legion_is_caller_error_not_empty()
    {
        var worldId = await CreateWorldAsync();

        var missing = await _http.GetAsync($"/api/world/{worldId}/legions/e-no-such-legion/claimable-caches");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
        var body = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("entity.unknown", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Known_legion_with_no_cache_reads_200_empty()
    {
        var worldId = await CreateWorldAsync();

        var listed = await _http.GetAsync($"/api/world/{worldId}/legions/{DaveLegion}/claimable-caches");
        Assert.True(listed.IsSuccessStatusCode, await listed.Content.ReadAsStringAsync());
        var body = await listed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("caches").EnumerateArray());
    }

    // ---- AC 4: foreign legion refused before reachability ----------------------------------------

    [Fact]
    public async Task Foreign_legion_refused_even_where_a_cache_is_reachable()
    {
        var worldId = await CreateWorldAsync();
        // Reachable at the foreign legion's own position — ownership still precedes reachability.
        SeedCache(worldId, "world_sector", ForeignSector);

        var listed = await _http.GetAsync($"/api/world/{worldId}/legions/{ForeignLegion}/claimable-caches");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, listed.StatusCode);
        var body = await listed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("entity.not-yours", body.GetProperty("reason").GetString());

        var cargo = await _http.GetAsync($"/api/world/{worldId}/legions/{ForeignLegion}/cargo");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, cargo.StatusCode);
        var cargoBody = await cargo.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("entity.not-yours", cargoBody.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Unknown_viewer_refused_on_all_three_reads()
    {
        var worldId = await CreateWorldAsync();

        foreach (var path in new[]
        {
            $"/api/world/{worldId}/legions/{DaveLegion}/claimable-caches?asFaction=nope",
            $"/api/world/{worldId}/legions/{DaveLegion}/cargo?asFaction=nope",
            $"/api/world/{worldId}/sectors/{HomeSector}/storage?asFaction=nope",
        })
        {
            var response = await _http.GetAsync(path);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("faction.unknown", body.GetProperty("reason").GetString());
        }
    }

    // ---- AC 5: filer idempotency + submit-time refusals ------------------------------------------

    [Fact]
    public async Task Same_command_id_posted_twice_replays_with_one_stored_row()
    {
        var worldId = await CreateWorldAsync();
        var cacheId = SeedCache(worldId, "world_sector", HomeSector);

        var first = await _http.PostAsJsonAsync($"/api/world/{worldId}/legions/{DaveLegion}/claims",
            new { commandId = "claim-once", cacheId });
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(firstBody.GetProperty("ok").GetBoolean(), firstBody.ToString());
        Assert.False(firstBody.GetProperty("replayed").GetBoolean(), firstBody.ToString());

        var second = await _http.PostAsJsonAsync($"/api/world/{worldId}/legions/{DaveLegion}/claims",
            new { commandId = "claim-once", cacheId });
        Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(secondBody.GetProperty("ok").GetBoolean(), secondBody.ToString());
        Assert.True(secondBody.GetProperty("replayed").GetBoolean(), secondBody.ToString());

        Assert.Single(_store.ListWorldCommands(worldId, 0), c => c.CommandId == "claim-once");
    }

    [Fact]
    public async Task Missing_ids_refuse_at_submit_with_zero_writes()
    {
        var worldId = await CreateWorldAsync();
        var cacheId = SeedCache(worldId, "world_sector", HomeSector);

        var noCache = await _http.PostAsJsonAsync($"/api/world/{worldId}/legions/{DaveLegion}/claims",
            new { commandId = "claim-no-cache" });
        Assert.True(noCache.IsSuccessStatusCode, await noCache.Content.ReadAsStringAsync());
        var noCacheBody = await noCache.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(noCacheBody.GetProperty("ok").GetBoolean(), noCacheBody.ToString());
        Assert.Equal("cache.missing", ReasonOf(noCacheBody));

        var noId = await _http.PostAsJsonAsync($"/api/world/{worldId}/legions/{DaveLegion}/claims",
            new { cacheId });
        Assert.True(noId.IsSuccessStatusCode, await noId.Content.ReadAsStringAsync());
        var noIdBody = await noId.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(noIdBody.GetProperty("ok").GetBoolean(), noIdBody.ToString());
        Assert.Equal("command.id-missing", ReasonOf(noIdBody));
        // The resolve-time string is unreachable on this path: the filer surfaces the
        // submit-time string, never `correlation.missing`.
        Assert.NotEqual("correlation.missing", ReasonOf(noIdBody));

        Assert.Empty(_store.ListWorldCommands(worldId, 0));
    }

    // ---- AC 6: filer never resolves ---------------------------------------------------------------

    [Fact]
    public async Task Filed_claim_moves_nothing_until_commit()
    {
        var worldId = await CreateWorldAsync();
        var cacheId = SeedCache(worldId, "world_sector", HomeSector, items: 2);

        var cargoBefore = await _http.GetAsync($"/api/world/{worldId}/legions/{DaveLegion}/cargo");
        var cargoBody = await cargoBefore.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(cargoBody.GetProperty("rows").EnumerateArray());

        var filed = await _http.PostAsJsonAsync($"/api/world/{worldId}/legions/{DaveLegion}/claims",
            new { commandId = "claim-later", cacheId });
        Assert.True(filed.IsSuccessStatusCode, await filed.Content.ReadAsStringAsync());

        // Filing is not resolving (GG-15): the cache rows are untouched and cargo unchanged —
        // the claim moves only at commit through the 4A.1 pass.
        Assert.Equal(2, _store.ListCorpseCacheItems(cacheId).Count);
        Assert.Empty(_store.ListCargo(worldId, DaveLegion));

        // Still listed: reachability is undisturbed by the filing itself.
        var relisted = await _http.GetAsync($"/api/world/{worldId}/legions/{DaveLegion}/claimable-caches");
        var relistedBody = await relisted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(relistedBody.GetProperty("caches").EnumerateArray(),
            c => (c.GetProperty("cacheId").GetString() ?? "") == cacheId);
    }

    // ---- AC 7: one-scope reads ---------------------------------------------------------------------

    [Fact]
    public async Task Cargo_read_returns_exactly_this_legions_rows_with_live_capacities()
    {
        var worldId = await CreateWorldAsync();
        SeedCargoRow(worldId, DaveLegion, seq: 0);
        SeedCargoRow(worldId, DaveLegion, seq: 1);

        var cargo = await _http.GetAsync($"/api/world/{worldId}/legions/{DaveLegion}/cargo");
        Assert.True(cargo.IsSuccessStatusCode, await cargo.Content.ReadAsStringAsync());
        var body = await cargo.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(worldId, body.GetProperty("worldId").GetString());
        Assert.Equal(DaveLegion, body.GetProperty("entityId").GetString());
        var rows = body.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { 0, 1 }, rows.Select(r => r.GetProperty("seq").GetInt32()).ToArray());
        // Snapshot read-back: weight_each 10 aboard, instance rows weigh their snapshot each.
        Assert.All(rows, r => Assert.Equal(10, r.GetProperty("rowWeight").GetInt64()));
        Assert.Equal(20, body.GetProperty("weightUsed").GetInt64());
        // Template legion: 3 members × tuning (100 mass / 2 slots per member), computed fresh.
        Assert.Equal(300, body.GetProperty("weightCapacity").GetInt64());
        Assert.Equal(2, body.GetProperty("slotsUsed").GetInt32());
        Assert.Equal(6, body.GetProperty("slotCapacity").GetInt32());
    }

    [Fact]
    public async Task Storage_read_returns_exactly_this_sectors_rows_with_owner()
    {
        var worldId = await CreateWorldAsync();
        SeedStorageRow(worldId, HomeSector, seq: 0);
        SeedStorageRow(worldId, AwaySector, seq: 0);

        var storage = await _http.GetAsync($"/api/world/{worldId}/sectors/{HomeSector}/storage");
        Assert.True(storage.IsSuccessStatusCode, await storage.Content.ReadAsStringAsync());
        var body = await storage.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(worldId, body.GetProperty("worldId").GetString());
        Assert.Equal(HomeSector, body.GetProperty("sectorId").GetString());
        // Live owner read — the capture-header input (§Design 5).
        Assert.Equal("dave", body.GetProperty("ownerFactionId").GetString());
        var rows = body.GetProperty("rows").EnumerateArray().ToList();
        Assert.Single(rows);
        Assert.Equal(0, rows[0].GetProperty("seq").GetInt32());
        Assert.Equal(1, body.GetProperty("slotsUsed").GetInt32());
        // No ItemStorage vault built in the template corpus: zero slots, by construction.
        Assert.Equal(0, body.GetProperty("slotCapacity").GetInt64());

        var unknown = await _http.GetAsync($"/api/world/{worldId}/sectors/nope-sector/storage");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknown.StatusCode);
        var unknownBody = await unknown.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sector.unknown", unknownBody.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("caches")]
    [InlineData("cargos")]
    [InlineData("legions")]
    public async Task No_bulk_route_lists_across_legions_or_sectors(string bulk)
    {
        // Diablo-memory precedent (ideal §6): one legion's / one sector's rows per open — a probe
        // asserting no cross-legion/cross-sector route exists.
        var worldId = await CreateWorldAsync();
        var probe = await _http.GetAsync($"/api/world/{worldId}/{bulk}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, probe.StatusCode);
    }

    // ---- AC 8: refusals verbatim ---------------------------------------------------------------------

    [Fact]
    public async Task Forged_cache_id_files_then_drops_unreachable_at_commit()
    {
        var worldId = await CreateWorldAsync();

        // Admission checks shape only (`cache.missing` is for a blank id): a forged id files.
        var filed = await _http.PostAsJsonAsync($"/api/world/{worldId}/legions/{DaveLegion}/claims",
            new { commandId = "claim-forged", cacheId = "cc-forged" });
        var filedBody = await filed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(filedBody.GetProperty("ok").GetBoolean(), filedBody.ToString());

        var commit = await _http.PostAsJsonAsync($"/api/world/{worldId}/commit",
            new { commanderId = "dave", turn = 0 });
        Assert.True(commit.IsSuccessStatusCode, await commit.Content.ReadAsStringAsync());

        // The outcome reaches the player through the turn report, never the filer response —
        // and the string byte-matches the verb's own reachability refusal.
        var report = _store.GetWorldTurnReport(worldId, 0);
        Assert.NotNull(report);
        Assert.Contains(report!.Entries, e =>
            e.Kind == TurnReportKinds.CommandDropped && e.Detail == "cache.unreachable");
    }

    // ---- AC 9: no weight on the wire ---------------------------------------------------------------------

    [Fact]
    public void No_request_shape_carries_weight()
    {
        // Schema assertion over the filer request plus the generic command request (which also
        // files `claim-cache` orders): no `weightEach`-shaped member exists on either.
        foreach (var type in new[] { typeof(FileClaimCacheRequest), typeof(WorldCommandRequest) })
        {
            var names = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name);
            Assert.DoesNotContain(names, n => n.Contains("weight", StringComparison.OrdinalIgnoreCase));
        }

        // The rehydrated command carries no mass dimension either — weight resolves server-side
        // at commit, so a client that could name its own weight could mint capacity.
        var commandNames = typeof(FusionRpg.Core.World.Turn.WorldCommand)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name);
        Assert.DoesNotContain(commandNames, n => n.Contains("weight", StringComparison.OrdinalIgnoreCase));
    }

    // ---- AC 10: stored detail, not replay ---------------------------------------------------------------------

    [Fact]
    public async Task Committed_claim_line_is_read_from_the_stored_hot_tail()
    {
        var worldId = await CreateWorldAsync();
        // An empty cache claims `ok` with zero rows and resolves no mass — the filer→commit→
        // stored-report plumbing end to end without the 4A.1 weight probe (movement itself is
        // 4A.1-proven; this bullet is about WHERE the line is read from).
        var cacheId = SeedCache(worldId, "world_sector", HomeSector, items: 0);

        var filed = await _http.PostAsJsonAsync($"/api/world/{worldId}/legions/{DaveLegion}/claims",
            new { commandId = "claim-empty", cacheId });
        Assert.True(filed.IsSuccessStatusCode, await filed.Content.ReadAsStringAsync());

        var commit = await _http.PostAsJsonAsync($"/api/world/{worldId}/commit",
            new { commanderId = "dave", turn = 0 });
        Assert.True(commit.IsSuccessStatusCode, await commit.Content.ReadAsStringAsync());
        var commitBody = await commit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(commitBody.GetProperty("advanced").GetBoolean(), commitBody.ToString());

        // Read from the stored hot-tail report — never assumed to survive post-trim re-derivation
        // (spec-cargo-commands §Design 5 gap, documented here, not re-proven: re-derivation
        // replays Step only, so cargo entries are absent there by construction).
        var report = _store.GetWorldTurnReport(worldId, 0);
        Assert.NotNull(report);
        Assert.Contains(report!.Entries, e =>
            e.Kind == TurnReportKinds.Event && e.Detail == "cache.claimed:0+0");
        // The command log is the save and survives regardless: the filed order is still there.
        Assert.Contains(_store.ListWorldCommands(worldId, 0), c => c.CommandId == "claim-empty");
    }
}
