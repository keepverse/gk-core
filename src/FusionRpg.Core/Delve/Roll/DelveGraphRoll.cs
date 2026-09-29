using FusionRpg.Core.Actions.Seeding;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.World;

namespace FusionRpg.Core.Delve.Roll;

/// <summary>Thrown for every structural refusal (spec-delve-graph-roll.md N7/S2-2: refuse-not-clamp
/// — a bad input throws, it is never shrunk, padded or floored).</summary>
public sealed class DelveGraphRollRejection : Exception
{
    public DelveGraphRollRejection(string message) : base(message) { }
}

/// <summary>
/// One pure function that turns a domain anchor and a layout template into one delve's room graph
/// (spec-delve-graph-roll.md). Mirrors <see cref="World.WorldTemplateCatalog.Build"/>'s shape: a
/// static pure builder, seed as a parameter, the result piped through the validator, unknown/bad
/// input throws. No clock, no store, no I/O, no retry-until-valid loop — every "no candidate fits"
/// case throws immediately (§2, §8).
/// </summary>
public static class DelveGraphRoll
{
    // Structural, with the exemption comment T2 asks for (spec §Tunables "Structural" row): the
    // two-digit id buffer bound -- changing it breaks id ORDERING (r{00}c{00}), not how the game
    // feels. MinCorridorRows/MinWidth are the fewest rows/columns the fixed-row rules (§2 step 4)
    // and the two-distinct-starts rule (§3 rule 5) can possibly hold with.
    const int MaxRows = 99;
    const int MaxCols = 99;
    const int MinCorridorRows = 4; // three fixed rows (0, cache, N-1) plus one free row
    const int MinWidth = 2; // two distinct walk starts need at least two columns

    public static DelveGraph Roll(
        DomainAnchor domain, LayoutTemplate layout, ulong seed, string raidMode, DungeonTuning tuning)
        => DelveGraphValidation.Validate(RollUnchecked(domain, layout, seed, raidMode, tuning), tuning);

