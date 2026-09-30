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
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// world-stage W8 (`WorldEntityDto` — carried loam, member role, supply, legion display name):
/// proves `CarriedLoam`, member `Role`, the supply block (`Capacity`/`Burn`/`Runway`) and
/// `DisplayName` all reach a client, using the `two-hearths` fixture's own legion — which already
/// carries real, non-default values (`CarriedLoam = 500`, one `Bearer` among three members) — so no
/// raw-SQL seeding is needed for this one, unlike W6/W7's dormant fields.
/// </summary>
[Collection("NotificationHub")]
public class WorldEntityProjectionTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    const string WorldId = "w8-projection";

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

    async Task<JsonElement> Legion(string faction, string entityId)
    {
        var state = await _http.GetFromJsonAsync<JsonElement>($"/api/world/{WorldId}/state?asFaction={faction}");
        return state.GetProperty("entities").EnumerateArray().Single(e => e.GetProperty("entityId").GetString() == entityId);
    }

    [Fact]
    public async Task Carried_loam_and_display_name_reach_the_wire()
    {
        var legion = await Legion("dave", "e-dave-legion-1");
        Assert.Equal(500, legion.GetProperty("carriedLoam").GetInt64());
        // Dave's only legion — the ordinal rule (world-stage W8, EntityNamingTests.cs) gives it I.
        Assert.Equal("Legion I", legion.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Supply_block_reflects_the_real_bearer_and_burn_math()
    {
        var legion = await Legion("dave", "e-dave-legion-1");

        // e-dave-legion-1: 3 members, exactly 1 Bearer (WorldTemplateCatalog.TwoHearths.cs) — proves
        // the projection reads real LegionSupply math, not a placeholder.
        var capacity = legion.GetProperty("capacity").GetInt64();
        var burn = legion.GetProperty("burn").GetInt64();
        Assert.True(capacity > 0, "one bearer must carry a positive capacity");
        Assert.True(burn > 0, "three members must burn a positive amount");

        var carried = legion.GetProperty("carriedLoam").GetInt64();
        var expectedRunway = (int)(Math.Max(0, carried) / burn);
        Assert.Equal(expectedRunway, legion.GetProperty("runway").GetInt32());
    }

    [Fact]
    public async Task Member_role_reaches_the_wire()
    {
        var legion = await Legion("dave", "e-dave-legion-1");
        var members = legion.GetProperty("members").EnumerateArray().ToList();

        Assert.Contains(members, m => m.GetProperty("speciesId").GetString() == "paperzombie" && m.GetProperty("role").GetString() == "Bearer");
        Assert.Contains(members, m => m.GetProperty("speciesId").GetString() == "peashooterzombie" && m.GetProperty("role").GetString() == "Fighter");
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
