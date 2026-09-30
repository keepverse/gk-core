using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.Creatures;
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
/// world-stage W9 (re-homed from `world-targeting`): a lane's march cost for a named legion is
/// projected server-side via `?forLegion=`, empty when no legion is named, and fog-honest — priced
/// against the viewer's *believed* climate, never truth, so an un-scouted ley discount does not
/// silently apply.
/// </summary>
[Collection(SpeciesCatalogSwapCollection.Name)]
public class WorldMarchCostProjectionTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    const string WorldId = "w9-projection";
    const string LegionId = "e-dave-legion-1";

    public async Task InitializeAsync()
    {
        ConfigureWorldTuningOnce();

        // solid-remediation 2026-09-17: establish the species catalog this test's premise depends on,
        // rather than inheriting whatever ran last. `BannerElement.Of` SKIPS members whose species the
        // catalog does not know, and this legion's three members (peashooterzombie / conezombie /
        // paperzombie) exist only in the COMPILED default — they are absent from the imported roster,
        // verified against every file in gk-data/packs/fusion/data/generated/creatures/. So with the imported catalog
        // installed the banner stops being Ice, d-flank-1's believed Earth matches instead, and the ley
        // discount applies: 576 rather than 720.
        //
        // `DelveRoomEncounterTests` installs the imported snapshot in its own constructor (it needs the
        // real roster, correctly), and `CreatureSpeciesCatalog` is process-wide. Sharing a collection
        // with it stops the two interleaving; this line is what makes THIS class's premise true no
        // matter which order they run in. A reader that depends on ambient global state is a test that
        // passes for reasons it never states.
        CreatureSpeciesCatalog.ConfigureFromCompiledDefault();

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

    SqliteConnection OpenHot()
    {
        return SqliteConnectionFactory.Open(_store.HotPath);
    }

    void Exec(string sql, params (string Name, object Value)[] parameters)
    {
        using var db = OpenHot();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    async Task<JsonElement> StateAsync(string? forLegion = null)
    {
        var url = $"/api/world/{WorldId}/state?asFaction=dave";
        if (forLegion is not null) url += $"&forLegion={forLegion}";
        return await _http.GetFromJsonAsync<JsonElement>(url);
    }

    [Fact]
    public async Task March_costs_are_empty_when_no_legion_is_selected()
    {
        var state = await StateAsync();
        Assert.Empty(state.GetProperty("marchCosts").EnumerateObject());
    }

    [Fact]
    public async Task A_selected_legions_march_cost_is_real_lane_cost_math()
    {
        // l-df1-df2: corridor, length 800, no hazard -> 800 * 700‰ = 560 (gk-core/data/tuning/world.v5.json's
        // corridor multiplier), a plain check that the projection isn't a placeholder.
        var state = await StateAsync(LegionId);
        var costs = state.GetProperty("marchCosts");
        Assert.Equal(560, costs.GetProperty("l-df1-df2").GetInt32());
    }

    [Fact]
    public async Task An_unscouted_leys_discount_does_not_apply_priced_against_belief_not_truth()
    {
        // e-dave-legion-1's banner is Ice (peashooterzombie=Earth, conezombie=Ice, paperzombie=Light,
        // all singletons -> first in ElementTypeId's own declared order wins: Ice).
        // Make l-dh-df1 a ley lane, and give d-flank-1's TRUTH climate a matching Ice — if the
        // projection read truth, the ley discount would apply (576). Its BELIEVED climate (Dave
        // scouted it before this) stays Earth, so a fog-honest reading must NOT discount (720).
        Exec("UPDATE rpg_world_lanes SET type_id = 'ley' WHERE world_id = $w AND lane_id = 'l-dh-df1';", ("$w", WorldId));
        Exec("UPDATE rpg_world_sectors SET climate = 'Ice' WHERE world_id = $w AND sector_id = 'd-flank-1';", ("$w", WorldId));

        // solid-remediation 2026-09-17: this test has failed intermittently at 576 (= 800 x 900permille
        // ley x 800permille ley discount), meaning the discount APPLIED and therefore the BELIEVED
        // climate read as Ice. Everything after this point is only meaningful if the premise above it
        // holds, so the premise is now asserted rather than assumed: if belief has already moved to Ice,
        // say THAT, instead of failing on a cost number that sends the next reader to the tuning file.
        //
        // Ruled out as causes before adding this, so they are not re-investigated: world tuning version
        // (every class loads world.v6.json), the ley multiplier (900 in both, and LeyDiscountMilli is a
        // code const), an unconfigured species catalog (PowerAndAptitudeTuningTestBootstrap is a
        // [ModuleInitializer], so it is configured at assembly load), and cross-class database state
        // (each test builds its own in-memory store).
        // The premise is about BOTH ENDS, which the first version of this check missed. `LaneCost.For`
        // discounts when `climateOf(FromSectorId) == banner || climateOf(ToSectorId) == banner`
        // (LaneCost.cs:142-143), and `l-dh-df1` joins `d-hearth` to `d-flank-1`. Controlling one end and
        // asserting a rule that reads two is how this test could fail with its own stated premise intact
        // — which is exactly what was observed: the d-flank-1-only check PASSED on a failing run.
        var beliefBefore = await StateAsync(LegionId);
        var believedSectors = beliefBefore.GetProperty("sectors").EnumerateArray().ToList();
        Assert.NotEmpty(believedSectors);

        string? BelievedClimate(string sectorId)
        {
            var row = believedSectors.FirstOrDefault(
                x => x.GetProperty("sectorId").GetString() == sectorId);
            return row.ValueKind == JsonValueKind.Object && row.TryGetProperty("climate", out var c)
                ? c.GetString()
                : null;
        }

        foreach (var end in new[] { "d-hearth", "d-flank-1" })
        {
            Assert.True(BelievedClimate(end) != "Ice",
                $"PRECONDITION FAILED, not the assertion under test: lane l-dh-df1's endpoint '{end}' has "
                + "a BELIEVED climate of Ice, which matches the legion's Ice banner, so LaneCost applies "
                + "the ley discount legitimately and 576 is correct arithmetic on a premise this test "
                + "never established. The rule reads EITHER end; pinning only d-flank-1 does not pin the "
                + "discount. Fix the fixture to control both ends, rather than looking at LaneCost.");
        }

        var state = await StateAsync(LegionId);
        var costs = state.GetProperty("marchCosts");
        // Length 800 * ley 900‰ = 720, no discount applied.
        Assert.Equal(720, costs.GetProperty("l-dh-df1").GetInt32());
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