    static DelveGraph RollUnchecked(DomainAnchor domain, LayoutTemplate layout, ulong seed, string raidMode, DungeonTuning tuning)
    {
        if (!RaidModeCatalog.IsKnown(raidMode))
            throw new DelveGraphRollRejection($"Unknown raid mode '{raidMode}'.");
        if (!tuning.RaidModes.TryGetValue(raidMode, out var raid))
            throw new DelveGraphRollRejection($"Raid mode '{raidMode}' has no tuning row.");
        if (!tuning.DepthBandRows.TryGetValue(layout.SizeBand, out var rowsRange))
            throw new DelveGraphRollRejection($"Unknown layout sizeBand '{layout.SizeBand}'.");
        if (!tuning.WidthBandCols.TryGetValue(layout.WidthBand, out var colsRange))
            throw new DelveGraphRollRejection($"Unknown layout widthBand '{layout.WidthBand}'.");
        if (!tuning.BranchinessPathWalks.TryGetValue(layout.Branchiness, out var pathWalks))
            throw new DelveGraphRollRejection($"Unknown layout branchiness '{layout.Branchiness}'.");
        if (!tuning.DangerBand.TryGetValue(domain.DangerBand, out var entranceBand))
            throw new DelveGraphRollRejection($"Unknown domain dangerBand '{domain.DangerBand}'.");
        if (!tuning.GateDensityPerRoomMilli.TryGetValue(layout.GateDensity, out var gateDensityMilli))
            throw new DelveGraphRollRejection($"Unknown gateDensity '{layout.GateDensity}'.");
        if (!tuning.OneWayDensityPerRoomMilli.TryGetValue(layout.OneWayDensity, out var oneWayDensityMilli))
            throw new DelveGraphRollRejection($"Unknown oneWayDensity '{layout.OneWayDensity}'.");
        if (!tuning.SecretDensityPerRoomMilli.TryGetValue(layout.SecretDensity, out var secretDensityMilli))
            throw new DelveGraphRollRejection($"Unknown secretDensity '{layout.SecretDensity}'.");

        // ---- Step 1: dimensions (stream "dungeon:layout") ------------------------------------
        var layoutRng = SeededRng.DeriveStream(seed, DelveStreams.Layout);
        var n = rowsRange.Min + layoutRng.NextInt(rowsRange.Max - rowsRange.Min + 1);
        var c = colsRange.Min + layoutRng.NextInt(colsRange.Max - colsRange.Min + 1);
        var k = pathWalks + raid.WalksDelta;
        var p = raid.Parties;

        if (n < MinCorridorRows) throw new DelveGraphRollRejection($"Rolled {n} corridor rows, below the {MinCorridorRows}-row floor (three fixed rows plus one free row).");
        if (c < MinWidth) throw new DelveGraphRollRejection($"Rolled {c} columns, below the {MinWidth}-column floor (two distinct walk starts).");
        if (p > k) throw new DelveGraphRollRejection($"Raid mode '{raidMode}' needs {p} party routes but only {k} walks are offered.");
        if (p > c) throw new DelveGraphRollRejection($"Raid mode '{raidMode}' needs {p} distinct party start columns but the layout only has {c}.");
        if (n > MaxRows || c > MaxCols) throw new DelveGraphRollRejection($"Rolled dimensions {n}x{c} exceed the {MaxRows}x{MaxCols} id-buffer bound.");

        var bossRow = n;
        var cacheRow = checked((int)(tuning.GraphFixedRowsMidCacheRowMilli * n / 1000));

        // ---- Step 2: walks (stream "dungeon:walk:{k}" per walk) -------------------------------
        // A "step" is a placed (row, fromCol) -> (row+1, toCol) hop, tracked as one flat set so a
        // later walk's crossing check (rule 6) and the gate/one-way passes (steps 7-8) all read the
        // same source of truth. The boss hop uses toCol = -1 as a sentinel (the boss has no column).
        var steps = new HashSet<(int Row, int FromCol, int ToCol)>();
        var nodeKind = new Dictionary<(int Row, int Col), string?>();
        var childrenByParent = new Dictionary<(int Row, int Col), List<(int Row, int Col)>>();
        var walks = new List<DelveWalk>();
        var partyStartsUsed = new HashSet<int>();
        var allStartsUsed = new HashSet<int>();

        void AddChild((int Row, int Col) parent, (int Row, int Col) child)
        {
            if (!childrenByParent.TryGetValue(parent, out var list)) childrenByParent[parent] = list = new List<(int, int)>();
            if (!list.Contains(child)) list.Add(child);
        }

        bool CrossesExisting(int row, int fromCol, int toCol)
        {
            if (toCol == fromCol + 1) return steps.Contains((row, fromCol + 1, fromCol));
            if (toCol == fromCol - 1) return steps.Contains((row, fromCol - 1, fromCol));
            return false;
        }

        for (var w = 0; w < k; w++)
        {
            var walkRng = SeededRng.DeriveStream(seed, DelveStreams.Walk(w));
            var isParty = w < p;

            int start;
            if (isParty)
            {
                var free = Enumerable.Range(0, c).Where(col => !partyStartsUsed.Contains(col)).ToList();
                start = free[walkRng.NextInt(free.Count)]; // p <= c is enforced above, so `free` is never empty
                partyStartsUsed.Add(start);
            }
            else if (w == p && p <= 1 && allStartsUsed.Count == 1)
            {
                // Rule 5's "at least two distinct starts across all walks": with zero or one party
                // route, force the FIRST non-party walk away from the lone start already claimed,
                // rather than leaving distinctness to chance and refusing on collision (no
                // retry-until-valid loop).
                var free = Enumerable.Range(0, c).Where(col => !allStartsUsed.Contains(col)).ToList();
                start = free.Count == 0 ? walkRng.NextInt(c) : free[walkRng.NextInt(free.Count)];
            }
            else
            {
                start = walkRng.NextInt(c);
            }
            allStartsUsed.Add(start);

            var sectorIds = new List<string>(n + 1);
            var col = start;
            nodeKind.TryAdd((0, col), "fight"); // row 0 is fixed (step 4)
            sectorIds.Add(DelveStreams.SectorId(0, col));

            for (var row = 0; row < n - 1; row++)
            {
                var candidates = new List<int>();
                foreach (var delta in new[] { -1, 0, 1 })
                {
                    var next = col + delta;
                    if (next < 0 || next >= c) continue;
                    if (CrossesExisting(row, col, next)) continue;
                    candidates.Add(next);
                }
                if (candidates.Count == 0)
                    throw new DelveGraphRollRejection($"Walk {w} has no offered step at row {row} col {col} -- every candidate crosses an existing lane.");

                var nextCol = candidates[walkRng.NextInt(candidates.Count)];
                steps.Add((row, col, nextCol));
                AddChild((row, col), (row + 1, nextCol));

                // Register the node's EXISTENCE unconditionally -- a generic row's kind is null
                // until step 5 draws it, but the room must exist now so a lane can target it.
                var kindHere = row + 1 == cacheRow ? "cache" : row + 1 == n - 1 ? "rest" : null;
                nodeKind.TryAdd((row + 1, nextCol), kindHere);
                sectorIds.Add(DelveStreams.SectorId(row + 1, nextCol));
                col = nextCol;
            }

            // Then to the boss -- every walk's final hop, regardless of which row-(N-1) column it
            // lands on (rule 7's fan-in: every row-(N-1) node gets a door to the single boss node).
            steps.Add((n - 1, col, -1));
            AddChild((n - 1, col), (bossRow, 0));
            sectorIds.Add(DelveStreams.SectorId(bossRow, 0));

            walks.Add(new DelveWalk(w, isParty ? w : null, sectorIds));
        }
        nodeKind[(bossRow, 0)] = "boss";

        // ---- Step 3: dead ends (still stream "dungeon:layout", continuing the same derived rng) --
        var hangPoints = nodeKind.Keys.Where(rc => rc.Row <= n - 3).ToList();
        if (hangPoints.Count < tuning.GraphMinDeadEnds)
            throw new DelveGraphRollRejection($"Only {hangPoints.Count} hang points for {tuning.GraphMinDeadEnds} required dead ends.");

        var spurLanes = new List<(string From, string To)>();
        var spurPositions = new List<(int Row, int Col)>(); // the spur ROOMS themselves -- secret attach points read this, never usedHangPoints (that holds the PARENT, a different room)
        var usedHangPoints = new HashSet<(int Row, int Col)>();
        var nextExtraCol = c; // dead-end spurs and secrets both live at col >= C, in disjoint bands
        for (var i = 0; i < tuning.GraphMinDeadEnds; i++)
        {
            var free = hangPoints.Where(h => !usedHangPoints.Contains(h)).ToList();
            if (free.Count == 0) throw new DelveGraphRollRejection("Ran out of distinct hang points for dead ends.");
            var hang = free[layoutRng.NextInt(free.Count)];
            usedHangPoints.Add(hang);

            var spur = (Row: hang.Row + 1, Col: nextExtraCol++);
            // A spur can coincidentally hang at the cache row (hang.Row in 0..N-3 can put spur.Row
            // on the same row cacheRow lands on) -- it then shares that row's fixed kind exactly
            // like a normal walk node would, rather than being independently drawn (its row can
            // never be 0 or N-1, since spur.Row >= 1 and <= N-2).
            nodeKind[spur] = spur.Row == cacheRow ? "cache" : null;
            AddChild(hang, spur);
            spurPositions.Add(spur);
            spurLanes.Add((DelveStreams.SectorId(hang.Row, hang.Col), DelveStreams.SectorId(spur.Row, spur.Col)));
        }

        // ---- Step 5: kinds (stream "dungeon:kind:{r}:{c}" per unassigned node, row/col order) --
        var parentsByChild = new Dictionary<(int Row, int Col), List<(int Row, int Col)>>();
        foreach (var (parent, children) in childrenByParent)
            foreach (var child in children)
            {
                if (!parentsByChild.TryGetValue(child, out var list)) parentsByChild[child] = list = new List<(int, int)>();
                list.Add(parent);
            }

        var allNodes = nodeKind.Keys.OrderBy(rc => rc.Row).ThenBy(rc => rc.Col).ToList();
        foreach (var node in allNodes)
        {
            if (nodeKind[node] != null) continue; // already fixed (row 0 / cache / rest / boss)

            var parentRowKinds = parentsByChild.TryGetValue(node, out var parents)
                ? parents.Select(pp => nodeKind[pp]).Where(k => k != null).Cast<string>().ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            var neverAdjacent = parentRowKinds.SelectMany(pk => RoomKindCatalog.Get(pk).NeverAdjacentTo).ToHashSet(StringComparer.Ordinal);

            var siblingKinds = (parents ?? Enumerable.Empty<(int Row, int Col)>())
                .SelectMany(pp => childrenByParent.TryGetValue(pp, out var sibs) ? sibs : Enumerable.Empty<(int, int)>())
                .Where(sib => sib != node)
                .Select(sib => nodeKind.TryGetValue(sib, out var sk) ? sk : null)
                .Where(k => k != null).Cast<string>().ToHashSet(StringComparer.Ordinal);

            var options = new List<WeightedOption<string>>();
            foreach (var kindDef in RoomKindCatalog.All)
            {
                if (kindDef.RoomKindId is "fight" or "cache" or "rest" or "boss") continue; // fixed-row kinds are never drawn
                if (neverAdjacent.Contains(kindDef.RoomKindId)) continue;
                if (siblingKinds.Contains(kindDef.RoomKindId)) continue;
                if (!kindDef.BossRowAllowed && node.Row == bossRow) continue;

                var nodeTuning = tuning.Nodes[kindDef.RoomKindId];
                var earliest = checked(nodeTuning.EarliestRowMilli * n / 1000);
                var latest = checked(nodeTuning.LatestRowMilli * n / 1000);
                if (node.Row < earliest || node.Row > latest) continue;

                options.Add(new WeightedOption<string>(kindDef.RoomKindId, checked((int)nodeTuning.WeightMilli)));
            }

            var streamName = DelveStreams.Kind(node.Row, node.Col);
            var rollSeed = unchecked((long)SeededRng.DeriveStream(seed, streamName).NextULong());
            nodeKind[node] = WeightedChoice.Pick(options, rollSeed, streamName);
        }

        // ---- Step 6: archetypes (stream "dungeon:room:{r}:{c}") -------------------------------
        var archetype = new Dictionary<(int Row, int Col), string>();
        foreach (var node in allNodes)
        {
            var kind = nodeKind[node]!;
            var kindDef = RoomKindCatalog.Get(kind);
            var palette = domain.RoomPalette.Where(rp => rp.Kind == kind && (kindDef.ClimateNeutral || rp.Climate == domain.Climate)).ToList();
            if (palette.Count == 0)
                throw new DelveGraphRollRejection($"Domain '{domain.DomainId}' has no room palette entry for (kind='{kind}', climate='{domain.Climate}').");

            var options = palette.Select(rp => new WeightedOption<string>(rp.RoomId, 1)).ToList(); // uniform: no per-archetype weight column exists in the tuning surface
            var streamName = DelveStreams.Room(node.Row, node.Col);
            var rollSeed = unchecked((long)SeededRng.DeriveStream(seed, streamName).NextULong());
            archetype[node] = WeightedChoice.Pick(options, rollSeed, streamName);
        }

        // ---- Step 7: gates (stream "dungeon:gate:{r}:{c}" per candidate lane, rows 1..N-3) -----
        var gateKeyByLane = new Dictionary<string, string>(StringComparer.Ordinal);
        var keyForLaneByRoom = new Dictionary<(int Row, int Col), string>();
        // Explicit (row, fromCol) order, never HashSet enumeration order (§8): this loop's key-room
        // claiming is stateful (keyForLaneByRoom), so which gate wins a contested key room must not
        // depend on an incidental, unguaranteed iteration order.
        foreach (var (row, fromCol, toCol) in steps.Where(s => s.Row is >= 1 && s.ToCol >= 0).Where(s => s.Row <= n - 3).OrderBy(s => s.Row).ThenBy(s => s.FromCol).ThenBy(s => s.ToCol))
        {
            var laneId = DelveStreams.LaneId(DelveStreams.SectorId(row, fromCol), DelveStreams.SectorId(row + 1, toCol));
            var gateRng = SeededRng.DeriveStream(seed, DelveStreams.Gate(row, fromCol));
            if (gateRng.NextPerMille() >= gateDensityMilli) continue;

            var stepsWithoutThisLane = steps.Where(s => s != (row, fromCol, toCol));
            var reachableWithoutGate = ReachableFrom(allNodes.Where(x => x.Row == 0), stepsWithoutThisLane);
            var keyCandidates = allNodes
                .Where(rc => nodeKind[rc] is "cache" or "elite" && rc.Row < row
                    && !keyForLaneByRoom.ContainsKey(rc) // never double-book a room as two gates' key (first gate to claim it keeps it)
                    && reachableWithoutGate.Contains(DelveStreams.SectorId(rc.Row, rc.Col)))
                .OrderBy(rc => rc.Row).ThenBy(rc => rc.Col)
                .ToList();
            if (keyCandidates.Count == 0) continue; // a placement rule, not a fallback (§2 step 7)

            var keyRoom = keyCandidates[0];
            gateKeyByLane[laneId] = $"key.{laneId}";
            keyForLaneByRoom[keyRoom] = laneId;
        }

        // ---- Step 8: one-way (stream "dungeon:oneway:{r}:{c}" per eligible lane) --------------
        var oneWayLanes = new HashSet<string>(StringComparer.Ordinal);
        var inboundCount = steps.GroupBy(s => (s.Row + 1, s.ToCol)).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (row, fromCol, toCol) in steps.Where(s => s.ToCol >= 0))
        {
            var laneId = DelveStreams.LaneId(DelveStreams.SectorId(row, fromCol), DelveStreams.SectorId(row + 1, toCol));
            if (gateKeyByLane.ContainsKey(laneId)) continue;
            if (row + 1 == n - 1) continue; // never into the rest row
            if (row == n - 1) continue; // not into the boss (this module's own conservative extra: the spec does not name the boss hop, but a one-way flag on the graph's single fan-in sink has no meaning)
            if (!inboundCount.TryGetValue((row + 1, toCol), out var inbound) || inbound < 2) continue; // `to` must keep a two-way inbound lane

            var owRng = SeededRng.DeriveStream(seed, DelveStreams.OneWay(row, fromCol));
            if (owRng.NextPerMille() < oneWayDensityMilli) oneWayLanes.Add(laneId);
        }

