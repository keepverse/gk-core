using FusionRpg.Core.Delve;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Delve;

/// <summary>
/// D1.27 — the sealed per-run seed (spec-delve-graph-roll.md §8). <see cref="RpgStore.CreateDelve"/>'s
/// own doc comment already states the contract: "the seed is sealed by the CALLER (delve-graph-roll
/// rolls it) and never re-derived here." This proves the other half of that contract — a graph
/// re-rolled from the SAME stored seed reproduces exactly what was persisted, so a client can always
/// rebuild a delve's layout from nothing but its header row.
/// </summary>
public class DelveGraphRollRoundTripTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly RoomTypeCatalog _roomTypes;
    readonly DoorTypeCatalog _doorTypes;
    readonly DungeonTuning _tuning;

    public DelveGraphRollRoundTripTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var repoRoot = FindRepoRoot();
        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(repoRoot, "data", "seed", "dungeon", "_registry"));
        _roomTypes = new RoomTypeCatalog(registries.RoomKinds);
        _doorTypes = new DoorTypeCatalog(registries.DoorKinds);
        _tuning = DungeonTuningLoader.Parse(File.ReadAllText(Path.Combine(repoRoot, "data", "tuning", "dungeon.v3.json")), registries);
    }

    public void Dispose() => _testStore.Dispose();

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "seed", "dungeon"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }

    static DomainAnchor RichFireDomain() => new(
        "domain-roundtrip-1", ElementTypeId.Fire, "shallow",
        RoomKindCatalog.All.Select(k => new RoomPaletteEntry($"room-{k.RoomKindId}", k.RoomKindId, k.ClimateNeutral ? null : ElementTypeId.Fire)).ToList());

    static LayoutTemplate ShortWide() => new("layout-short-wide", "short", "wide", "linear", "none", "none", "none");

    // The graph->store row mapping is a future entry-endpoint's job (domain-catalog, not yet built)
    // -- this inline helper is test-only glue, matching "tests construct inline" (tunables-ssot §7.2).
    static IReadOnlyList<DelveRoomRow> ToRoomRows(DelveGraph graph) =>
        graph.Facts.Select(f => new DelveRoomRow(
            f.SectorId, f.Row, f.Col, f.Kind, f.ArchetypeId, Visited: false, Cleared: false, f.KeyForLaneId,
            EventId: null, ResolvedKind: null, ResolvedArchetypeId: null, FloorJson: "[]", Revision: 0)).ToList();

    [Fact]
    public void A_graph_rerolled_from_the_stored_seed_matches_what_CreateDelve_persisted()
    {
        const ulong seed = 20260906UL;
        var domain = RichFireDomain();
        var layout = ShortWide();
        var graph = DelveGraphRoll.Roll(domain, layout, seed, "solo", _tuning);

        var world = new WorldState { WorldId = "w-roundtrip-1", TemplateId = layout.LayoutId, Seed = seed, Sectors = graph.Rooms, Lanes = graph.Doors };
        var (ok, reason, delve) = _store.CreateDelve(
            playerId: 1, domainId: domain.DomainId, raidMode: "solo", rungId: "hard", correlationId: "corr-roundtrip-1",
            parentWorldId: null, worldId: world.WorldId, templateId: layout.LayoutId, seed: seed,
            world: world, rooms: ToRoomRows(graph), roomCatalog: _roomTypes, doorCatalog: _doorTypes);

        Assert.True(ok, reason);
        Assert.NotNull(delve);
        Assert.Equal(seed, delve!.Seed);

        var reloaded = _store.LoadDelve(delve.DelveId);
        var reloadedRooms = _store.LoadDelveRooms(delve.DelveId);
        Assert.NotNull(reloaded);
        Assert.Equal(seed, reloaded!.Seed);

        // The seed alone (never re-derived) is enough to reproduce the exact same rooms.
        var reRolled = DelveGraphRoll.Roll(domain, layout, reloaded.Seed, reloaded.RaidMode, _tuning);

        var storedShape = reloadedRooms.OrderBy(r => r.SectorId, StringComparer.Ordinal)
            .Select(r => (r.SectorId, r.RowIndex, r.ColIndex, r.Kind, r.ArchetypeId, r.KeyForLaneId)).ToList();
        var reRolledShape = reRolled.Facts.OrderBy(f => f.SectorId, StringComparer.Ordinal)
            .Select(f => (f.SectorId, f.Row, f.Col, f.Kind, f.ArchetypeId, f.KeyForLaneId)).ToList();
        Assert.Equal(storedShape, reRolledShape);
    }

    [Fact]
    public void CreateDelve_never_generates_its_own_seed_the_stored_seed_is_exactly_the_caller_supplied_one()
    {
        const ulong seed = 777777UL;
        var domain = RichFireDomain();
        var layout = ShortWide();
        var graph = DelveGraphRoll.Roll(domain, layout, seed, "pair", _tuning);
        var world = new WorldState { WorldId = "w-roundtrip-2", TemplateId = layout.LayoutId, Seed = seed, Sectors = graph.Rooms, Lanes = graph.Doors };

        var (ok, _, delve) = _store.CreateDelve(
            playerId: 2, domainId: domain.DomainId, raidMode: "pair", rungId: "hard", correlationId: "corr-roundtrip-2",
            parentWorldId: null, worldId: world.WorldId, templateId: layout.LayoutId, seed: seed,
            world: world, rooms: ToRoomRows(graph), roomCatalog: _roomTypes, doorCatalog: _doorTypes);

        Assert.True(ok);
        Assert.Equal(seed, delve!.Seed); // exactly the caller's seed -- never a freshly-minted one
    }
}
