using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Delve.Objects;

/// <summary>§4's own closed four (the fourth, <see cref="Structure"/>, projects nothing yet — no
/// `structure-schema` row exists to read, "later" per the source table).</summary>
public enum ObjectKind
{
    Curio,
    Obstacle,
    Building,
    Structure,
}

/// <summary>
/// D3.27 (spec-supplies-and-objects.md §4) — one room's interactive thing, a PROJECTION over three
/// sources (curio, gated door, room kind) — never its own seed kind (§4: "this module mints no object
/// seed kind"). `Requirement` is nullable, matching this repo's own established `PredicateNode`
/// convention exactly (`PredicateCompiler.cs`'s own comment: "zero children is distinct from an
/// ABSENT predicate: absent means 'always' and is legal, while `And()` would quietly mean true" — an
/// empty `And` is a validation REFUSAL, not a stand-in for "no requirement"). `OwnerKey` is always
/// `sector:{sectorId}` (`OwnerScope.cs` §6: a delve room *is* a `WorldSector`).
/// </summary>
public sealed record RoomObject(
    string SectorId, ObjectKind Kind, string SourceRef, IReadOnlyList<string> Verbs,
    PredicateNode? Requirement, bool OneShot, string OwnerKey)
{
    public static string OwnerKeyFor(string sectorId) => $"sector:{sectorId}";
}

/// <summary>The room kinds §4's own table names "building" — reusable within the visit, per its
/// owner's rule (shrine, merchant, rest, wild's altar/cage, boss).</summary>
public static class BuildingRoomKinds
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { "shrine", "merchant", "rest", "wild", "boss" };
}

/// <summary>One curio source's own three fields — the minimum §4's own table needs, not a guessed
/// full "curio row" aggregate no shipped type defines yet.</summary>
public sealed record CurioSource(string SourceRef, IReadOnlyList<string> Verbs, PredicateNode? Requirement);

/// <summary>One gated-door source — `WorldLane.TypeId == "gated"` with a live `GateKeyId`
/// (`WorldState.cs:246-261`); a lane whose key is already spent (`GateKeyId == null`) projects nothing.</summary>
public sealed record GatedDoorSource(string LaneId, string? GateKeyId);

public static class RoomObjectBuilder
{
    /// <summary>
    /// §4's own three-source projection for one room. Every source is optional — a room may carry
    /// none, one, or (a gated boss room) more than one. Pure: no store, no clock, no RNG (§9);
    /// building the projection "draws nothing" (§4's own closing line).
    /// </summary>
    public static IReadOnlyList<RoomObject> For(
        string sectorId, string roomKind, CurioSource? curio, GatedDoorSource? gatedDoor,
        IReadOnlyList<string> buildingVerbsForKind, bool allowDestroyOnObstacles)
    {
        if (string.IsNullOrWhiteSpace(sectorId)) throw new ArgumentException("sectorId required", nameof(sectorId));
        if (string.IsNullOrWhiteSpace(roomKind)) throw new ArgumentException("roomKind required", nameof(roomKind));
        if (buildingVerbsForKind is null) throw new ArgumentNullException(nameof(buildingVerbsForKind));

        var ownerKey = RoomObject.OwnerKeyFor(sectorId);
        var objects = new List<RoomObject>();

        if (curio is not null)
            objects.Add(new RoomObject(sectorId, ObjectKind.Curio, curio.SourceRef, curio.Verbs, curio.Requirement, OneShot: true, ownerKey));

        if (gatedDoor is { GateKeyId: not null } door)
        {
            // objects.breakMode == none (Tunables): no break option, the key is the only way through
            // -- destroy is simply not offered, matching §6's own "a reachable key OR a break option".
            var verbs = allowDestroyOnObstacles ? new[] { "open", "destroy" } : new[] { "open" };
            objects.Add(new RoomObject(sectorId, ObjectKind.Obstacle, door.LaneId, verbs, Requirement: null, OneShot: false, ownerKey));
        }

        if (BuildingRoomKinds.All.Contains(roomKind) && buildingVerbsForKind.Count > 0)
            objects.Add(new RoomObject(sectorId, ObjectKind.Building, roomKind, buildingVerbsForKind, Requirement: null, OneShot: false, ownerKey));

        return objects;
    }
}
