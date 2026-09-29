using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;

namespace FusionRpg.Core.Delve.Roll;

/// <summary>
/// The sixteen fairness/structural rules (spec-delve-graph-roll.md §3), run after
/// <see cref="DelveGraphRoll.Roll"/> and again over rows loaded back from the store — a persisted
/// graph can never be one the roller would have refused. Each rule throws
/// <see cref="InvalidOperationException"/> naming the rule and the id; none returns a flag.
/// </summary>
public static class DelveGraphValidation
{
    public static DelveGraph Validate(DelveGraph graph, DungeonTuning tuning)
    {
        Rule1StableOrderWellFormedIds(graph);
        Rule3Layered(graph);
        Rule4Reachable(graph);
        Rule5WalksComplete(graph);
        Rule6NoCrossings(graph);
        Rule7Boss(graph);
        Rule8FixedRows(graph);
        Rule9RowBans(graph, tuning);
        Rule10NeverAdjacent(graph);
        Rule11SiblingsDiffer(graph);
        Rule12KeyAboveGate(graph);
        Rule13OneWayDeeperOnly(graph);
        Rule14Secrets(graph);
        Rule15DeadEnds(graph, tuning);
        Rule16BandsMonotone(graph, tuning);
        Rule17RefuseNotClamp(graph);
        return graph;
    }

    static int RowOf(string sectorId) => int.Parse(sectorId.Substring(1, 2));
    static int ColOf(string sectorId) => int.Parse(sectorId.Substring(4, 2));

    static void Rule1StableOrderWellFormedIds(DelveGraph g)
    {
        var roomIds = g.Rooms.Select(r => r.SectorId).ToList();
        if (!roomIds.SequenceEqual(roomIds.OrderBy(x => x, StringComparer.Ordinal)))
            throw new InvalidOperationException("Rule1StableOrder: Rooms are not ordinal by SectorId.");
        var laneIds = g.Doors.Select(d => d.LaneId).ToList();
        if (!laneIds.SequenceEqual(laneIds.OrderBy(x => x, StringComparer.Ordinal)))
            throw new InvalidOperationException("Rule1StableOrder: Doors are not ordinal by LaneId.");

        var known = g.Rooms.Select(r => r.SectorId).ToHashSet(StringComparer.Ordinal);
        foreach (var room in g.Rooms)
            if (room.SectorId.Length != 6 || room.SectorId[0] != 'r' || room.SectorId[3] != 'c')
                throw new InvalidOperationException($"Rule1WellFormedIds: sector id '{room.SectorId}' is not shaped 'r{{00}}c{{00}}'.");
        foreach (var door in g.Doors)
        {
            if (door.LaneId != $"{door.FromSectorId}-{door.ToSectorId}")
                throw new InvalidOperationException($"Rule1WellFormedIds: lane id '{door.LaneId}' does not match its endpoints.");
            if (!known.Contains(door.FromSectorId)) throw new InvalidOperationException($"Rule1WellFormedIds: lane '{door.LaneId}' names unknown room '{door.FromSectorId}'.");
            if (!known.Contains(door.ToSectorId)) throw new InvalidOperationException($"Rule1WellFormedIds: lane '{door.LaneId}' names unknown room '{door.ToSectorId}'.");
        }
    }

    static void Rule3Layered(DelveGraph g)
    {
        foreach (var door in g.Doors)
        {
            var fromRow = RowOf(door.FromSectorId);
            var toRow = RowOf(door.ToSectorId);
            if (door.TypeId == "secret")
            {
                if (toRow != fromRow) throw new InvalidOperationException($"Rule3Layered: secret lane '{door.LaneId}' must join rooms on the same row.");
                continue;
            }
            if (toRow != fromRow + 1)
                throw new InvalidOperationException($"Rule3Layered: lane '{door.LaneId}' goes from row {fromRow} to row {toRow}, not one row deeper.");
        }
    }

    static void Rule4Reachable(DelveGraph g)
    {
        if (g.Rooms.Count == 0) throw new InvalidOperationException("Rule4Reachable: a delve needs at least one room.");
        var adjacency = UndirectedAdjacency(g);
        var roots = g.Rooms.Where(r => r.LayoutY == 0).Select(r => r.SectorId).ToList();
        var seen = Bfs(roots, adjacency);
        foreach (var room in g.Rooms)
            if (!seen.Contains(room.SectorId))
                throw new InvalidOperationException($"Rule4Reachable: room '{room.SectorId}' is unreachable from a row-0 room.");
    }

