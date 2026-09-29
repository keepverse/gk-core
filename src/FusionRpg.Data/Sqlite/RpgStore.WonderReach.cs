using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

// empire-wonder-surfaces `wonder-wire` §Design 4 (plan Task 4A.4): reachable-first picking's
// planning-time source — "which relic instances are reachable HERE for (entity, sector)".
//
// The `IsRelicSpendableUnlocked` predicate (`RpgStore.WonderBuild.cs`) factored to a list: the
// SAME ownership/live/unassigned/Relic-container filter JOINED to the SAME reachability UNION
// (aboard the issuing legion's cargo OR in the target sector's storage) — minus the batch-only
// `claimed` set, which is commit-scoped and meaningless at planning time. It is factored, never
// forked: the gate keeps calling its own function; this read answers the list question the gate
// cannot ask. SQL lives here, only here (guard-dal).
public sealed partial class RpgStore
{
    /// <summary>
    /// Planning-time reachability list (spec §Design 4). Read-only — performs no write of any
    /// kind. Sorted ordinal, de-duplicated. Empty for an unknown world (no player row), a blank
    /// entity/sector (matches nothing, same posture as the gate's NULL handling), an unknown or
    /// unpositioned legion, or nothing reachable — inert, not broken, the same posture as
    /// <c>ListClaimableCaches</c> for an unknown legion.
    /// </summary>
    internal static List<string> ListReachableRelicsUnlocked(
        SqliteConnection db, SqliteTransaction tx,
        string worldId, string entityId, string sectorId, string playerStr)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(entityId) || string.IsNullOrWhiteSpace(sectorId))
            return found;

        // Owned, live, unassigned, genuinely-a-relic (the gate's ownership half, verbatim:
        // 'owned' = armoury, CargoAboardDisposition = aboard marker written by LoadCargo;
        // origin traces to a real Relic-kind container; dual unassigned NOT EXISTS mirrors
        // IsArmouryResidentUnlocked) AND reachable-right-now (the gate's UNION half, verbatim).
        // One statement, so the list and the gate cannot disagree about either half.
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT DISTINCT i.instance_id FROM rpg_item i
            WHERE i.player_id = $p
              AND i.disposition IN ('owned', $cargo)
              AND i.origin_kind = 'drop'
              AND EXISTS (SELECT 1 FROM effect_container c
                          WHERE c.container_id = i.origin_ref AND c.container_kind = 'relic')
              AND NOT EXISTS (SELECT 1 FROM rpg_item_assignment a WHERE a.ref_id = i.instance_id)
              AND NOT EXISTS (SELECT 1 FROM rpg_player_item_assignment pa WHERE pa.ref_id = i.instance_id)
              AND (EXISTS (SELECT 1 FROM rpg_world_entity_cargo g
                           WHERE g.world_id = $w AND g.entity_id = $e AND g.instance_id = i.instance_id)
                OR EXISTS (SELECT 1 FROM rpg_world_sector_storage s
                           WHERE s.world_id = $w AND s.sector_id = $s AND s.instance_id = i.instance_id))
            ORDER BY 1;
            """;
        cmd.Parameters.AddWithValue("$p", playerStr);
        cmd.Parameters.AddWithValue("$cargo", CargoAboardDisposition);
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        cmd.Parameters.AddWithValue("$s", sectorId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            found.Add(r.GetString(0));
        return found;
    }

    /// <summary>
    /// Public entry: the relic instances reachable for (entity, sector) right now (spec §Design 4).
    /// Read-only — performs no write of any kind.
    /// </summary>
    public IReadOnlyList<string> ListReachableRelics(string worldId, string entityId, string sectorId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var playerStr = ReadWorldPlayerUnlocked(db, tx, worldId)?.ToString();
            if (playerStr is null)
                return Array.Empty<string>();
            return ListReachableRelicsUnlocked(db, tx, worldId, entityId, sectorId, playerStr);
        }
    }
}
