using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.World;
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
/// base-defense siege-seam 7.4 (spec-siege-seam.md): "the five plumbing sites, proven by a round
/// trip" — `WorldCommandKinds` (1), the `WorldCommand` field (2, both already existing: `EntityId`,
/// `SectorId`), `RpgStore.CommandPayload` (3), `WorldCommandRequest` (4), `WorldEndpoints` mapping
/// (5). `bind-warden` failed sites 4 and 5 in its own history (`WorldCommandRequest` never carried
/// its field) — the whole point of testing at THIS layer, through the real wire DTO and the real
/// endpoint, is that <see cref="FusionRpg.Data.Tests"/>'s own `WorldCommandRoundTripPropertyTests`
/// bypasses `WorldCommandRequest` entirely and could not have caught that exact class of bug. This
/// file proves `assault` does not repeat it.
/// </summary>
[Collection("NotificationHub")]
public class DistrictAssaultCommandWireTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    const string WorldId = "w-assault-wire";

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

        var created = await _http.PostAsJsonAsync("/api/test/world/create", new { worldId = WorldId, seed = "1" });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());

        // Test-setup only: place Dave's legion at ash-waste (where the wild pack sits by default in
        // first-light), the same direct-row convention WorldCedeForecastTests already uses for its
        // own fixture setup — the behaviour under test is the HTTP round trip below, not this seed.
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE rpg_world_entities SET at_sector_id = 'ash-waste', on_lane_id = NULL,
                on_lane_toward_sector_id = NULL, lane_progress_milli = 0
            WHERE world_id = $world AND entity_id = 'e-dave-legion-1';
            """;
        cmd.Parameters.AddWithValue("$world", WorldId);
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    [Fact]
    public async Task An_assault_order_survives_the_real_wire_commits_and_fights()
    {
        // Site 4/5: the real WorldCommandRequest DTO and the real WorldEndpoints mapping, over real
        // HTTP -- not a WorldCommand constructed directly in-process.
        var filed = await _http.PostAsJsonAsync($"/api/world/{WorldId}/commands", new
        {
            commanderId = "dave",
            commands = new[] { new { commandId = "assault1", kind = "assault", entityId = "e-dave-legion-1", sectorId = "ash-waste" } }
        });
        Assert.True(filed.IsSuccessStatusCode, await filed.Content.ReadAsStringAsync());
        var filedResult = await filed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(filedResult.GetProperty("results")[0].GetProperty("ok").GetBoolean(),
            filedResult.GetProperty("results")[0].ToString());

        var commit = await _http.PostAsJsonAsync($"/api/world/{WorldId}/commit", new { commanderId = "dave", turn = 0 });
        Assert.True(commit.IsSuccessStatusCode, await commit.Content.ReadAsStringAsync());
        var commitResult = await commit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(commitResult.GetProperty("advanced").GetBoolean(), commitResult.ToString());

        // Read back through the real endpoint too, confirming the route itself answers (the other
        // half of "submit, commit, read back") -- but assert the BATTLE fired against the store's
        // own unfiltered report, not the fog-of-war-projected one: this test placed Dave's legion at
        // ash-waste via a direct row UPDATE (test setup only, the same shortcut
        // WorldCedeForecastTests already uses), which never ran the real march/survey pipeline that
        // would normally raise his belief of ash-waste to `Watched` -- so the endpoint's OWN,
        // correctly-working fog filter (`WorldEndpoints.VisibleTo`) would hide the line from him
        // regardless of whether the assault fired. That is an orthogonal, working-as-designed
        // feature this task is not testing.
        var reportResponse = await _http.GetAsync($"/api/world/{WorldId}/turn/0?asFaction=dave");
        Assert.True(reportResponse.IsSuccessStatusCode, await reportResponse.Content.ReadAsStringAsync());

        var fullReport = _store.GetWorldTurnReport(WorldId, 0);
        Assert.NotNull(fullReport);
        Assert.Contains(fullReport!.Entries, e =>
            e.Kind == FusionRpg.Core.World.Turn.TurnReportKinds.Battle
            && e.Detail.StartsWith("district:ash-waste:", StringComparison.Ordinal));
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
