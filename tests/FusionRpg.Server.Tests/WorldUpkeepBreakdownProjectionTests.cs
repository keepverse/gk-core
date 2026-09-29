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
using FusionRpg.Server.Tests.Notifications;

namespace FusionRpg.Server.Tests;

/// <summary>
/// world-stage W10 (`LoamUpkeep` operand breakdown, re-homed from `world-numbers`): proves
/// `WorldSectorDto.UpkeepBreakdown` reaches the wire, its operands recombine to exactly
/// `LoamUpkeep`, and it is owner-gated the same way `LoamUpkeep` itself already is.
/// </summary>
[Collection("NotificationHub")]
public class WorldUpkeepBreakdownProjectionTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    const string WorldId = "w10-projection";

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

    async Task<JsonElement> Sector(string faction, string sectorId)
    {
        var state = await _http.GetFromJsonAsync<JsonElement>($"/api/world/{WorldId}/state?asFaction={faction}");
        return state.GetProperty("sectors").EnumerateArray().Single(s => s.GetProperty("sectorId").GetString() == sectorId);
    }

    [Fact]
    public async Task The_owners_breakdown_operands_recombine_to_exactly_loam_upkeep()
    {
        var state = await _http.GetFromJsonAsync<JsonElement>($"/api/world/{WorldId}/state?asFaction=dave");
        var sector = state.GetProperty("sectors").EnumerateArray().Single(s => s.GetProperty("sectorId").GetString() == "d-home");
        var breakdown = sector.GetProperty("upkeepBreakdown");

        // wonder-wire §Design 3 (plan Task 4A.4): five additive terms through the four-factor
        // Total — Sum × Intensity × Handicap × Season / 1_000_000_000. SeasonMilli rides no DTO
        // field (Ask-first adjacent staleness, flagged in the wire), so the test reads it the way
        // the engine does: calendar season off this same response + the shipped world tuning row.
        var sum = breakdown.GetProperty("base").GetInt64()
                  + breakdown.GetProperty("garrison").GetInt64()
                  + breakdown.GetProperty("development").GetInt64()
                  + breakdown.GetProperty("danger").GetInt64()
                  + breakdown.GetProperty("wonderUpkeep").GetInt64();
        var intensity = breakdown.GetProperty("intensityMilli").GetInt32();
        var handicap = breakdown.GetProperty("handicapMilli").GetInt32();
        var season = state.GetProperty("calendar").GetProperty("season").GetInt32();
        var seasonMilli = FusionRpg.Core.World.WorldTuningHub.Tuning.Seasons.UpkeepMilli[season];
        var recombined = checked(sum * intensity * handicap * seasonMilli / 1_000_000_000);

        Assert.Equal(sector.GetProperty("loamUpkeep").GetInt64(), recombined);
        Assert.True(recombined > 0, "d-home carries a garrison and a below-baseline fracture intensity, so its upkeep must be non-zero");
    }

    [Fact]
    public async Task The_owners_breakdown_matches_the_real_tuning_and_garrison_math()
    {
        // d-home: DevelopmentLevel 0, DangerBand 0, FractureIntensityMilli 500, handicap 1000
        // (defaults), garrisoned by e-dave-legion-1's 3 members.
        var sector = await Sector("dave", "d-home");
        var breakdown = sector.GetProperty("upkeepBreakdown");

        Assert.Equal(10, breakdown.GetProperty("base").GetInt64());      // baseUpkeepPerSector
        Assert.Equal(6, breakdown.GetProperty("garrison").GetInt64());   // 3 members * 2 per member
        Assert.Equal(0, breakdown.GetProperty("development").GetInt64());
        Assert.Equal(0, breakdown.GetProperty("danger").GetInt64());
        // wonder-wire §Design 3: two-hearths ships no Wonder, so the fifth operand reads 0 here —
        // the nonzero-operand proof rides WorldWonderWireTests (temp-corpus Empire wonder).
        Assert.Equal(0, breakdown.GetProperty("wonderUpkeep").GetInt64());
        Assert.Equal(500, breakdown.GetProperty("intensityMilli").GetInt32());
        Assert.Equal(1000, breakdown.GetProperty("handicapMilli").GetInt32());
        Assert.Equal(8, sector.GetProperty("loamUpkeep").GetInt64()); // (10+6+0) * 500 * 1000 * 1000 / 1_000_000_000 (season 0 = 1000)
    }

    [Fact]
    public async Task A_non_owners_breakdown_is_every_field_zero()
    {
        var sector = await Sector("zomboss", "d-home");
        var breakdown = sector.GetProperty("upkeepBreakdown");

        Assert.Equal(0, breakdown.GetProperty("base").GetInt64());
        Assert.Equal(0, breakdown.GetProperty("garrison").GetInt64());
        Assert.Equal(0, breakdown.GetProperty("development").GetInt64());
        Assert.Equal(0, breakdown.GetProperty("danger").GetInt64());
        Assert.Equal(0, breakdown.GetProperty("wonderUpkeep").GetInt64());
        Assert.Equal(0, sector.GetProperty("loamUpkeep").GetInt64());
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
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not find repo root above " + AppContext.BaseDirectory);
    }
}
