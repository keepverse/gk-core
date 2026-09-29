using System.Net.Http.Json;
using System.Text.Json;
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

namespace FusionRpg.Server.Tests;

// Task 4A.1 (`cargo-commands`, empire-inventory-surfaces module 1): the wire half —
// `WorldCommandRequest`'s seven cargo/cache fields through the real HTTP mapping
// (`WorldEndpoints.cs`), the store log, and the commit pass (the assault wire-test pattern:
// self-hosted app over a warm in-memory store). One kind end to end (`unload-cargo`);
// every other kind shares the same field-for-field mapping lines and is covered store-side
// by `CargoCommandResolveTests`.
[Collection("NotificationHub")]
public class CargoCommandWireTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    const string WorldId = "w-cargo-wire";

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

    void SeedCargoRow()
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_world_entity_cargo
              (world_id, entity_id, seq, kind, instance_id, container_id, qty, weight_each)
            VALUES ($w, $e, 0, 'stack', NULL, 'cont-wire', 2, 10);
            """;
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$e", "e-dave-legion-1");
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    [Fact]
    public async Task Unload_cargo_files_through_post_commands_and_resolves_on_commit()
    {
        SeedCargoRow();

        var filed = await _http.PostAsJsonAsync($"/api/world/{WorldId}/commands", new
        {
            commanderId = "dave",
            commands = new[] { new { commandId = "unload-wire", kind = "unload-cargo", entityId = "e-dave-legion-1", seq = 0 } },
        });
        Assert.True(filed.IsSuccessStatusCode, await filed.Content.ReadAsStringAsync());
        var filedResult = await filed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(filedResult.GetProperty("results")[0].GetProperty("ok").GetBoolean(),
            filedResult.GetProperty("results")[0].ToString());

        // The mapping carried `seq` into the store log: read back the rehydrated command,
        // not the echo.
        var logged = _store.ListWorldCommands(WorldId, 0).Single(c => c.CommandId == "unload-wire");
        Assert.Equal("unload-cargo", logged.Kind);
        Assert.Equal(0, logged.Seq);

        var commit = await _http.PostAsJsonAsync($"/api/world/{WorldId}/commit", new { commanderId = "dave", turn = 0 });
        Assert.True(commit.IsSuccessStatusCode, await commit.Content.ReadAsStringAsync());
        var commitResult = await commit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(commitResult.GetProperty("advanced").GetBoolean(), commitResult.ToString());

        Assert.Empty(_store.ListCargo(WorldId, "e-dave-legion-1"));
        Assert.Contains(_store.GetWorldTurnReport(WorldId, 0)!.Entries, e =>
            e.Kind == TurnReportKinds.Event && e.Detail == "cargo.unloaded:0");
    }

    [Fact]
    public async Task Malformed_load_cargo_is_refused_at_submit_with_its_named_reason()
    {
        // No `cargoKind`: admission's load arm refuses `cargo.kind-unknown` — through HTTP.
        var filed = await _http.PostAsJsonAsync($"/api/world/{WorldId}/commands", new
        {
            commanderId = "dave",
            commands = new[] { new { commandId = "load-bad", kind = "load-cargo", entityId = "e-dave-legion-1" } },
        });
        Assert.True(filed.IsSuccessStatusCode, await filed.Content.ReadAsStringAsync());
        var filedResult = await filed.Content.ReadFromJsonAsync<JsonElement>();
        var first = filedResult.GetProperty("results")[0];
        Assert.False(first.GetProperty("ok").GetBoolean(), first.ToString());
        Assert.Equal("cargo.kind-unknown", first.GetProperty("reason").GetString());
    }
}
