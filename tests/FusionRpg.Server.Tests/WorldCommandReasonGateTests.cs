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
/// world-stage W18 (arbitration §C, §8.3): another commander's own `Reason` is one of the two
/// leak channels §8c.3 closes alongside the fog fix. Gated behind `FUSIONRPG_SIM=1`
/// (`SimFlags.Enabled`), the same developer gate `/api/test/*` already uses. Owner decision on
/// granularity ("drop the whole entry, or just its Reason") was left open with a stated fallback —
/// no answer came in time, so the cheaper reading shipped: the entry stays, only `Reason` is
/// dropped for a foreign commander outside the gate.
/// </summary>
[Trait("VerificationId", "server.sim-flags")]
[Collection("NotificationHub")]
public class WorldCommandReasonGateTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    const string WorldId = "w18-reason-gate";
    const int Turn = 0;

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

        SeedCommand("dave", "c-dave-1", entityId: "e-dave-legion-1", sectorId: null, reason: "reason-dave");
        // d-home is Dave's own capital — he has believed it since world creation, so the
        // pre-existing `VisibleTo(WorldCommand, ...)` rule already shows this entry to him; this
        // test is about whether its `Reason` leaks, not about whether the entry itself is visible.
        SeedCommand("zomboss", "c-zomboss-1", entityId: null, sectorId: "d-home", reason: "reason-zomboss");
        SeedTurnLog();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    void SeedCommand(string commanderId, string commandId, string? entityId, string? sectorId, string reason)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO rpg_world_commands
                (world_id, turn, commander_id, command_id, seq, kind, payload_json, submitted_utc, reason)
            VALUES ($w, $t, $c, $cmd, 0, 'move', $payload, '2026-01-01T00:00:00Z', $reason);
            """;
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$t", Turn);
        cmd.Parameters.AddWithValue("$c", commanderId);
        cmd.Parameters.AddWithValue("$cmd", commandId);
        var payload = JsonSerializer.Serialize(new
        {
            EntityId = entityId, SectorId = sectorId, SlotIndex = (int?)null,
            LanePath = Array.Empty<string>(), Stance = (string?)null
        });
        cmd.Parameters.AddWithValue("$payload", payload);
        cmd.Parameters.AddWithValue("$reason", reason);
        cmd.ExecuteNonQuery();
    }

    void SeedTurnLog()
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO rpg_world_turn_log
                (world_id, turn, state_hash, engine_version, ruleset_version, seed, committed_utc, report_json)
            VALUES ($w, $t, 'test-hash', 1, 1, '7', '2026-01-01T00:00:00Z', '[]');
            """;
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$t", Turn);
        cmd.ExecuteNonQuery();
    }

    async Task<List<JsonElement>> CommandsFor(string faction)
    {
        var response = await _http.GetFromJsonAsync<JsonElement>($"/api/world/{WorldId}/turn/{Turn}?asFaction={faction}");
        return response.GetProperty("commands").EnumerateArray().ToList();
    }

    [Fact]
    public async Task Without_the_dev_gate_a_foreign_commanders_reason_is_dropped_but_the_viewers_own_is_kept()
    {
        Assert.Null(Environment.GetEnvironmentVariable("FUSIONRPG_SIM"));

        var commands = await CommandsFor("dave");
        var own = commands.Single(c => c.GetProperty("commanderId").GetString() == "dave");
        var foreign = commands.Single(c => c.GetProperty("commanderId").GetString() == "zomboss");

        Assert.Equal("reason-dave", own.GetProperty("reason").GetString());
        Assert.True(foreign.GetProperty("reason").ValueKind is JsonValueKind.Null);
    }

    [Fact]
    public async Task With_the_dev_gate_on_the_projection_is_unchanged_both_reasons_reach_the_viewer()
    {
        Environment.SetEnvironmentVariable("FUSIONRPG_SIM", "1");
        try
        {
            var commands = await CommandsFor("dave");
            var own = commands.Single(c => c.GetProperty("commanderId").GetString() == "dave");
            var foreign = commands.Single(c => c.GetProperty("commanderId").GetString() == "zomboss");

            Assert.Equal("reason-dave", own.GetProperty("reason").GetString());
            Assert.Equal("reason-zomboss", foreign.GetProperty("reason").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FUSIONRPG_SIM", null);
        }
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
