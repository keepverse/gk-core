using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Data.Sqlite;
using FusionRpg.Server.Tests.Notifications;

namespace FusionRpg.Server.Tests;

/// <summary>
/// world-stage W14 (`VisibleTo` as W-F1's three named clauses, fog defect C): the turn-report
/// projection's own filter, tested against a hand-seeded `report_json` row so each of W-F1's three
/// rules is proven in isolation rather than depending on emergent behaviour from a real multi-turn
/// simulation — the same seeding technique W6/W7/W9/W10 already used for dormant/hard-to-reach
/// fields.
/// </summary>
[Collection("NotificationHub")]
public class WorldTurnReportFogTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    const string WorldId = "w14-fog";
    const int Turn = 1;

    public async Task InitializeAsync()
    {
        ConfigureWorldTuningOnce();

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

        var created = await _http.PostAsJsonAsync("/api/test/world/create", new
        {
            worldId = WorldId, templateId = "two-hearths", seed = "7"
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());

        // "hot-ground" is deep in the corridor chain, unowned and un-garrisoned by anyone at
        // creation (`WorldTemplateCatalog.TwoHearths.cs`) — nothing of Dave's stands near it, so
        // `SeesNow` is false for it regardless of what belief is seeded below.
        SeedDaveGlimpseOf("hot-ground");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    SqliteConnection OpenHot()
    {
        return SqliteConnectionFactory.Open(_store.HotPath);
    }

    /// <summary>A once-seen, not-currently-watched belief for Dave — "scouted long ago", the exact
    /// shape W-F1's rule 3 (remembered sight) needs to be provable independently of rule 2.</summary>
    void SeedDaveGlimpseOf(string sectorId)
    {
        using var db = OpenHot();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO rpg_world_faction_intel
                (world_id, faction_id, sector_id, last_seen_turn, detail, owner_faction_id, phase,
                 climate, danger_band, slots_json, forces_json)
            VALUES ($w, 'dave', $s, 0, 'Glimpse', NULL, 'Unknown', NULL, 0, '[]', '[]');
            """;
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$s", sectorId);
        cmd.ExecuteNonQuery();
    }

    void SeedTurnLog(IReadOnlyList<TurnReportEntry> entries)
    {
        using var db = OpenHot();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO rpg_world_turn_log
                (world_id, turn, state_hash, engine_version, ruleset_version, seed, committed_utc, report_json)
            VALUES ($w, $t, 'test-hash', 1, 1, '7', '2026-01-01T00:00:00Z', $report);
            """;
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$t", Turn);
        cmd.Parameters.AddWithValue("$report", JsonSerializer.Serialize(entries));
        cmd.ExecuteNonQuery();
    }

    async Task<List<JsonElement>> EntriesFor(string faction)
    {
        var response = await _http.GetFromJsonAsync<JsonElement>($"/api/world/{WorldId}/turn/{Turn}?asFaction={faction}");
        return response.GetProperty("entries").EnumerateArray().ToList();
    }

    static bool Has(List<JsonElement> entries, string detail) =>
        entries.Any(e => e.GetProperty("detail").GetString() == detail);

    [Fact]
    public async Task Rule_1_audience_a_faction_scoped_line_reaches_its_own_faction_and_not_the_other()
    {
        SeedTurnLog(new[]
        {
            new TurnReportEntry("pressure", TurnReportKinds.Event, "dave", "loam.handicap:500", SectorId: null, Audience: "dave")
        });

        Assert.True(Has(await EntriesFor("dave"), "loam.handicap:500"));
        Assert.False(Has(await EntriesFor("zomboss"), "loam.handicap:500"));
    }

    [Fact]
    public async Task Rule_1_audience_a_halt_line_reaches_its_owner()
    {
        SeedTurnLog(new[]
        {
            new TurnReportEntry("movement", TurnReportKinds.Event, "e-dave", "halt:zoc:d-home", SectorId: "d-home", Audience: "dave")
        });

        Assert.True(Has(await EntriesFor("dave"), "halt:zoc:d-home"));
        Assert.False(Has(await EntriesFor("zomboss"), "halt:zoc:d-home"));
    }

    [Fact]
    public async Task Rule_2_vs_rule_3_a_battle_on_old_ground_is_withheld_while_a_claim_on_the_same_ground_is_shown()
    {
        // Same sector, same "scouted long ago, not currently seen" belief — proving the static/dynamic
        // split is decided by the line's own kind and detail token, not by re-seeding two different
        // sectors.
        SeedTurnLog(new[]
        {
            new TurnReportEntry("battles", TurnReportKinds.Battle, "b1", "sector:hot-ground:none", SectorId: "hot-ground"),
            new TurnReportEntry("movement", TurnReportKinds.Event, "c1", "claim.held:hot-ground", SectorId: "hot-ground")
        });

        var daveEntries = await EntriesFor("dave");
        Assert.False(Has(daveEntries, "sector:hot-ground:none"), "a battle on ground not currently watched must be withheld");
        Assert.True(Has(daveEntries, "claim.held:hot-ground"), "a claim on ground ever seen must still be shown");
    }

    [Fact]
    public async Task Rule_4_a_line_with_no_audience_and_no_ground_is_shown_to_everyone()
    {
        // world-map W59: the gap this suite left open — a calendar tick (week/month/season) carries
        // neither, and used to be dropped silently (`VisibleTo` returned false for exactly this
        // shape until this defect was found by playing the phase's own acceptance scenario).
        SeedTurnLog(new[]
        {
            new TurnReportEntry("events", TurnReportKinds.Calendar, "season", "1", SectorId: null)
        });

        Assert.True(Has(await EntriesFor("dave"), "1"));
        Assert.True(Has(await EntriesFor("zomboss"), "1"));
    }

    [Fact]
    public async Task Rule_2_a_battle_on_ground_currently_watched_is_shown()
    {
        // d-home is Dave's own capital — live-watched every turn, the contrasting case that proves
        // rule 2 is reachable at all, not just rule 3's fallback.
        SeedTurnLog(new[]
        {
            new TurnReportEntry("battles", TurnReportKinds.Battle, "b1", "sector:d-home:e-dave", SectorId: "d-home")
        });

        Assert.True(Has(await EntriesFor("dave"), "sector:d-home:e-dave"));
    }

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Same tuning bootstrap `WorldSectorProjectionTests` needs — see its own doc comment.</summary>
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
        _tuningConfigured = true;
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not find repo root above " + AppContext.BaseDirectory);
    }
}
