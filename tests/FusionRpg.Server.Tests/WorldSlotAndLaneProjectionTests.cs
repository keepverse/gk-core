using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
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
/// world-stage W7 (`WorldSlotDto`/`WorldLaneDto` — construction, slot owner, gate key): proves
/// <c>WorldSlotDto.OwnerFactionId</c> is projected from truth and owner-gated on the exact
/// `StabilityMilli` pattern with **no** `RememberedSlot` change (spec-world-wire.md §1's "cheap
/// resolution"), and that `ConstructionTurnsRemaining`/`GateKeyId` reach the wire ungated.
/// </summary>
[Collection("NotificationHub")]
public class WorldSlotAndLaneProjectionTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    const string WorldId = "w7-projection";

    public async Task InitializeAsync()
    {
        WorldSectorProjectionTests_ConfigureWorldTuningOnce();

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

    /// <summary>
    /// d-home's three real slots (`WorldTemplateCatalog.TwoHearths.cs:45-47`), as a faction would
    /// remember them from a full survey — mirrors exactly what `IntelRecorder.Observe` (or, after the
    /// W7 fix, `IntelSeed.Snapshot`) would itself produce, so the seed is a realistic belief snapshot
    /// rather than an invented shape.
    /// </summary>
    static List<RememberedSlot> DHomeRememberedSlots(int? rootbedConstructionTurnsRemaining = null) => new()
    {
        new RememberedSlot { SlotIndex = 0, SlotTypeId = "seat", State = SlotState.Claimed, GuardState = GuardState.Cleared },
        new RememberedSlot
        {
            SlotIndex = 1, SlotTypeId = "rootbed", State = SlotState.Claimed, GuardState = GuardState.Cleared,
            ConstructionTurnsRemaining = rootbedConstructionTurnsRemaining
        },
        new RememberedSlot { SlotIndex = 2, SlotTypeId = "market", State = SlotState.Claimed, GuardState = GuardState.Cleared },
    };

    /// <summary>
    /// Writes (or replaces) one faction's `IntelSnapshot` for one sector directly into
    /// `rpg_world_faction_intel` — the exact belief-level state `ProjectSector` reads via
    /// `view.Believed(sectorId)`. Needed because the `two-hearths` template never gives either
    /// faction Full-detail sight of the *other's* ground at creation (each side's `AuthoredIntel`
    /// only covers its own cluster, and the AI faction gets no authored bonus at all —
    /// `IntelSeed.cs:34`), so proving slot-owner gating "from two viewers" has no real fixture path
    /// to exercise without seeding a survey directly, the same way W6 seeded sector-truth columns.
    /// </summary>
    void SeedFullIntel(string factionId, string sectorId, string? ownerFactionId, IReadOnlyList<RememberedSlot> slots)
    {
        using var db = OpenHot();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO rpg_world_faction_intel
                (world_id, faction_id, sector_id, last_seen_turn, detail, owner_faction_id, phase,
                 climate, danger_band, slots_json, forces_json)
            VALUES ($w, $f, $s, 0, 'Full', $owner, 'Held', NULL, 0, $slots, '[]');
            """;
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$f", factionId);
        cmd.Parameters.AddWithValue("$s", sectorId);
        cmd.Parameters.AddWithValue("$owner", (object?)ownerFactionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$slots", JsonSerializer.Serialize(slots));
        cmd.ExecuteNonQuery();
    }

    void SeedLaneGateKey(string laneId, string gateKeyId)
    {
        using var db = OpenHot();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE rpg_world_lanes SET gate_key_id = $k WHERE world_id = $world AND lane_id = $lane;";
        cmd.Parameters.AddWithValue("$k", gateKeyId);
        cmd.Parameters.AddWithValue("$world", WorldId);
        cmd.Parameters.AddWithValue("$lane", laneId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    async Task<JsonElement> StateFor(string faction) =>
        await _http.GetFromJsonAsync<JsonElement>($"/api/world/{WorldId}/state?asFaction={faction}");

    static JsonElement Sector(JsonElement state, string id) =>
        state.GetProperty("sectors").EnumerateArray().Single(s => s.GetProperty("sectorId").GetString() == id);

    static JsonElement Slot(JsonElement sector, int slotIndex) =>
        sector.GetProperty("slots").EnumerateArray().Single(s => s.GetProperty("slotIndex").GetInt32() == slotIndex);

    static JsonElement Lane(JsonElement state, string id) =>
        state.GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneId").GetString() == id);

    [Fact]
    public async Task Slot_owner_is_projected_from_truth_and_owner_gated_from_two_viewers()
    {
        // Give zomboss a full survey of d-home too — the template never does this on its own (see
        // SeedFullIntel's own doc comment) — so both viewers can actually see the slot, and the only
        // difference in what they read is the ownership gate itself, not sight.
        SeedFullIntel("zomboss", "d-home", ownerFactionId: "dave", DHomeRememberedSlots());

        // dave already has Watched sight of his own d-home from world creation (AuthoredIntel) —
        // slot 1's OwnerFactionId is read from live truth (`WorldSlot.OwnerFactionId`), gated by
        // `sector.OwnerFactionId == view.FactionId`, so it comes back "dave" for the owner.
        var owner = Slot(Sector(await StateFor("dave"), "d-home"), 1);
        Assert.Equal("dave", owner.GetProperty("ownerFactionId").GetString());

        // zomboss now sees the same slot (seeded above) but does not own the sector, so the gate
        // resolves to null — proving the read is gated on ownership, not on sight.
        var nonOwner = Slot(Sector(await StateFor("zomboss"), "d-home"), 1);
        Assert.True(nonOwner.GetProperty("ownerFactionId").ValueKind is JsonValueKind.Null);
    }

    [Fact]
    public async Task Construction_turns_remaining_reaches_the_wire_ungated()
    {
        // ConstructionTurnsRemaining flows through belief, not truth (`RememberedSlot`, same as
        // StructureId) — so both viewers need their own seeded survey to prove it is ungated rather
        // than just "the owner's own truth-read happens to work."
        SeedFullIntel("dave", "d-home", ownerFactionId: "dave", DHomeRememberedSlots(rootbedConstructionTurnsRemaining: 3));
        SeedFullIntel("zomboss", "d-home", ownerFactionId: "dave", DHomeRememberedSlots(rootbedConstructionTurnsRemaining: 3));

        var ownerSlot = Slot(Sector(await StateFor("dave"), "d-home"), 1);
        var nonOwnerSlot = Slot(Sector(await StateFor("zomboss"), "d-home"), 1);
        Assert.Equal(3, ownerSlot.GetProperty("constructionTurnsRemaining").GetInt32());
        Assert.Equal(3, nonOwnerSlot.GetProperty("constructionTurnsRemaining").GetInt32());
    }

    [Fact]
    public async Task Gate_key_id_reaches_the_wire()
    {
        SeedLaneGateKey("l-dh-df1", "key.ember-vault");

        var lane = Lane(await StateFor("dave"), "l-dh-df1");
        Assert.Equal("key.ember-vault", lane.GetProperty("gateKeyId").GetString());
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
    static void WorldSectorProjectionTests_ConfigureWorldTuningOnce()
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
