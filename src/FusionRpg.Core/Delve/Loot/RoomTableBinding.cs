using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Delve.Loot;

/// <summary>The three delve source kinds `LootCorrelation`/`DropTableValidator` already accept
/// (D3.10) — named once here so a caller never re-types the literal string.</summary>
public static class DungeonSourceKinds
{
    public const string DungeonRoom = "dungeon-room";
    public const string DungeonClear = "dungeon-clear";
    public const string DungeonQuest = "dungeon-quest";
}

/// <summary>What a room rolls on, or `null` when its own kind carries no table at all (spec §5's own
/// "no-table" row: `curio · shrine · trap · wild · rest · merchant · unknown`).</summary>
public sealed record RoomTableBindingResult(string SourceKind, string TableId);

/// <summary>
/// `dungeon-loot` D3.14 (spec-dungeon-loot.md §5, "Room-kind table map") — PARTIALLY BUILT: the
/// kind-to-table lookup (this file's own core) is done and proven; the `keyForLaneId` → `DropResult`
/// grant half of the spec's own cited signature (`RoomTableBinding.For(kind, lootBinding,
/// keyForLaneId)`, `:395`) is not built — see the honest gap below.
/// </summary>
public static class RoomTableBinding
{
    // "drop" is already registered by DropTableValidator/DropTableDraw/LootPipeline/
    // WorldSectorLootSource, each independently -- ContentRuleNamespaces.Register tolerates repeat
    // registration of the same namespace (the established, already-proven pattern those four share).
    // This file adds its own registration too, so `drop.unknown-loot-source` below never depends on
    // one of those OTHER types having already run its own static constructor first.
    static RoomTableBinding() => ContentRuleNamespaces.Register("drop");

    /// <summary>Room kinds that carry no table at all — spec §5's own no-table row, verbatim: "no
    /// table; a `cache` outcome of `event-deck` calls `dungeon-room` at the room's coordinates."</summary>
    static readonly IReadOnlySet<string> NoTableKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "curio", "shrine", "trap", "wild", "rest", "merchant", "unknown",
    };

    /// <summary>
    /// Spec §5's own table, verbatim, resolved for one room kind: `fight`/`elite`/`cache`/`boss` (the
    /// fight side) all read `dungeon-room` off the domain's own `lootBinding[kind]`; a "secret" room is
    /// never passed here directly — spec, verbatim: "a secret cache is a cache" — the CALLER resolves a
    /// secret room to its own underlying kind (e.g. `cache`) before calling `For`, exactly the way
    /// `EventFilters`'s own "read model owned elsewhere" callers pre-resolve their own facts. `boss`'s
    /// OWN separate `dungeon-clear` relic binding (source id = the domain) is D3.15's own job ("The
    /// boss first-clear grant") — this function resolves only the fight-side `dungeon-room` table every
    /// table-having kind shares, `boss` included.
    /// </summary>
    public static AtomRejection For(
        string kind, IReadOnlyDictionary<string, string> lootBinding, out RoomTableBindingResult? result)
    {
        result = null;
        if (kind is null) throw new ArgumentNullException(nameof(kind));
        if (lootBinding is null) throw new ArgumentNullException(nameof(lootBinding));

        if (NoTableKinds.Contains(kind))
            return AtomRejection.Ok; // result stays null -- no table for this kind, not a refusal

        if (!lootBinding.TryGetValue(kind, out var tableId))
            return AtomRejection.ContentRule("drop.unknown-loot-source",
                $"room kind '{kind}' has no lootBinding entry and is not one of the no-table kinds");

        result = new RoomTableBindingResult(DungeonSourceKinds.DungeonRoom, tableId);
        return AtomRejection.Ok;
    }
}
