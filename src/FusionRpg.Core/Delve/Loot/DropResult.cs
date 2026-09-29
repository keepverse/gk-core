using FusionRpg.Core.Items.Drops;

namespace FusionRpg.Core.Delve.Loot;

/// <summary>Two ways a room hands `loot-pack` something: a real, rolled item grant, or a deterministic
/// lane key (no roll — spec §5, verbatim: "no roll, fires on clear").</summary>
public enum DropResultKind
{
    Item,
    Key,
}

/// <summary>
/// `dungeon-loot` (spec-dungeon-loot.md §7, "Haul and the at-risk ledger", `:203`) — one grant, room-
/// scoped, ready for `loot-pack` to place: "Every grant becomes a `DropResult(kind, refId, instanceId?,
/// count, row, col, grantIndex)` row emitted to `loot-pack`, which owns capacity, arrangement, the
/// floor list and the D26 reconciliation. This module never places, floors or reads a cell count."
/// </summary>
public sealed record DropResult(
    DropResultKind Kind, string RefId, string? InstanceId, long Count, int Row, int Col, int GrantIndex)
{
    /// <summary>D3.11: one row per manifest grant — `RollRoom`'s own room coordinates, never re-read
    /// from the manifest itself (a `LootManifest` carries no row/col; that fact belongs to the room
    /// that rolled it, not the pipeline's own source-agnostic output).</summary>
    public static IReadOnlyList<DropResult> From(LootManifest manifest, RoomLootInput room)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));
        if (room is null) throw new ArgumentNullException(nameof(room));

        return manifest.Grants
            .Select(g => new DropResult(DropResultKind.Item, g.RefId, g.InstanceId, g.Count, room.Row, room.Col, g.Index))
            .ToList();
    }

    /// <summary>Spec §5, verbatim: "a room with `keyForLaneId` adds one deterministic `DropResult` of
    /// kind `Key`, `RefId = laneId` — no roll, fires on clear." <see cref="GrantIndex"/> is `-1` — a
    /// key is never one of the manifest's own numbered grants, so no real index applies.</summary>
    public static DropResult Key(string laneId, RoomLootInput room)
    {
        if (string.IsNullOrEmpty(laneId)) throw new ArgumentException("laneId must be non-empty", nameof(laneId));
        if (room is null) throw new ArgumentNullException(nameof(room));

        return new DropResult(DropResultKind.Key, laneId, null, 1, room.Row, room.Col, GrantIndex: -1);
    }
}
