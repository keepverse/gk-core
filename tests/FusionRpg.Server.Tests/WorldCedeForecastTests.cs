using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Data.Sqlite;
using FusionRpg.Server.Tests.Notifications;

namespace FusionRpg.Server.Tests;

/// <summary>
/// world-stage W26 acceptance: a world with a `cede` order filed asserts <c>WillRelease</c> (the
/// `/state` route's `willReleaseNextTurn` flag) and the sector <c>LoamPhases.Pressure</c> actually
/// fades are the **same id** — the whole reason this design has one function (`LoamForecast.Weakest`)
/// instead of two. `ComputeLoamReading` (`WorldEndpoints.cs`) now reads this viewer's own pending
/// order via a dedicated `store.ListWorldCommands` call on the state route itself, rather than reusing
/// `/turn/{turn}`'s `ListLoggedWorldCommands` call, which answers a different question ("what
/// happened") on a different route.
/// </summary>
[Collection("NotificationHub")]
public class WorldCedeForecastTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    const string WorldId = "w26-cede-forecast";

    // Dave's whole starting component in `two-hearths` (spec: WorldTemplateCatalog.TwoHearths.cs) —
    // a triangle (d-home, d-flank-1, d-flank-2) plus d-outpost hanging off d-flank-2 by one lane.
    static readonly string[] DaveSectors = { "d-home", "d-flank-1", "d-flank-2", "d-outpost" };

    public async Task InitializeAsync()
    {
        WorldPolicyTestBootstrap.EnsureConfigured();

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

        // Drain the pool and fragile every candidate up front — a shortfall every turn, and a
        // shortfall large enough to zero whichever one is picked in a single pass (spec-loam-calc.md;
        // full 1000 stability would only ever dim, per LoamForecastTests' own documented ceiling).
        // Development/danger are pushed hard too: d-home and d-flank-1 each carry a rootbed, and
        // their combined production alone (100/turn) would otherwise cover the four sectors' base
        // upkeep (40/turn) and mask the shortfall this test needs on the very first predicted turn.
        //
        // world-map W55 (empire-economy-ssot.md A8) note: `danger_band = 4 + 2 * 20 = 44`, not the
        // original `4` — once `LoamProduction.For` reads `DevelopmentLevel` too, A8 requires its
        // yield rate to exceed its own upkeep rate, so `development_level = 20` alone is now a net
        // *contributor* (120 loam/turn per sector at the real shipped tuning), not a drag. The extra
        // danger compensates exactly (`DevelopmentYieldPerLevel(6) / DangerUpkeepPerBand(3) == 2` at
        // the real configured tuning), reproducing this fixture's original pre-W55 shortfall.
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        foreach (var sectorId in DaveSectors)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                UPDATE rpg_world_sectors
                SET loam_stock = 0, stability_milli = 50, development_level = 20, danger_band = 44
                WHERE world_id = $world AND sector_id = $sector;
                """;
            cmd.Parameters.AddWithValue("$world", WorldId);
            cmd.Parameters.AddWithValue("$sector", sectorId);
            var rows = cmd.ExecuteNonQuery();
            Assert.Equal(1, rows);
        }
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    async Task<JsonElement> StateFor(string faction) =>
        await _http.GetFromJsonAsync<JsonElement>($"/api/world/{WorldId}/state?asFaction={faction}");

    static JsonElement Sector(JsonElement state, string id) =>
        state.GetProperty("sectors").EnumerateArray().Single(s => s.GetProperty("sectorId").GetString() == id);

    static string SingleFlaggedWeakest(JsonElement state)
    {
        var flagged = DaveSectors
            .Where(id => Sector(state, id).GetProperty("willReleaseNextTurn").GetBoolean())
            .ToList();
        return Assert.Single(flagged);
    }

    [Fact]
    public async Task A_filed_cede_order_moves_the_forecast_flag_to_the_same_sector_pressure_actually_releases()
    {
        var before = await StateFor("dave");
        var defaultWeakest = SingleFlaggedWeakest(before);

        // Any other member of the same component is a legal override — unwarded, in-component.
        var alternative = DaveSectors.First(id => id != defaultWeakest);

        var filed = await _http.PostAsJsonAsync($"/api/world/{WorldId}/commands", new
        {
            commanderId = "dave",
            commands = new[] { new { commandId = "cede1", kind = "cede", sectorId = alternative } }
        });
        Assert.True(filed.IsSuccessStatusCode, await filed.Content.ReadAsStringAsync());
        var outcome = await filed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(outcome.GetProperty("results")[0].GetProperty("ok").GetBoolean());

        // The forecast must have moved to "alternative" — proving `/state` now threads the pending
        // order into `LoamForecast.WillRelease` instead of silently repeating the unceded prediction.
        var afterFiling = await StateFor("dave");
        Assert.Equal(alternative, SingleFlaggedWeakest(afterFiling));

        var turn = before.GetProperty("currentTurn").GetInt32();
        var commit = await _http.PostAsJsonAsync($"/api/world/{WorldId}/commit", new { commanderId = "dave", turn });
        Assert.True(commit.IsSuccessStatusCode, await commit.Content.ReadAsStringAsync());
        var commitResult = await commit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(commitResult.GetProperty("advanced").GetBoolean(), "two-hearths' AI factions carry a policy and should auto-fill so dave's own commit steps the turn");

        // Pressure must have actually released exactly the sector the forecast named, not the
        // default pick it would have named with no order filed.
        var after = await StateFor("dave");
        Assert.Equal("Lost", Sector(after, alternative).GetProperty("phase").GetString());
        Assert.True(Sector(after, alternative).GetProperty("ownerFactionId").ValueKind is JsonValueKind.Null);

        Assert.Equal("Held", Sector(after, defaultWeakest).GetProperty("phase").GetString());
        Assert.Equal("dave", Sector(after, defaultWeakest).GetProperty("ownerFactionId").GetString());
    }

    static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

}
