using FusionRpg.Core.Delve;
using FusionRpg.Core.Delve.Attrition;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using FusionRpg.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>D5.2 (spec-delve-stage.md §5, §18 asks 1-2) — <c>GET /api/delve/{delveId}</c> and the
/// <c>DelveUpdated</c> broadcast helper. Calls the extracted, `internal` handler method directly
/// (this project's own pre-existing `InternalsVisibleTo`), matching
/// <c>DelveDomainsAndStartEndpointsTests.cs</c>'s own established reason: the routing lambda in
/// `DelveEndpoints.cs` is one line, DI binding only. Seeds a real delve via `_store.CreateDelve(...)`
/// directly, matching `DelveWildEndpointsTests.cs`'s own `CreateDelve()` fixture helper — the only way
/// to get a real `rpg_delves`/`rpg_delve_rooms` row today, since `POST /start` never reaches
/// `CreateDelve` in production while `dungeon_domain` stays empty (D4.22's own already-recorded
/// finding).</summary>
public class DelveProjectionEndpointTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;
    readonly RoomTypeCatalog _rooms;
    readonly DoorTypeCatalog _doors;
    int _worldSeq;

    public DelveProjectionEndpointTests()
    {
        ConfigureDungeonTuningOnce();
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();

        var repoRoot = FindRepoRoot();
        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(KeepverseRoots.Content(), "data", "seed", "dungeon", "_registry"));
        _rooms = new RoomTypeCatalog(registries.RoomKinds);
        _doors = new DoorTypeCatalog(registries.DoorKinds);
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    // This assembly's own [ModuleInitializer] (PowerAndAptitudeTuningTestBootstrap.cs) covers
    // Power/Aptitude/DerivedStat/Rung/Aura/Items/CreatureSpeciesCatalog only -- DungeonTuningHub is
    // NOT among them (confirmed by reading that file directly), and DelveEndpoints.HandleGetDelve
    // reads it via DungeonTuningHub.Tuning. Configured here from the REAL shipped
    // gk-core/data/tuning/dungeon.v3.json, matching DungeonHubTestBootstrap's own established convention in
    // FusionRpg.Core.Tests ("a fixture copy could drift from what ships") and
    // DelveWildEndpointsTests.cs's own ConfigureWildTuningOnce idiom for the identical situation.
    static bool _dungeonTuningConfigured;
    static void ConfigureDungeonTuningOnce()
    {
        if (_dungeonTuningConfigured) return;
        var repoRoot = FindRepoRoot();
        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(KeepverseRoots.Content(), "data", "seed", "dungeon", "_registry"));
        DungeonTuningHub.Configure(DungeonTuningLoader.Parse(
            File.ReadAllText(Path.Combine(repoRoot, "data", "tuning", "dungeon.v3.json")), registries));
        _dungeonTuningConfigured = true;
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }

    // ---- fixtures (mirrors DelveWildEndpointsTests.cs's own BuildGraph/BuildRooms/CreateDelve) -----

    const ulong Seed = 11UL;

    WorldState BuildGraph(string worldId, string partyEntityId) => new()
    {
        WorldId = worldId, TemplateId = "layout.short-narrow-linear-001", Seed = Seed, CurrentTurn = 0,
        Factions = new[] { new WorldFaction { FactionId = _playerId.ToString(), Kind = WorldFactionKind.Player, Name = "Player" } },
        Sectors = new[]
        {
            new WorldSector { SectorId = "r0c0", TypeId = "fight" },
            new WorldSector { SectorId = "r0c1", TypeId = "elite" },
        },
        Lanes = new[] { new WorldLane { LaneId = "l01", FromSectorId = "r0c0", ToSectorId = "r0c1", TypeId = "passage" } },
        Entities = new[]
        {
            new WorldEntity { EntityId = partyEntityId, Kind = WorldEntityKind.Warband, OwnerFactionId = _playerId.ToString(), AtSectorId = "r0c0" },
        },
    };

    static IReadOnlyList<DelveRoomRow> BuildRooms() => new[]
    {
        new DelveRoomRow("r0c0", 0, 0, "fight", "room.fight-none-001", true, false, null, null, null, null, "[]", 0),
        new DelveRoomRow("r0c1", 0, 1, "elite", "room.elite-none-001", false, false, null, null, null, null, "[]", 0),
    };

    long CreateDelve(string partyEntityId = "1")
    {
        var worldId = $"delve-projection-ep-{Interlocked.Increment(ref _worldSeq)}";
        var (ok, _, delve) = _store.CreateDelve(
            _playerId, "domain.fire-shallow-001", "solo", "hard", "corr-" + worldId, null,
            worldId, "layout.short-narrow-linear-001", Seed, BuildGraph(worldId, partyEntityId), BuildRooms(), _rooms, _doors);
        Assert.True(ok);
        return delve!.DelveId;
    }

    static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? -1;

    // ASP.NET's minimal-API pipeline serializes every Results.Ok(...) payload with a camelCase naming
    // policy applied recursively, including nested record types (confirmed: DomainOfferDto -- a plain
    // PascalCase record, HandleGetDomains's own real precedent -- renders as camelCase JSON in
    // production). System.Text.Json.JsonSerializer.Serialize's own DEFAULT options do NOT apply that
    // policy, so a raw call here would only match the outer anonymous object's own already-lowercase
    // member names and miss every nested DelveProjectionRoom/Door/PartyPosition property -- this
    // mirrors the real pipeline's options explicitly rather than silently testing a different shape.
    static readonly System.Text.Json.JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    // ==============================================================================================
    // GET /api/delve/{delveId}
    // ==============================================================================================

    [Fact]
    public void GetDelve_for_an_unknown_player_404s()
    {
        var result = DelveEndpoints.HandleGetDelve(1, playerId: 999_999, _store);
        Assert.IsType<NotFound>(result);
    }

    [Fact]
    public void GetDelve_for_an_unknown_delve_404s()
    {
        var result = DelveEndpoints.HandleGetDelve(delveId: 999_999, playerId: _playerId, _store);
        Assert.Equal(404, StatusOf(result));
    }

    [Fact]
    public void GetDelve_owned_by_a_different_player_404s_never_confirms_existence()
    {
        var delveId = CreateDelve();
        // A second REAL player (PlayerExists must pass) so this 404 is proven to come from the
        // ownership check inside DelveProjection.For, not from the earlier "unknown player" guard.
        var second = _store.CreatePlayer("second");

        var result = DelveEndpoints.HandleGetDelve(delveId, playerId: second.Id, _store);
        Assert.Equal(404, StatusOf(result));
    }

    [Fact]
    public void GetDelve_defaults_playerId_to_GetCurrentPlayerId_when_omitted()
    {
        var delveId = CreateDelve();

        var result = DelveEndpoints.HandleGetDelve(delveId, playerId: null, _store);

        var ok = Assert.IsAssignableFrom<IValueHttpResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void GetDelve_returns_a_revision_stamped_projection_with_the_delves_own_scalar_fields()
    {
        var delveId = CreateDelve();

        var result = DelveEndpoints.HandleGetDelve(delveId, _playerId, _store);

        var ok = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value, CamelCase);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(delveId, root.GetProperty("delveId").GetInt64());
        Assert.Equal("domain.fire-shallow-001", root.GetProperty("domainId").GetString());
        Assert.Equal("Active", root.GetProperty("state").GetString());
        Assert.True(root.GetProperty("revision").GetInt64() >= 0);
        Assert.Equal(2, root.GetProperty("rooms").GetArrayLength());
        Assert.Equal(1, root.GetProperty("doors").GetArrayLength());
    }

    [Fact]
    public void GetDelve_hides_an_unlit_rooms_kind_but_still_lists_the_room()
    {
        // BuildGraph always seeds a WorldEntity for CreateDelve's own WorldValidation to accept (a
        // real delve world cannot be empty) -- so r0c0, where that entity stands, is legitimately Full
        // via Visibility.SeenBy's own floor (entity ownership drives the floor directly, independent
        // of parties_json/delve.Parties, which stays empty here). r0c1, one lane further, is the room
        // that proves the redaction: still LISTED, contents hidden.
        var delveId = CreateDelve();

        var result = DelveEndpoints.HandleGetDelve(delveId, _playerId, _store);
        var ok = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value, CamelCase);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var rooms = doc.RootElement.GetProperty("rooms");
        Assert.Equal(2, rooms.GetArrayLength());
        var r0c1 = rooms.EnumerateArray().Single(r => r.GetProperty("sectorId").GetString() == "r0c1");
        // No global JsonStringEnumConverter is configured anywhere in Program.cs (confirmed by direct
        // search) -- SectorSight serializes as its raw int today (Glimpse = 1). D5.3's own
        // contract/adapter layer (a later, separate task) is where a stable wire id would land.
        Assert.Equal((int)SectorSight.Glimpse, r0c1.GetProperty("sight").GetInt32());
        Assert.Equal("elite", r0c1.GetProperty("kind").GetString()); // a glimpse still names the kind
        Assert.Equal(System.Text.Json.JsonValueKind.Null, r0c1.GetProperty("archetypeId").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, r0c1.GetProperty("floorJson").ValueKind);
    }

    [Fact]
    public void GetDelve_reveals_the_wired_partys_own_room_end_to_end()
    {
        // The full pipeline, store through Core: a party entity in the WorldState AND a matching
        // parties_json entry (WritePartyMembers) together are what today's real production gap is
        // missing (named in DelveProjection's own doc comment) -- wiring both by hand here proves the
        // REST of the pipeline (the store reads, the endpoint's own projection call, JSON shape) is
        // correct once that gap is closed by whichever task closes it.
        var delveId = CreateDelve(partyEntityId: "42");
        _store.WritePartyMembers(delveId, 42, Array.Empty<DelveMemberState>());

        var result = DelveEndpoints.HandleGetDelve(delveId, _playerId, _store);
        var ok = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value, CamelCase);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var rooms = doc.RootElement.GetProperty("rooms");
        var r0c0 = rooms.EnumerateArray().Single(r => r.GetProperty("sectorId").GetString() == "r0c0");
        Assert.Equal((int)SectorSight.Full, r0c0.GetProperty("sight").GetInt32());
        Assert.Equal("fight", r0c0.GetProperty("kind").GetString());

        var parties = doc.RootElement.GetProperty("partyPositions");
        var pos = parties.EnumerateArray().Single(p => p.GetProperty("entityId").GetInt64() == 42);
        Assert.Equal("r0c0", pos.GetProperty("atSectorId").GetString());
    }

    // ==============================================================================================
    // NotifyDelveUpdatedAsync — no production caller wires it yet (see the method's own doc comment);
    // proven real and callable directly against a genuine IHubContext<RpgHub>, matching
    // ZombossAdaptiveSeamTests.cs's own established technique for testing hub-touching code with no
    // connected clients to assert wire payloads against.
    // ==============================================================================================

    [Fact]
    public async Task NotifyDelveUpdatedAsync_runs_against_a_real_hub_context_without_throwing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        var provider = services.BuildServiceProvider();
        var hub = provider.GetRequiredService<IHubContext<RpgHub>>();

        await DelveEndpoints.NotifyDelveUpdatedAsync(hub, delveId: 7, revision: 3);
        // No connected clients -- SendAsync completing without throwing is the whole contract this
        // method's own try/catch guarantees (matching NotifyAsync's own best-effort shape).
    }
}