    static void Rule5WalksComplete(DelveGraph g)
    {
        var bossId = g.Rooms.Single(r => r.TypeId == "boss").SectorId;
        var starts = new HashSet<int>();
        foreach (var walk in g.Walks)
        {
            if (walk.SectorIds.Count == 0 || walk.SectorIds[^1] != bossId)
                throw new InvalidOperationException($"Rule5WalksComplete: walk {walk.WalkIndex} does not end at the boss.");
            starts.Add(ColOf(walk.SectorIds[0]));
        }
        var partyWalks = g.Walks.Where(w => w.PartyIndex != null).ToList();
        var partyStarts = partyWalks.Select(w => ColOf(w.SectorIds[0])).ToList();
        if (partyStarts.Count != partyStarts.Distinct().Count())
            throw new InvalidOperationException("Rule5WalksComplete: two party routes share a starting column.");
        if (starts.Count < 2 && g.Walks.Count > 1)
            throw new InvalidOperationException("Rule5WalksComplete: fewer than two distinct starts across all walks.");
    }

    static void Rule6NoCrossings(DelveGraph g)
    {
        var byRowFromTo = g.Doors.Where(d => d.TypeId != "secret").Select(d => (Row: RowOf(d.FromSectorId), From: ColOf(d.FromSectorId), To: ColOf(d.ToSectorId))).ToHashSet();
        foreach (var (row, from, to) in byRowFromTo)
            if (to == from + 1 && byRowFromTo.Contains((row, from + 1, from)))
                throw new InvalidOperationException($"Rule6NoCrossings: lanes at row {row} between columns {from} and {from + 1} cross.");
    }

    static void Rule7Boss(DelveGraph g)
    {
        var bosses = g.Rooms.Where(r => r.TypeId == "boss").ToList();
        if (bosses.Count != 1) throw new InvalidOperationException($"Rule7Boss: expected exactly one boss room, found {bosses.Count}.");
        var boss = bosses[0];
        var maxRow = g.Rooms.Max(r => r.LayoutY);
        if (boss.LayoutY != maxRow) throw new InvalidOperationException("Rule7Boss: the boss room is not on the last row.");
        if (g.Doors.Any(d => d.FromSectorId == boss.SectorId))
            throw new InvalidOperationException("Rule7Boss: the boss has an outbound door.");

        // A secret room can attach to a rest room (§2 step 9), landing it on row N-1 too -- but a
        // secret is a leaf (rule 14) and was never on any walk, so it is excluded from the fan-in.
        var secretIds = g.Facts.Where(f => f.IsSecret).Select(f => f.SectorId).ToHashSet(StringComparer.Ordinal);
        var lastCorridorRow = maxRow - 1;
        var lastCorridorRooms = g.Rooms.Where(r => r.LayoutY == lastCorridorRow && !secretIds.Contains(r.SectorId)).Select(r => r.SectorId).ToHashSet();
        var inbound = g.Doors.Where(d => d.ToSectorId == boss.SectorId).Select(d => d.FromSectorId).ToHashSet();
        foreach (var room in lastCorridorRooms)
            if (!inbound.Contains(room))
                throw new InvalidOperationException($"Rule7Boss: row-(N-1) room '{room}' has no door to the boss.");
    }

    static void Rule8FixedRows(DelveGraph g)
    {
        // A secret room can attach to a row-0 or a rest (row N-1) anchor (§2 step 9) and lands on
        // the SAME row as its anchor with its own eligible kind -- excluded here for the same
        // reason Rule7Boss excludes it: a secret was never part of the fixed-row assignment.
        var secretIds = g.Facts.Where(f => f.IsSecret).Select(f => f.SectorId).ToHashSet(StringComparer.Ordinal);
        var maxRow = g.Rooms.Max(r => r.LayoutY);
        foreach (var room in g.Rooms.Where(r => r.LayoutY == 0 && !secretIds.Contains(r.SectorId)))
            if (room.TypeId != "fight") throw new InvalidOperationException($"Rule8FixedRows: row 0 room '{room.SectorId}' is '{room.TypeId}', not fight.");
        foreach (var room in g.Rooms.Where(r => r.LayoutY == maxRow - 1 && !secretIds.Contains(r.SectorId)))
            if (room.TypeId != "rest") throw new InvalidOperationException($"Rule8FixedRows: row-(N-1) room '{room.SectorId}' is '{room.TypeId}', not rest.");

        var cacheRowCandidates = g.Rooms.Where(r => r.TypeId == "cache" && !secretIds.Contains(r.SectorId)).Select(r => r.LayoutY).Distinct().ToList();
        if (cacheRowCandidates.Count > 1)
            throw new InvalidOperationException("Rule8FixedRows: cache rooms span more than one row.");
    }