        // ---- Step 9: secrets (stream "dungeon:secret:{r}:{c}") --------------------------------
        var secretLanes = new List<(string From, string To)>();
        var appearRng = SeededRng.DeriveStream(seed, DelveStreams.Secret(0, 0));
        if (appearRng.NextPerMille() < tuning.GraphSecretAppearMilli)
        {
            var attachPoints = spurPositions
                .Concat(nodeKind.Where(kv => kv.Value == "rest").Select(kv => kv.Key))
                .Distinct()
                .OrderBy(a => a.Row).ThenBy(a => a.Col)
                .ToList();
            var placedSecretAtRow = new HashSet<int>(); // no two secrets adjacent -- one per row is the conservative reading this module takes

            foreach (var attach in attachPoints)
            {
                if (placedSecretAtRow.Contains(attach.Row)) continue;
                var streamName = DelveStreams.Secret(attach.Row, attach.Col);
                var secretRng = SeededRng.DeriveStream(seed, streamName);
                if (secretRng.NextPerMille() >= secretDensityMilli) continue;

                // Same [earliest, latest] row window as step 5's regular draw (rule 9 applies to
                // every room regardless of how it entered the graph) -- an attach row with no
                // eligible kind in its window places no secret here rather than violating the ban.
                var eligible = RoomKindCatalog.All.Where(kd => kd.SecretEligible
                    && attach.Row >= checked(tuning.Nodes[kd.RoomKindId].EarliestRowMilli * n / 1000)
                    && attach.Row <= checked(tuning.Nodes[kd.RoomKindId].LatestRowMilli * n / 1000)).ToList();
                if (eligible.Count == 0) continue;
                var options = eligible.Select(kd => new WeightedOption<string>(kd.RoomKindId, checked((int)tuning.Nodes[kd.RoomKindId].WeightMilli))).ToList();
                var rollSeed = unchecked((long)secretRng.NextULong());
                var kind = WeightedChoice.Pick(options, rollSeed, streamName);

                var secretNode = (Row: attach.Row, Col: nextExtraCol++);
                nodeKind[secretNode] = kind;
                secretLanes.Add((DelveStreams.SectorId(attach.Row, attach.Col), DelveStreams.SectorId(secretNode.Row, secretNode.Col)));
                placedSecretAtRow.Add(attach.Row);
            }
        }

