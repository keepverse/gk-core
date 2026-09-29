namespace FusionRpg.Core.Delve.Pack;

/// <summary>`pack.move {from: grid|floor, itemKey, toRow, toCol}` (spec-loot-pack.md §6) — `From` is
/// one of <see cref="PackMoves.FromGrid"/>/<see cref="PackMoves.FromFloor"/>.</summary>
public sealed record PackMoveDecision(string From, string ItemKey, int ToRow, int ToCol);

/// <summary>`pack.drop {itemKey, qty?, by: player|autopilot|use|door, rule?}` (spec-loot-pack.md §6) —
/// <see cref="Qty"/> null means the whole stack; <see cref="By"/> is one of
/// <see cref="PackMoves.DropBy"/>'s four values.</summary>
public sealed record PackDropDecision(string ItemKey, long? Qty, string By, string? Rule);

/// <summary>
/// D3.21 (spec-loot-pack.md §6) — applying one already-decided `pack.move`/`pack.drop`. Pure: no
/// store, no clock, no RNG. "Only applied decisions are logged" (§6) — a caller checks <c>Ok</c>
/// before calling `AppendDecision`; this type never touches the decision log itself.
/// </summary>
public static class PackMoves
{
    public const string FromGrid = "grid";
    public const string FromFloor = "floor";

    public static class DropBy
    {
        public const string Player = "player";
        public const string Autopilot = "autopilot";
        public const string Use = "use";
        public const string Door = "door";
    }

    /// <summary>
    /// `pack.move`. From <see cref="FromFloor"/> only while <paramref name="partyIsInThisRoom"/> (§6:
    /// "from floor only while the party stands in that room") — the caller already knows whether the
    /// party's current room is the one whose floor list this is. The target rectangle must be free of
    /// every cell that is not the moved item's own — picking the item up before checking the target is
    /// what makes moving it onto its OWN current cells legal.
    /// </summary>
    public static (bool Ok, string Reason, PackGrid Grid, IReadOnlyList<PackItem> Floor) ApplyMove(
        PackGrid grid, IReadOnlyList<PackItem> floor, PackMoveDecision decision, bool partyIsInThisRoom)
    {
        if (grid is null) throw new ArgumentNullException(nameof(grid));
        if (floor is null) throw new ArgumentNullException(nameof(floor));
        if (decision is null) throw new ArgumentNullException(nameof(decision));

        if (decision.From == FromFloor)
        {
            if (!partyIsInThisRoom) return (false, "pack.not-here", grid, floor);
            var item = floor.FirstOrDefault(f => PackGrid.KeyOf(f) == decision.ItemKey);
            if (item is null) return (false, "pack.not-here", grid, floor);
            if (!grid.IsFree(decision.ToRow, decision.ToCol, item.W, item.H))
                return (false, RefusalReason(grid, decision, item), grid, floor);

            var newGrid = grid.With(item, decision.ToRow, decision.ToCol);
            var newFloor = floor.Where(f => PackGrid.KeyOf(f) != decision.ItemKey).ToList();
            return (true, "", newGrid, newFloor);
        }

        if (decision.From == FromGrid)
        {
            var cell = grid.Find(decision.ItemKey);
            if (cell is null) return (false, "pack.not-here", grid, floor);
            var picked = grid.Remove(decision.ItemKey);
            if (!picked.IsFree(decision.ToRow, decision.ToCol, cell.Item.W, cell.Item.H))
                return (false, RefusalReason(picked, decision, cell.Item), grid, floor);

            var newGrid = picked.With(cell.Item, decision.ToRow, decision.ToCol);
            return (true, "", newGrid, floor);
        }

        return (false, "pack.not-here", grid, floor);
    }

    static string RefusalReason(PackGrid grid, PackMoveDecision decision, PackItem item)
    {
        var outOfGrid = decision.ToRow < 0 || decision.ToCol < 0
            || decision.ToRow + item.H > grid.Rows || decision.ToCol + item.W > grid.Cols;
        return outOfGrid ? "pack.out-of-grid" : "pack.cell-occupied";
    }