    static void Rule9RowBans(DelveGraph g, DungeonTuning tuning)
    {
        var maxRow = g.Rooms.Max(r => r.LayoutY);
        var n = maxRow; // boss is row N; corridor rows are 0..N-1
        foreach (var room in g.Rooms)
        {
            if (room.TypeId is "fight" or "cache" or "rest" or "boss") continue; // fixed rows have no window
            var node = tuning.Nodes[room.TypeId];
            var earliest = checked(node.EarliestRowMilli * n / 1000);
            var latest = checked(node.LatestRowMilli * n / 1000);
            if (room.LayoutY < earliest || room.LayoutY > latest)
                throw new InvalidOperationException($"Rule9RowBans: '{room.SectorId}' is kind '{room.TypeId}' at row {room.LayoutY}, outside its [{earliest},{latest}] window.");
        }
    }

    static void Rule10NeverAdjacent(DelveGraph g)
    {
        var kindById = g.Rooms.ToDictionary(r => r.SectorId, r => r.TypeId, StringComparer.Ordinal);
        foreach (var door in g.Doors.Where(d => d.TypeId != "secret"))
        {
            var fromKind = kindById[door.FromSectorId];
            var toKind = kindById[door.ToSectorId];
            if (RoomKindCatalog.Get(fromKind).NeverAdjacentTo.Contains(toKind))
                throw new InvalidOperationException($"Rule10NeverAdjacent: '{door.FromSectorId}' ({fromKind}) -> '{door.ToSectorId}' ({toKind}) is a banned adjacency.");
        }
    }

    // Fixed-row kinds (§2 step 4) are UNIFORM by construction -- every fight/cache/rest/boss row
    // shares its kind by design, so "siblings differ" only ever constrains the drawn (step 5) rows.
    static readonly HashSet<string> FixedRowKinds = new(StringComparer.Ordinal) { "fight", "cache", "rest", "boss" };

    static void Rule11SiblingsDiffer(DelveGraph g)
    {
        var kindById = g.Rooms.ToDictionary(r => r.SectorId, r => r.TypeId, StringComparer.Ordinal);
        var childrenByParent = g.Doors.Where(d => d.TypeId != "secret").GroupBy(d => d.FromSectorId).ToDictionary(gr => gr.Key, gr => gr.Select(d => d.ToSectorId).ToList());
        foreach (var (_, children) in childrenByParent)
        {
            var kinds = children.Select(c => kindById[c]).ToList();
            if (kinds.All(FixedRowKinds.Contains)) continue;
            if (kinds.Count != kinds.Distinct().Count())
                throw new InvalidOperationException($"Rule11SiblingsDiffer: children [{string.Join(", ", children)}] are not pairwise distinct in kind.");
        }
    }

    static void Rule12KeyAboveGate(DelveGraph g)
    {
        var factByRoom = g.Facts.ToDictionary(f => f.SectorId, StringComparer.Ordinal);
        foreach (var door in g.Doors.Where(d => d.GateKeyId != null))
        {
            var key = g.Facts.SingleOrDefault(f => f.KeyForLaneId == door.LaneId)
                ?? throw new InvalidOperationException($"Rule12KeyAboveGate: gate '{door.LaneId}' has no key room.");
            if (key.Kind is not ("cache" or "elite"))
                throw new InvalidOperationException($"Rule12KeyAboveGate: key room '{key.SectorId}' for gate '{door.LaneId}' is kind '{key.Kind}', not cache/elite.");
            if (key.Row >= RowOf(door.FromSectorId))
                throw new InvalidOperationException($"Rule12KeyAboveGate: key room '{key.SectorId}' is not strictly above gate '{door.LaneId}'.");

            var adjacency = UndirectedAdjacency(g, excludeLaneId: door.LaneId);
            var roots = g.Rooms.Where(r => r.LayoutY == 0).Select(r => r.SectorId);
            var reachable = Bfs(roots, adjacency);
            if (!reachable.Contains(key.SectorId))
                throw new InvalidOperationException($"Rule12KeyAboveGate: key room '{key.SectorId}' is not reachable without gate '{door.LaneId}'.");
        }
    }

    static void Rule13OneWayDeeperOnly(DelveGraph g)
    {
        var inboundCount = g.Doors.GroupBy(d => d.ToSectorId).ToDictionary(gr => gr.Key, gr => gr.Count());
        foreach (var door in g.Doors.Where(d => d.TypeId == "one-way"))
        {
            if (RowOf(door.ToSectorId) <= RowOf(door.FromSectorId))
                throw new InvalidOperationException($"Rule13OneWayDeeperOnly: one-way door '{door.LaneId}' does not go strictly deeper.");
            if (inboundCount[door.ToSectorId] < 2)
                throw new InvalidOperationException($"Rule13OneWayDeeperOnly: '{door.ToSectorId}' loses its only inbound door if '{door.LaneId}' is one-way.");
        }
    }

