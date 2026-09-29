using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using FusionRpg.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// The map view renders from a checked-in fixture so the FE has no server dependency (W13). That
/// only stays honest if something notices when the DTO moves, which is this: the fixture must be
/// exactly what `GET /api/world/{id}/state` returns for `first-light`.
///
/// Set `FUSIONRPG_BLESS_WORLD_FIXTURE=1` to rewrite it after a deliberate DTO change.
/// </summary>
[Collection("e2e")]
public class WorldFixtureTests : IAsyncLifetime
{
    const string FixturePath = "web/fusion-rpg-web/src/stages/world/fixtures/first-light.json";
    const string TwoHeartsFixturePath = "web/fusion-rpg-web/src/stages/world/fixtures/two-hearths.json";
    const string BigFixturePath = "web/fusion-rpg-web/src/stages/world/fixtures/eighteen-ten.json";

    readonly RpgApiFactory _factory;
    readonly HttpClient _http;

    public WorldFixtureTests(RpgApiFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync() =>
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_checked_in_world_fixture_still_matches_the_live_dto()
    {
        (await _http.PostAsJsonAsync("/api/test/world/create", new
        {
            worldId = "first-light",
            templateId = "first-light",
            seed = "1"
        })).EnsureSuccessStatusCode();

        var state = await _http.GetFromJsonAsync<JsonElement>("/api/world/first-light/state");
        BlessOrAssert(state, FixturePath);
    }

    /// <summary>
    /// world-stage W21: `two-hearths` is Gate B's playtest world (medium tier) and had no web
    /// fixture — generated the same way as `first-light.json` above, a real templated world, never
    /// hand-written.
    /// </summary>
    [Fact]
    public async Task The_checked_in_two_hearths_fixture_still_matches_the_live_dto()
    {
        (await _http.PostAsJsonAsync("/api/test/world/create", new
        {
            worldId = "two-hearths",
            templateId = "two-hearths",
            seed = "7"
        })).EnsureSuccessStatusCode();

        var state = await _http.GetFromJsonAsync<JsonElement>("/api/world/two-hearths/state");
        BlessOrAssert(state, TwoHeartsFixturePath);
    }

    /// <summary>
    /// world-stage W21: the 18-sector / 10-legion fixture that sizes the outliner against §8e.3's
    /// ~28-row ceiling (18 + 10 = 28) and proves a collection surface is bounded rather than assumed
    /// bounded. Owner decision left open by the task, taken as its own stated fallback since no
    /// synchronous answer was available: a SIM-created world built by extending the real,
    /// already-validated `two-hearths` template (2 more sectors, 8 more legions) rather than a new
    /// `WorldTemplateCatalog` entry, which would need its own validation pass — this costs nothing
    /// in Core, exactly as the fallback's own reasoning says. Built and inserted directly through
    /// `RpgStore` (resolved from the test host's own DI container), then captured through the exact
    /// same `GET /state` byte-pinning pattern as every other fixture here — never hand-written.
    /// </summary>
    [Fact]
    public async Task The_checked_in_eighteen_sector_ten_legion_fixture_still_matches_the_live_dto()
    {
        var store = _factory.Services.GetRequiredService<RpgStore>();
        var player = store.CreatePlayer("EighteenTenFixtureOwner");

        var world = BuildEighteenSectorTenLegionWorld(player.Id);
        // Formula, not a literal (population-pin SE3.4, 2026-09-20): the fixture extends the real
        // two-hearths template by exactly 2 sectors and 8 legions (the builder's own doc comment) --
        // recomputed independently from the same template call, not retyped.
        var baseWorld = WorldTemplateCatalog.Build(WorldTemplateCatalog.TwoHeartsId, seed: 42, worldId: "eighteen-ten");
        Assert.Equal(baseWorld.Sectors.Count + 2, world.Sectors.Count);
        Assert.Equal(baseWorld.Entities.Count + 8, world.Entities.Count);
        var created = store.CreateWorld(player.Id, world);
        Assert.True(created.Ok, created.Reason);

        // The wire response is Dave's own fog-scoped view — `Entities` is only ever the viewer's own
        // forces (world-intel's own rule), so it shows his 5, not the world's full 10; the sector
        // ceiling is checked on the wire, the legion total on the world that produced it.
        var state = await _http.GetFromJsonAsync<JsonElement>($"/api/world/{world.WorldId}/state");
        Assert.Equal(18, state.GetProperty("sectors").GetArrayLength());
        Assert.Equal(5, state.GetProperty("entities").GetArrayLength());
        BlessOrAssert(state, BigFixturePath);
    }

    /// <summary>
    /// Extends the real `two-hearths` template (16 sectors, 2 legions) rather than authoring a
    /// world from nothing: 2 more sectors, each reachable by exactly one new lane off an existing
    /// outpost (satisfies `WorldValidation`'s connectivity rule trivially), and 8 more legions
    /// parked at existing sectors, split across Dave and Zomboss, mirroring the member shape the
    /// template's own two starting legions already use.
    /// </summary>
    static WorldState BuildEighteenSectorTenLegionWorld(long playerId)
    {
        var baseWorld = WorldTemplateCatalog.Build(WorldTemplateCatalog.TwoHeartsId, seed: 42, worldId: "eighteen-ten");

        const string Dave = "dave";
        const string Zomboss = "zomboss";

        WorldSector NewOutpost(string sectorId, string owner) => new()
        {
            SectorId = sectorId, TypeId = "stable", Climate = ElementTypeId.Earth, DangerBand = 1,
            Phase = SectorPhase.Held, OwnerFactionId = owner, AuthoredIntel = IntelState.Watched,
            StabilityMilli = 1000, LayoutX = 0, LayoutY = 0,
            Slots = new WorldSlot[]
            {
                new() { SlotIndex = 0, SlotTypeId = "seat", State = SlotState.Claimed, OwnerFactionId = owner }
            }
        };

        WorldEntity NewLegion(string entityId, string owner, string atSectorId) => new()
        {
            EntityId = entityId, Kind = WorldEntityKind.Legion, OwnerFactionId = owner,
            AtSectorId = atSectorId, Stance = "hold", MovementRemaining = 1000, CarriedLoam = 200,
            Members = new[]
            {
                new WorldEntityMember { SpeciesId = "peashooterzombie", Level = 1, Hp = 110 },
                new WorldEntityMember { SpeciesId = "conezombie", Level = 1, Hp = 110 }
            }
        };

        var newSectors = new[]
        {
            NewOutpost("aux-outpost-1", Dave),
            NewOutpost("aux-outpost-2", Zomboss)
        };

        var newLanes = new[]
        {
            new WorldLane { LaneId = "l-do-aux1", FromSectorId = "d-outpost", ToSectorId = "aux-outpost-1", TypeId = "corridor", Length = 800, Width = 1000 },
            new WorldLane { LaneId = "l-zo-aux2", FromSectorId = "z-outpost", ToSectorId = "aux-outpost-2", TypeId = "corridor", Length = 800, Width = 1000 }
        };

        var newLegions = new[]
        {
            NewLegion("e-dave-legion-2", Dave, "d-flank-1"),
            NewLegion("e-dave-legion-3", Dave, "d-flank-2"),
            NewLegion("e-dave-legion-4", Dave, "d-outpost"),
            NewLegion("e-dave-legion-5", Dave, "aux-outpost-1"),
            NewLegion("e-zomboss-legion-2", Zomboss, "z-flank-1"),
            NewLegion("e-zomboss-legion-3", Zomboss, "z-flank-2"),
            NewLegion("e-zomboss-legion-4", Zomboss, "z-outpost"),
            NewLegion("e-zomboss-legion-5", Zomboss, "aux-outpost-2")
        };

        return WorldValidation.Validate(baseWorld with
        {
            Sectors = baseWorld.Sectors.Concat(newSectors).OrderBy(s => s.SectorId, StringComparer.Ordinal).ToList(),
            Lanes = baseWorld.Lanes.Concat(newLanes).OrderBy(l => l.LaneId, StringComparer.Ordinal).ToList(),
            Entities = baseWorld.Entities.Concat(newLegions).OrderBy(e => e.EntityId, StringComparer.Ordinal).ToList()
        });
    }

    void BlessOrAssert(JsonElement state, string fixturePath)
    {
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }) + "\n";

        var path = Path.Combine(RepoRoot(), fixturePath);
        if (Environment.GetEnvironmentVariable("FUSIONRPG_BLESS_WORLD_FIXTURE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }

        Assert.True(File.Exists(path), $"missing fixture {fixturePath} — run with FUSIONRPG_BLESS_WORLD_FIXTURE=1");
        Assert.Equal(json.Replace("\r\n", "\n"), File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CONTRIBUTING.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
