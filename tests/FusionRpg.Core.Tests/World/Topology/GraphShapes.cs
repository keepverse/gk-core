using FusionRpg.Core.World;

namespace FusionRpg.Core.Tests.World.Topology;

/// <summary>
/// Hand-built graphs with answers you can work out on paper.
///
/// `first-light` is too small and too well connected to exercise anything interesting — every
/// topology claim worth making needs a shape chosen to make it true or false, so the shapes live
/// here and the tests name them.
///
/// These are raw <see cref="WorldState"/> records, deliberately not run through
/// <see cref="WorldValidation"/>: a bare path of three sectors has no homeworld and no player
/// faction, and demanding one would mean every shape carried scaffolding that has nothing to do
/// with what is being tested.
/// </summary>
public static class GraphShapes
{
    /// <summary>Builds a world from edges written as <c>"a-b"</c>. Ids are kept ordinal-sorted.</summary>
    public static WorldState From(params string[] edges) => From(1000, edges);

    public static WorldState From(int laneLength, params string[] edges)
    {
        var sectorIds = edges
            .SelectMany(e => e.Split('-'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        return new WorldState
        {
            WorldId = "shape",
            TemplateId = "shape",
            Sectors = sectorIds
                .Select(id => new WorldSector
                {
                    SectorId = id,
                    TypeId = "barren",
                    Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = "wildland" } }
                })
                .ToList(),
            Lanes = edges
                .Select(e =>
                {
                    var ends = e.Split('-');
                    return new WorldLane
                    {
                        LaneId = "l-" + e,
                        FromSectorId = ends[0],
                        ToSectorId = ends[1],
                        TypeId = "rift",
                        Length = laneLength,
                        Width = 1000
                    };
                })
                .OrderBy(l => l.LaneId, StringComparer.Ordinal)
                .ToList()
        };
    }

    /// <summary>The same world with every collection reversed — ordering must not change an answer.</summary>
    public static WorldState Reversed(WorldState world) => world with
    {
        Sectors = world.Sectors.Reverse().ToList(),
        Lanes = world.Lanes.Reverse().ToList()
    };

    public static WorldState Sever(WorldState world, string laneId) => world with
    {
        Lanes = world.Lanes.Select(l => l.LaneId == laneId ? l with { State = LaneState.Severed } : l).ToList()
    };

    /// <summary>`a–b–c`: the middle sector is the only thing holding it together.</summary>
    public static WorldState Path() => From("a-b", "b-c");

    /// <summary>`a–b–c–a`: nothing is critical, everything has a way round.</summary>
    public static WorldState Cycle() => From("a-b", "b-c", "a-c");

    /// <summary>Two triangles joined by one lane. The join is the whole point of this module.</summary>
    public static WorldState Barbell() => From(
        "a-b", "a-c", "b-c",       // west cluster
        "c-d",                      // the neck
        "d-e", "d-f", "e-f");      // east cluster

    /// <summary>One hub, three spokes. The hub cuts everything; a spoke cuts nothing.</summary>
    public static WorldState Star() => From("hub-x", "hub-y", "hub-z");

    /// <summary>
    /// A five-sector ring. Big enough that losing one member genuinely lengthens journeys — a
    /// triangle does not, because removing any corner leaves the other two touching.
    /// </summary>
    public static WorldState Ring() => From("a-b", "b-c", "c-d", "d-e", "a-e");

    /// <summary>Two separate pairs — a faction holding two islands.</summary>
    public static WorldState TwoIslands() => From("a-b", "y-z");
}
