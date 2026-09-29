namespace FusionRpg.Core.Delve.Objects;

public sealed class ObjectPreflightRejection : Exception
{
    public ObjectPreflightRejection(string message) : base(message) { }
}

/// <summary>
/// D3.29 (spec-supplies-and-objects.md §8, "Refused at delve creation") — three checks that throw
/// before the first room, naming the defect rather than leaving a soft lock. Pure: graph facts are
/// caller-supplied edges, never a `WorldState` read here (matching this program's own "read model
/// owned elsewhere" shape throughout `Delve/Objects/` and `Delve/Supplies/`).
/// </summary>
public static class ObjectPreflight
{
    /// <summary>One directed lane edge the reachability walk needs — just the three fields it reads,
    /// not a full `WorldLane`.</summary>
    public sealed record LaneEdge(string LaneId, string FromSectorId, string ToSectorId);

    /// <summary>
    /// §6/§8: "every gated door has a reachable key room or a break option... a `breakMode: none`
    /// domain whose key room sits on a route this raid mode does not walk is a preflight throw, not a
    /// soft lock." A plain BFS from <paramref name="entranceSectorId"/> over
    /// <paramref name="lanes"/> (undirected — a raid can walk either way down a corridor) decides
    /// reachability; <paramref name="keyRoomForLaneId"/> names, for each gated lane's own id, which
    /// sector carries its key (absent = no key was minted for this lane at all, itself a defect).
    /// </summary>
    public static void CheckGatedDoorsReachable(
        IReadOnlyList<LaneEdge> lanes, string entranceSectorId,
        IReadOnlyDictionary<string, string> keyRoomForLaneId, string breakMode)
    {
        if (lanes is null) throw new ArgumentNullException(nameof(lanes));
        if (keyRoomForLaneId is null) throw new ArgumentNullException(nameof(keyRoomForLaneId));
        if (string.IsNullOrWhiteSpace(entranceSectorId)) throw new ArgumentException("entranceSectorId required", nameof(entranceSectorId));

        var reachable = ReachableSectors(lanes, entranceSectorId);
        var breakAvailable = breakMode != ObjectsBreakMode.None;

        foreach (var (laneId, keySectorId) in keyRoomForLaneId)
        {
            if (reachable.Contains(keySectorId)) continue;
            if (breakAvailable) continue;
            throw new ObjectPreflightRejection(
                $"object.gated-door-unreachable: lane '{laneId}''s key room '{keySectorId}' is not reachable on this raid's walks, and objects.breakMode is 'none'");
        }
    }

    static IReadOnlySet<string> ReachableSectors(IReadOnlyList<LaneEdge> lanes, string entranceSectorId)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void AddEdge(string from, string to)
        {
            if (!adjacency.TryGetValue(from, out var list)) adjacency[from] = list = new List<string>();
            list.Add(to);
        }
        foreach (var lane in lanes)
        {
            AddEdge(lane.FromSectorId, lane.ToSectorId);
            AddEdge(lane.ToSectorId, lane.FromSectorId); // undirected: a corridor is walked both ways
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { entranceSectorId };
        var queue = new Queue<string>();
        queue.Enqueue(entranceSectorId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var neighbors)) continue;
            foreach (var next in neighbors)
                if (visited.Add(next))
                    queue.Enqueue(next);
        }
        return visited;
    }

    /// <summary>§8: "an empty curio verb set" refuses, naming the sector.</summary>
    public static void CheckCurioVerbSetsNonEmpty(IReadOnlyList<(string SectorId, IReadOnlyList<string> Verbs)> curioObjects)
    {
        if (curioObjects is null) throw new ArgumentNullException(nameof(curioObjects));
        foreach (var (sectorId, verbs) in curioObjects)
            if (verbs is null || verbs.Count == 0)
                throw new ObjectPreflightRejection($"object.empty-curio-verbs: sector '{sectorId}' has a curio with no verbs at all");
    }

    /// <summary>Runs every check this file owns, in the stated order, on one call.</summary>
    public static void Run(
        IReadOnlyList<LaneEdge> lanes, string entranceSectorId,
        IReadOnlyDictionary<string, string> keyRoomForLaneId, string breakMode,
        IReadOnlyList<(string SectorId, IReadOnlyList<string> Verbs)> curioObjects)
    {
        CheckGatedDoorsReachable(lanes, entranceSectorId, keyRoomForLaneId, breakMode);
        CheckCurioVerbSetsNonEmpty(curioObjects);
    }
}
