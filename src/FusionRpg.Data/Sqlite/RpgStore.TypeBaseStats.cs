using System.Text.Json;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>One type's base stat row, exactly as the game's own static table defines it.</summary>
public sealed class TypeBaseStatsDto
{
    public string Side { get; set; } = "";
    public int TypeId { get; set; }
    public string? TypeName { get; set; }
    public string StatsJson { get; set; } = "";
    public string CapturedUtc { get; set; } = "";
}

public sealed partial class RpgStore
{
    /// <summary>
    /// Ingests a <c>catalog.basestats</c> chunk from the injector's base-stat sweep.
    ///
    /// <para>Mirrors <c>ProjectRecipes</c>: tolerant of a malformed payload (log and drop rather than
    /// throw, because this runs on the ingest path and one bad chunk must not stall the queue), and
    /// idempotent, because the sweep is re-runnable by hand and a second run must produce the same
    /// table rather than duplicate rows.</para>
    /// </summary>
    void ProjectBaseStats(SqliteConnection db, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                Console.WriteLine("[basestats] ProjectBaseStats: payload has no 'entries' array — dropped: " +
                    (payload.Length > 200 ? payload[..200] : payload));
                return;
            }

            var nowUtc = ServerClock.UtcNowDateTime.ToString("o");
            var written = 0;
            var skipped = 0;

            foreach (var item in entries.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { skipped++; continue; }

                if (!item.TryGetProperty("side", out var sideEl) || sideEl.ValueKind != JsonValueKind.String)
                { skipped++; continue; }
                if (!item.TryGetProperty("typeId", out var typeEl) || !typeEl.TryGetInt32(out var typeId))
                { skipped++; continue; }

                var side = NormSide(sideEl.GetString() ?? "");
                if (side != "plant" && side != "zombie") { skipped++; continue; }

                string? typeName = item.TryGetProperty("typeName", out var tn) && tn.ValueKind == JsonValueKind.String
                    ? tn.GetString()
                    : null;

                // The whole row is stored as JSON rather than as columns. The game's field set differs
                // per side (only a zombie has armour and a summon tier) and may grow with a game update;
                // a column per field would mean a migration every time the game changes, for a table
                // whose only consumers read named keys out of it anyway.
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO type_base_stats(side, type_id, type_name, stats_json, captured_utc)
                    VALUES($side,$type,$tn,$json,$t)
                    ON CONFLICT(side, type_id) DO UPDATE SET
                      type_name=excluded.type_name,
                      stats_json=excluded.stats_json,
                      captured_utc=excluded.captured_utc;
                    """;
                cmd.Parameters.AddWithValue("$side", side);
                cmd.Parameters.AddWithValue("$type", typeId);
                cmd.Parameters.AddWithValue("$tn", (object?)typeName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$json", item.GetRawText());
                cmd.Parameters.AddWithValue("$t", nowUtc);
                cmd.ExecuteNonQuery();
                written++;
            }

            Console.WriteLine($"[basestats] projected {written} rows" + (skipped > 0 ? $", {skipped} skipped" : ""));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[basestats] ProjectBaseStats failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Every captured base-stat row, ordered deterministically so a dump of this table is byte-stable
    /// across runs — the same contract `ListSpawnBaselines` holds for the spawn-sampled twin.
    /// </summary>
    public List<TypeBaseStatsDto> ListTypeBaseStats(string? side = null)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            if (string.IsNullOrWhiteSpace(side))
            {
                cmd.CommandText =
                    "SELECT side, type_id, type_name, stats_json, captured_utc FROM type_base_stats " +
                    "ORDER BY side, type_id;";
            }
            else
            {
                cmd.CommandText =
                    "SELECT side, type_id, type_name, stats_json, captured_utc FROM type_base_stats " +
                    "WHERE side=$side ORDER BY type_id;";
                cmd.Parameters.AddWithValue("$side", NormSide(side));
            }

            var rows = new List<TypeBaseStatsDto>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new TypeBaseStatsDto
                {
                    Side = r.GetString(0),
                    TypeId = r.GetInt32(1),
                    TypeName = r.IsDBNull(2) ? null : r.GetString(2),
                    StatsJson = r.GetString(3),
                    CapturedUtc = r.GetString(4),
                });
            }
            return rows;
        }
    }

    /// <summary>
    /// The base-stat table in the shape <see cref="LoadCombatBaselinesUnlocked"/> returns, so
    /// <c>ResolveCombatBaseline</c> can prefer it over a spawn sample without knowing which it holds.
    /// </summary>
    static Dictionary<(string Side, int TypeId), (string StatsJson, string CapturedUtc)>
        LoadTypeBaseStatsUnlocked(SqliteConnection db, SqliteTransaction? tx)
    {
        var result = new Dictionary<(string, int), (string, string)>();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT side, type_id, stats_json, captured_utc FROM type_base_stats;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result[(r.GetString(0), r.GetInt32(1))] = (r.GetString(2), r.GetString(3));
        return result;
    }
}
