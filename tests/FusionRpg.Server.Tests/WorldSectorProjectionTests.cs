using System.Net;
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
/// world-stage W6 (`WorldSectorDto` — pressure, warden, neglect, capacity): proves
/// <c>PressureMilli</c>, <c>WardenBindingId</c>, <c>NeglectedTurns</c> and <c>LoamCapacity</c> reach
/// the wire owner-gated on the exact <c>StabilityMilli</c> pattern (`WorldEndpoints.cs:309-311`),
/// asserted from two viewers over the same world.
///
/// <c>WardenBindingId</c>/<c>NeglectedTurns</c>/<c>PressureMilli</c> are live Core state
/// (`LoamPhases.cs:224,240,266-283`) but nothing in Core drives a fresh `two-hearths` world to a
/// non-default value in the handful of setup steps a unit test can afford — a real "bind a warden" /
/// "let a sector go neglected for N turns" playthrough belongs to the `world-commands` program
/// (not yet built) and to `LoamPhases`' own turn tests, not to this projection test. So this test
/// seeds the exact same persisted columns `LoadWorldState` reads (`RpgStore.World.cs:413-415`)
/// directly via the store's own sqlite file, then exercises the real HTTP projection — proving the
/// gating, not re-proving the contagion math.
/// </summary>
[Collection("NotificationHub")]
public class WorldSectorProjectionTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    const string WorldId = "w6-projection";

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

    /// <summary>
    /// Writes straight to `rpg_world_sectors` — the same columns `RpgStore.LoadWorldState` reads
    /// (`RpgStore.World.cs:413-415`) — so the next `LoadWorldState` call sees these values as genuine
    /// Core-level `WorldSector` state, not a projection-layer double.
    /// </summary>
    void SeedSectorColumns(string sectorId, int pressureMilli, string wardenBindingId, int neglectedTurns)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE rpg_world_sectors
            SET pressure_milli = $p, warden_binding_id = $w, neglected_turns = $n
            WHERE world_id = $world AND sector_id = $sector;
            """;
        cmd.Parameters.AddWithValue("$p", pressureMilli);
        cmd.Parameters.AddWithValue("$w", wardenBindingId);
        cmd.Parameters.AddWithValue("$n", neglectedTurns);
        cmd.Parameters.AddWithValue("$world", WorldId);
        cmd.Parameters.AddWithValue("$sector", sectorId);
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows); // fixture row must exist — a 0 here means the sector id is wrong
    }

    /// <summary>world-map W46: same seeding shape as <see cref="SeedSectorColumns"/>, one level up
    /// for the growth/project columns nothing in Core drives to a non-default value yet either.</summary>
    void SeedGrowthColumns(string sectorId, long recruitStock, string projectId, int projectTurnsRemaining)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE rpg_world_sectors
            SET recruit_stock = $r, project_id = $p, project_turns_remaining = $t
            WHERE world_id = $world AND sector_id = $sector;
            """;
        cmd.Parameters.AddWithValue("$r", recruitStock);
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$t", projectTurnsRemaining);
        cmd.Parameters.AddWithValue("$world", WorldId);
        cmd.Parameters.AddWithValue("$sector", sectorId);
        var rows = cmd.ExecuteNonQuery();
        Assert.Equal(1, rows);
    }

    async Task<JsonElement> StateFor(string faction) =>
        await _http.GetFromJsonAsync<JsonElement>($"/api/world/{WorldId}/state?asFaction={faction}");

    static JsonElement Sector(JsonElement state, string id) =>
        state.GetProperty("sectors").EnumerateArray().Single(s => s.GetProperty("sectorId").GetString() == id);

    [Fact]
    public async Task Pressure_warden_and_neglect_reach_the_owner_and_only_the_owner()
    {
        // d-home is Dave's capital in the two-hearths template — WorldLoamWireTests.cs already
        // relies on the same fact.
        SeedSectorColumns("d-home", pressureMilli: 456, wardenBindingId: "warden.ember-warden", neglectedTurns: 5);

        var owner = Sector(await StateFor("dave"), "d-home");
        Assert.Equal(456, owner.GetProperty("pressureMilli").GetInt32());
        Assert.Equal("warden.ember-warden", owner.GetProperty("wardenBindingId").GetString());
        Assert.Equal(5, owner.GetProperty("neglectedTurns").GetInt32());

        var nonOwner = Sector(await StateFor("zomboss"), "d-home");
        Assert.Equal(0, nonOwner.GetProperty("pressureMilli").GetInt32());
        Assert.True(nonOwner.GetProperty("wardenBindingId").ValueKind is JsonValueKind.Null);
        Assert.Equal(0, nonOwner.GetProperty("neglectedTurns").GetInt32());
    }

    /// <summary>world-map W46 acceptance: recruit stock and project progress reach the owner and only the owner.</summary>
    [Fact]
    public async Task Recruit_stock_and_project_progress_reach_the_owner_and_only_the_owner()
    {
        SeedGrowthColumns("d-home", recruitStock: 340, projectId: "placeholder-project", projectTurnsRemaining: 2);

        var owner = Sector(await StateFor("dave"), "d-home");
        Assert.Equal(340, owner.GetProperty("recruitStock").GetInt64());
        Assert.Equal("placeholder-project", owner.GetProperty("projectId").GetString());
        Assert.Equal(2, owner.GetProperty("projectTurnsRemaining").GetInt32());

        var nonOwner = Sector(await StateFor("zomboss"), "d-home");
        Assert.Equal(0, nonOwner.GetProperty("recruitStock").GetInt64());
        Assert.True(nonOwner.GetProperty("projectId").ValueKind is JsonValueKind.Null);
        Assert.True(nonOwner.GetProperty("projectTurnsRemaining").ValueKind is JsonValueKind.Null);
    }

    [Fact]
    public async Task Loam_capacity_denominates_the_owners_own_stock_and_is_zero_for_everyone_else()
    {
        // No raw-SQL seed needed: `LoamPolicy.LoamCapacity` is the sector's base capacity from
        // creation — a fresh, unmodified sector already carries a real, positive denominator.
        var owner = Sector(await StateFor("dave"), "d-home");
        var capacity = owner.GetProperty("loamCapacity").GetInt64();
        Assert.True(capacity > 0, "a freshly created sector must already carry a positive base loam capacity");
        Assert.True(owner.GetProperty("loamStock").GetInt64() <= capacity);

        var nonOwner = Sector(await StateFor("zomboss"), "d-home");
        Assert.Equal(0, nonOwner.GetProperty("loamCapacity").GetInt64());
    }

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static bool _tuningConfigured;

    /// <summary>
    /// `WorldTemplateCatalog.TwoHearths` reads `LoamPolicy.LoamCapacity` at build time
    /// (`WorldTemplateCatalog.TwoHearths.cs:17`), which throws unless configured first — this
    /// assembly has no module-initializer bootstrap (unlike Core/Data/E2E.Tests), so
    /// `AptitudeChannelModsTests.cs`'s own inline pattern is repeated here rather than invented.
    /// Configuring twice across test classes in the same process is harmless (same repo file, same
    /// values each time) — guarded only to avoid redundant file reads within this class's own runs.
    /// </summary>
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
