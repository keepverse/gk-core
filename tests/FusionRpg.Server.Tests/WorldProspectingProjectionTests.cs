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
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// world-stage W16: `WorldStateDto.ProspectedSectorIds` projects `Prospecting.Reveal` — separate
/// from `Sectors[].Intel`, never merged into it, and empty (inert, not broken) whenever no dowser
/// is out.
/// </summary>
[Collection("NotificationHub")]
public class WorldProspectingProjectionTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    const string WorldId = "w16-prospecting";

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
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    void SetStance(string entityId, string stance)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE rpg_world_entities SET stance = $s WHERE world_id = $w AND entity_id = $e;";
        cmd.Parameters.AddWithValue("$s", stance);
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    async Task<JsonElement> State() =>
        await _http.GetFromJsonAsync<JsonElement>($"/api/world/{WorldId}/state?asFaction=dave");

    [Fact]
    public async Task With_no_dowser_the_set_is_empty_and_the_response_shape_is_unchanged()
    {
        var state = await State();
        Assert.Empty(state.GetProperty("prospectedSectorIds").EnumerateArray());
        // The rest of the shape is untouched — sectors still project normally.
        Assert.True(state.GetProperty("sectors").GetArrayLength() > 0);
    }

    [Fact]
    public async Task A_dowser_reveals_its_own_sector_without_changing_that_sectors_intel()
    {
        // e-dave-legion-1 stands at d-home, which carries a rootbed (a real loam source) — the
        // dowser's own ground always qualifies for its own reveal.
        SetStance("e-dave-legion-1", "dowse");

        var state = await State();
        var prospected = state.GetProperty("prospectedSectorIds").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("d-home", prospected);

        // The acceptance's own core guarantee: prospecting must never promote a sector's `intel`.
        // d-home is already Watched (Dave's own capital) either way — the point is that the field
        // exists and is untouched by this projection, not that dowsing itself downgrades anything.
        var dHome = state.GetProperty("sectors").EnumerateArray()
            .Single(s => s.GetProperty("sectorId").GetString() == "d-home");
        Assert.Equal("Watched", dHome.GetProperty("intel").GetString());
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
        return KeepverseRoots.Core();
    }
}