        // ---- Bands, sight, assembly (no draw; §6, §7, §10) ------------------------------------
        var rooms = new Dictionary<string, WorldSector>(StringComparer.Ordinal);
        var facts = new List<DelveRoomFact>();
        var secretDestinations = secretLanes.Select(sl => sl.To).ToHashSet(StringComparer.Ordinal);
        foreach (var node in nodeKind.Keys)
        {
            var sectorId = DelveStreams.SectorId(node.Row, node.Col);
            var kind = nodeKind[node]!;
            var baseBand = node.Row == bossRow
                ? checked(BaseBandOf(entranceBand, tuning, n - 1) + tuning.DepthBossBandDelta)
                : BaseBandOf(entranceBand, tuning, node.Row);

            // Starting shape: sightBand extras are the archetype's own field on the ROOM anchor
            // (ROOM_OWNERSHIP.sightBand), which this module's minimal RoomPaletteEntry projection
            // does not carry (delve-scope's DelveSight.ForParty owns resolving the real one, §6's
            // own note). This module still emits the tuning-only base radii so a caller with no
            // archetype detail yet has a correct floor.
            var sightLanes = tuning.SightLanes;
            var scoutLanes = tuning.SightScoutLanes;

            rooms[sectorId] = new WorldSector { SectorId = sectorId, TypeId = kind, Climate = domain.Climate, DangerBand = baseBand, LayoutX = node.Col, LayoutY = node.Row };
            facts.Add(new DelveRoomFact(
                node.Row, node.Col, sectorId, kind, archetype.TryGetValue(node, out var arch) ? arch : "", baseBand,
                secretDestinations.Contains(sectorId), sightLanes, scoutLanes, PartyRouteMaskOf(walks, sectorId),
                keyForLaneByRoom.TryGetValue(node, out var kfl) ? kfl : null));
        }