    /// <summary>
    /// `pack.drop`. A partial quantity (typically <see cref="DropBy.Use"/> consuming one of a stack)
    /// shrinks the stack in place and keeps its cell — "frees the cell at 0" (§6), never before. The
    /// whole stack leaving frees the cell; a HAUL item joins the room's floor, a CARRY-IN item does
    /// not (§6: "destroyed at settlement" — `CloseDelve`'s own concern, not modeled here).
    /// </summary>
    public static (bool Ok, string Reason, PackGrid Grid, IReadOnlyList<PackItem> Floor) ApplyDrop(
        PackGrid grid, IReadOnlyList<PackItem> floor, PackDropDecision decision)
    {
        if (grid is null) throw new ArgumentNullException(nameof(grid));
        if (floor is null) throw new ArgumentNullException(nameof(floor));
        if (decision is null) throw new ArgumentNullException(nameof(decision));

        var cell = grid.Find(decision.ItemKey);
        if (cell is null) return (false, "pack.not-here", grid, floor);

        var dropQty = decision.Qty ?? cell.Item.Qty;
        if (dropQty <= 0 || dropQty > cell.Item.Qty) return (false, "pack.not-here", grid, floor);

        if (dropQty < cell.Item.Qty)
        {
            var shrunk = cell.Item with { Qty = cell.Item.Qty - dropQty };
            var inPlace = grid.Remove(decision.ItemKey).With(shrunk, cell.Row, cell.Col);
            return (true, "", inPlace, floor);
        }

        var freed = grid.Remove(decision.ItemKey);
        var newFloor = cell.Item.Origin == PackItemOrigin.Haul ? floor.Append(cell.Item).ToList() : floor;
        return (true, "", freed, newFloor);
    }

    /// <summary>One logged entry — exactly one of <see cref="Move"/>/<see cref="Drop"/>, matching the
    /// decision log's own `{seq, kind, partyIndex, tick?, payload}` shape (spec-delve-battle-profile.md
    /// `:141-144`) where `kind` picks which payload this is.</summary>
    public sealed record PackTraceEntry(PackMoveDecision? Move, PackDropDecision? Drop, bool PartyIsInThisRoom = false)
    {
        public static PackTraceEntry OfMove(PackMoveDecision move, bool partyIsInThisRoom = false) => new(move, null, partyIsInThisRoom);
        public static PackTraceEntry OfDrop(PackDropDecision drop) => new(null, drop);
    }

    /// <summary>
    /// §10's own determinism claim, made executable: "replaying the `pack.*` entries over the reveals
    /// re-derives every party's grid and floor byte for byte." Folds <see cref="ApplyMove"/>/
    /// <see cref="ApplyDrop"/> over the logged sequence in order — since "only applied decisions are
    /// logged" (§6), a refusal partway through means the log itself is corrupt or stale, not a normal
    /// outcome; the caller gets the refusal reason and the state as of the last GOOD entry, never a
    /// silently-partial replay.
    /// </summary>
    public static (bool Ok, string Reason, PackGrid Grid, IReadOnlyList<PackItem> Floor) Replay(
        PackGrid grid, IReadOnlyList<PackItem> floor, IReadOnlyList<PackTraceEntry> entries)
    {
        if (grid is null) throw new ArgumentNullException(nameof(grid));
        if (floor is null) throw new ArgumentNullException(nameof(floor));
        if (entries is null) throw new ArgumentNullException(nameof(entries));

        foreach (var entry in entries)
        {
            var (ok, reason, nextGrid, nextFloor) = entry.Move is not null
                ? ApplyMove(grid, floor, entry.Move, entry.PartyIsInThisRoom)
                : ApplyDrop(grid, floor, entry.Drop!);
            if (!ok) return (false, reason, grid, floor);
            grid = nextGrid;
            floor = nextFloor;
        }
        return (true, "", grid, floor);
    }
}