    static void Rule14Secrets(DelveGraph g)
    {
        var maxRow = g.Rooms.Max(r => r.LayoutY);
        var secretRooms = g.Facts.Where(f => f.IsSecret).ToList();
        var secretRows = new HashSet<int>();
        foreach (var secret in secretRooms)
        {
            if (!RoomKindCatalog.Get(secret.Kind).SecretEligible)
                throw new InvalidOperationException($"Rule14Secrets: secret room '{secret.SectorId}' has kind '{secret.Kind}', which is not secretEligible.");
            if (secret.Row == maxRow)
                throw new InvalidOperationException($"Rule14Secrets: secret room '{secret.SectorId}' sits on the boss row.");
            if (!secretRows.Add(secret.Row))
                throw new InvalidOperationException($"Rule14Secrets: two secret rooms attach at row {secret.Row} — secrets must not be adjacent.");
            if (g.Doors.Any(d => d.FromSectorId == secret.SectorId))
                throw new InvalidOperationException($"Rule14Secrets: secret room '{secret.SectorId}' has an outbound door — a secret must be a leaf.");
        }
    }

    static void Rule15DeadEnds(DelveGraph g, DungeonTuning tuning)
    {
        var maxRow = g.Rooms.Max(r => r.LayoutY);
        // "No outbound door OTHER THAN the boss and secrets" (§3 rule 15): a room whose only
        // outbound lanes go to the boss (every row-(N-1) room, by rule 7's fan-in) or to an
        // attached secret does not lose its dead-end status because of those two exceptions.
        var boss = g.Rooms.Single(r => r.TypeId == "boss").SectorId;
        var outboundCount = g.Rooms.ToDictionary(
            r => r.SectorId,
            r => g.Doors.Count(d => d.FromSectorId == r.SectorId && d.TypeId != "secret" && d.ToSectorId != boss),
            StringComparer.Ordinal);
        var deadEnds = g.Rooms.Count(r => r.LayoutY != maxRow && r.TypeId != "boss" && !g.Facts.First(f => f.SectorId == r.SectorId).IsSecret && outboundCount[r.SectorId] == 0);
        if (deadEnds < tuning.GraphMinDeadEnds)
            throw new InvalidOperationException($"Rule15DeadEnds: {deadEnds} dead ends, below the required {tuning.GraphMinDeadEnds}.");
    }

    static void Rule16BandsMonotone(DelveGraph g, DungeonTuning tuning)
    {
        var maxRow = g.Rooms.Max(r => r.LayoutY);
        foreach (var door in g.Doors.Where(d => d.TypeId != "secret"))
        {
            var from = g.Rooms.Single(r => r.SectorId == door.FromSectorId);
            var to = g.Rooms.Single(r => r.SectorId == door.ToSectorId);
            if (to.DangerBand < from.DangerBand)
                throw new InvalidOperationException($"Rule16BandsMonotone: '{door.LaneId}' goes from band {from.DangerBand} to a lower band {to.DangerBand}.");
        }
        var boss = g.Rooms.Single(r => r.TypeId == "boss");
        var lastCorridor = g.Rooms.Where(r => r.LayoutY == maxRow - 1).Select(r => r.DangerBand).Distinct().ToList();
        if (lastCorridor.Count == 1 && boss.DangerBand != lastCorridor[0] + tuning.DepthBossBandDelta)
            throw new InvalidOperationException($"Rule16BandsMonotone: boss band {boss.DangerBand} != row-(N-1) band {lastCorridor[0]} + bossBandDelta {tuning.DepthBossBandDelta}.");
    }

    static void Rule17RefuseNotClamp(DelveGraph g)
    {
        foreach (var fact in g.Facts)
        {
            if (fact.SightLanes < 0) throw new InvalidOperationException($"Rule17RefuseNotClamp: '{fact.SectorId}' has a negative sightLanes ({fact.SightLanes}) -- a tuning error, never clamped.");
            if (fact.ScoutSightLanes < 0) throw new InvalidOperationException($"Rule17RefuseNotClamp: '{fact.SectorId}' has a negative scoutSightLanes ({fact.ScoutSightLanes}) -- a tuning error, never clamped.");
        }
    }

    static Dictionary<string, List<string>> UndirectedAdjacency(DelveGraph g, string? excludeLaneId = null)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var room in g.Rooms) adjacency[room.SectorId] = new List<string>();
        foreach (var door in g.Doors)
        {
            if (door.LaneId == excludeLaneId) continue;
            adjacency[door.FromSectorId].Add(door.ToSectorId);
            adjacency[door.ToSectorId].Add(door.FromSectorId);
        }
        return adjacency;
    }

    static HashSet<string> Bfs(IEnumerable<string> roots, Dictionary<string, List<string>> adjacency)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var r in roots) if (seen.Add(r)) queue.Enqueue(r);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (adjacency.TryGetValue(current, out var neighbours))
                foreach (var next in neighbours)
                    if (seen.Add(next)) queue.Enqueue(next);
        }
        return seen;
    }
}