        var doors = new Dictionary<string, WorldLane>(StringComparer.Ordinal);
        void AddDoor(string from, string to, string typeId, string? gateKeyId = null)
        {
            var laneId = DelveStreams.LaneId(from, to);
            doors[laneId] = new WorldLane { LaneId = laneId, FromSectorId = from, ToSectorId = to, TypeId = typeId, GateKeyId = gateKeyId };
        }
        foreach (var (row, fromCol, toCol) in steps)
        {
            var from = DelveStreams.SectorId(row, fromCol);
            var to = toCol < 0 ? DelveStreams.SectorId(bossRow, 0) : DelveStreams.SectorId(row + 1, toCol);
            var laneId = DelveStreams.LaneId(from, to);
            var typeId = gateKeyByLane.ContainsKey(laneId) ? "gated" : oneWayLanes.Contains(laneId) ? "one-way" : "passage";
            AddDoor(from, to, typeId, gateKeyByLane.TryGetValue(laneId, out var key) ? key : null);
        }
        foreach (var (from, to) in spurLanes) AddDoor(from, to, "passage");
        foreach (var (from, to) in secretLanes) AddDoor(from, to, "secret");

        return new DelveGraph(
            rooms.Values.OrderBy(r => r.SectorId, StringComparer.Ordinal).ToList(),
            doors.Values.OrderBy(d => d.LaneId, StringComparer.Ordinal).ToList(),
            facts.OrderBy(f => f.SectorId, StringComparer.Ordinal).ToList(),
            walks);
    }

    static int BaseBandOf(int entranceBand, DungeonTuning tuning, int row) => checked(entranceBand + row / tuning.DepthRowsPerBandStep);

    static HashSet<string> ReachableFrom(IEnumerable<(int Row, int Col)> roots, IEnumerable<(int Row, int FromCol, int ToCol)> steps)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Link(string a, string b)
        {
            if (!adjacency.TryGetValue(a, out var list)) adjacency[a] = list = new List<string>();
            list.Add(b);
        }
        foreach (var (row, fromCol, toCol) in steps)
        {
            if (toCol < 0) continue; // the boss hop -- irrelevant to "reachable without this ONE gated lane" queries above row N-1
            Link(DelveStreams.SectorId(row, fromCol), DelveStreams.SectorId(row + 1, toCol));
            Link(DelveStreams.SectorId(row + 1, toCol), DelveStreams.SectorId(row, fromCol));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var r in roots)
        {
            var id = DelveStreams.SectorId(r.Row, r.Col);
            if (seen.Add(id)) queue.Enqueue(id);
        }
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (adjacency.TryGetValue(current, out var neighbours))
                foreach (var next in neighbours)
                    if (seen.Add(next)) queue.Enqueue(next);
        }
        return seen;
    }

    static int PartyRouteMaskOf(IReadOnlyList<DelveWalk> walks, string sectorId)
    {
        var mask = 0;
        foreach (var walk in walks)
            if (walk.PartyIndex is { } p && walk.SectorIds.Contains(sectorId))
                mask |= 1 << p;
        return mask;
    }
}
