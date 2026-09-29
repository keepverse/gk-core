namespace FusionRpg.Core.Delve.Roll;

/// <summary>
/// One owner of every `dungeon:*` stream name and of the `r{00}c{00}` / `{from}-{to}` id shapes
/// (spec-delve-graph-roll.md §2, §8; D1.23). Every stream feeds
/// <see cref="Battle.SeededRng.DeriveStream"/> off the delve's own sealed <c>ulong</c> seed — one
/// name, one draw, so an extra roll in one stream never shifts another.
///
/// Reserves, but never draws, the names other wave-2/3/4 modules derive at entry
/// (`event-deck`, `dungeon-loot`, `supplies-and-objects`, `wild-room`) — formatted here so every
/// future caller shares one naming authority instead of re-deriving the same shape (N13).
/// </summary>
public static class DelveStreams
{
    public const string Layout = "dungeon:layout";

    public static string Walk(int walkIndex) => $"dungeon:walk:{walkIndex}";
    public static string Kind(int row, int col) => $"dungeon:kind:{row}:{col}";
    public static string Room(int row, int col) => $"dungeon:room:{row}:{col}";
    public static string Gate(int row, int col) => $"dungeon:gate:{row}:{col}";
    public static string OneWay(int row, int col) => $"dungeon:oneway:{row}:{col}";
    public static string Secret(int row, int col) => $"dungeon:secret:{row}:{col}";

    // Reserved for entry-time draws by other modules -- never called from DelveGraphRoll itself.
    public static string Event(int row, int col) => $"dungeon:event:{row}:{col}";
    public static string Loot(int row, int col) => $"dungeon:loot:{row}:{col}";
    public static string Unknown(int row, int col) => $"dungeon:unknown:{row}:{col}";
    public static string Supply(int row, int col, int n) => $"dungeon:supply:{row}:{col}:{n}";
    public static string SupplyEntry(int n) => $"dungeon:supply:entry:{n}";
    public static string Merchant(int row, int col) => $"dungeon:merchant:{row}:{col}";
    public static string Wild(int row, int col, string what) => $"dungeon:wild:{row}:{col}:{what}"; // what: seq|traits|cage
    public static string Altar(int row, int col, int n) => $"dungeon:altar:{row}:{col}:{n}";

    /// <summary>`r{00}c{00}` — zero-padded so ordinal string order equals (row, col) order (§8).
    /// The two-digit buffer is `graph.MaxRows`/`MaxCols` = 99, a structural id-buffer bound, not a
    /// balance number (spec §Tunables).</summary>
    public static string SectorId(int row, int col) => $"r{row:00}c{col:00}";

    public static string LaneId(string fromSectorId, string toSectorId) => $"{fromSectorId}-{toSectorId}";
}
