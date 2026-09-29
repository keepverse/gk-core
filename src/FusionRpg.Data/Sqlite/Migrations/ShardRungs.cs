using FusionRpg.Core.Creatures;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data.Sqlite.Migrations;

/// <summary>
/// seed-to-concrete T4.1 (spec-rarity-migration.md §4, §7 step 6): the ten-rung rarity ladder renamed
/// four shard materials — <c>shard.common</c> → <c>shard.chaff</c>, <c>shard.rare</c> →
/// <c>shard.cultivated</c>, <c>shard.epic</c> → <c>shard.heirloom</c>, <c>shard.legendary</c> →
/// <c>shard.sunwoven</c> (the band's lowest rung, per <c>ssot-rarity.md</c> §4.3's own map — no player
/// gains value from the rename). Those ids are stored in <c>rpg_creature_materials</c> rows, so a rename
/// without a data step orphans every owned stack. This migration rewrites them, merging into the live
/// id when a player already holds both — <b>no player ends the migration with fewer materials than
/// they started with.</b>
///
/// One-shot and idempotent by construction: after running once, no <c>rpg_creature_materials</c> row uses
/// a legacy id, so a second run's <c>SELECT</c> finds nothing and touches zero rows. Runs inside
/// <see cref="RpgStore.Init"/>, immediately after <c>rpg_creature_materials</c>'s own
/// <c>CREATE TABLE IF NOT EXISTS</c>, so it applies to every store on every boot rather than needing a
/// separate opt-in step.
///
/// The four legacy ids stay <b>resolvable but unissuable</b> for one release
/// (<see cref="CreatureMaterialCatalog.LegacyIds"/>) — this migration only rewrites what a player already
/// owns; it does not need to reject the old ids, that is the catalog's job.
/// </summary>
public static class ShardRungs
{
    /// <summary>"shard.common" -> "shard.chaff" etc., built from the one forward map
    /// (<see cref="LegacyCreatureRarityIds.ForwardMap"/>) so this migration can never drift from the
    /// catalog's own legacy/live id pairing.</summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyToLiveShardId =
        LegacyCreatureRarityIds.ForwardMap.ToDictionary(
            kv => "shard." + kv.Key,
            kv => "shard." + kv.Value.ToId());

    /// <summary>
    /// Rewrites every owned legacy shard stack to its live id, summing quantities where a player holds
    /// both (never overwriting). Returns the number of (player, legacy id) rows migrated, for logging
    /// and tests — 0 on a clean or already-migrated store.
    /// </summary>
    public static int Migrate(SqliteConnection db, TextWriter? log = null)
    {
        var legacyIds = LegacyToLiveShardId.Keys.ToArray();
        var legacyRows = new List<(long PlayerId, string LegacyId, long Qty)>();
        using (var select = db.CreateCommand())
        {
            select.CommandText =
                $"SELECT player_id, material_id, qty FROM rpg_creature_materials " +
                $"WHERE material_id IN ({string.Join(",", legacyIds.Select((_, i) => "$id" + i))});";
            for (var i = 0; i < legacyIds.Length; i++)
                select.Parameters.AddWithValue("$id" + i, legacyIds[i]);
            using var r = select.ExecuteReader();
            while (r.Read())
                legacyRows.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2)));
        }

        if (legacyRows.Count == 0)
            return 0;

        var now = ServerClock.UtcNowDateTime.ToString("o");
        using var tx = db.BeginTransaction();
        foreach (var (playerId, legacyId, qty) in legacyRows)
        {
            var liveId = LegacyToLiveShardId[legacyId];

            using (var merge = db.CreateCommand())
            {
                // ON CONFLICT sums into whatever the player already holds under the live id — the
                // both-held case never overwrites, it always adds.
                merge.CommandText = """
                    INSERT INTO rpg_creature_materials(player_id, material_id, qty, updated_utc)
                    VALUES($p,$m,$q,$t)
                    ON CONFLICT(player_id, material_id)
                    DO UPDATE SET qty = qty + $q, updated_utc = $t;
                    """;
                merge.Parameters.AddWithValue("$p", playerId);
                merge.Parameters.AddWithValue("$m", liveId);
                merge.Parameters.AddWithValue("$q", qty);
                merge.Parameters.AddWithValue("$t", now);
                merge.ExecuteNonQuery();
            }

            using (var clear = db.CreateCommand())
            {
                // Zero the legacy row rather than DELETE — the id stays resolvable (spec §4 point 4:
                // "resolvable but unissuable for one release"); a zero qty already reads as "none" to
                // every consumer, since ListCreatureMaterials filters qty > 0.
                clear.CommandText = """
                    UPDATE rpg_creature_materials SET qty = 0, updated_utc = $t
                    WHERE player_id = $p AND material_id = $m;
                    """;
                clear.Parameters.AddWithValue("$t", now);
                clear.Parameters.AddWithValue("$p", playerId);
                clear.Parameters.AddWithValue("$m", legacyId);
                clear.ExecuteNonQuery();
            }
        }
        tx.Commit();

        log?.WriteLine($"[data] ShardRungs migration: rewrote {legacyRows.Count} legacy shard stack(s) to their live ids");
        return legacyRows.Count;
    }
}
