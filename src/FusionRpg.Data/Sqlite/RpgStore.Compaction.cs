using FusionRpg.Data.Abstractions;
using FusionRpg.Data.Policies;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

public sealed partial class RpgStore
{
    /// <summary>Cold-move closed-run capture to <c>archive/</c>; refuse open runs.</summary>
    public string? PromoteClosedRunCapture(long runId)
    {
        RequireFileArchive();
        lock (_gate)
            return PromoteClosedRunCaptureCore(runId);
    }

    public IReadOnlyList<ColdArchiveEntry> ListArchiveCatalog()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT uri, kind, run_id, created_utc
                FROM archive_catalog
                ORDER BY id ASC;
                """;
            var list = new List<ColdArchiveEntry>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new ColdArchiveEntry(
                    r.GetString(0),
                    r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetInt64(2),
                    r.GetString(3)));
            }
            return list;
        }
    }

    public void CompactAfterRunClosed(long? closedRunId)
    {
        RequireFileArchive();
        lock (_gate)
        {
            EnforceKeepLastNCore();
            TrimActivityTailsCore();
            TrimXpTailsCore();
            TrimSoulTailsCore();
        }
    }

    /// <summary>D5: soul-ledger tail-trim (XP-ledger pattern). Only rows the watermarked balance
    /// already covers trim, so balances never change; the overflow lands in a segment archive.</summary>
    public void TrimSoulLedgerTails(int? retainOverride = null)
    {
        RequireFileArchive();
        lock (_gate)
        {
            TrimSoulTailsCore(retainOverride);
        }
    }

    void TrimSoulTailsCore(int? retainOverride = null)
    {
        var retain = retainOverride ?? SealedCompactionPolicy.SoulRetainTailPerPlayer;
        List<long> players;
        using (var db = OpenUnlocked())
        {
            players = new List<long>();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT player_id FROM rpg_soul_ledger
                GROUP BY player_id
                HAVING COUNT(*) > $n;
                """;
            cmd.Parameters.AddWithValue("$n", retain);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                players.Add(r.GetInt64(0));
        }

        foreach (var playerId in players)
            TrimSoulPlayerCore(playerId, retain);
    }

    void TrimSoulPlayerCore(long playerId, int retain)
    {
        List<long> ids;
        long through;
        using (var db = OpenUnlocked())
        {
            long total;
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM rpg_soul_ledger WHERE player_id=$p;";
                cmd.Parameters.AddWithValue("$p", playerId);
                total = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }

            var overflow = total - retain;
            if (overflow <= 0)
                return;

            ids = new List<long>((int)overflow);
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT id FROM rpg_soul_ledger
                    WHERE player_id=$p
                    ORDER BY id ASC
                    LIMIT $n;
                    """;
                cmd.Parameters.AddWithValue("$p", playerId);
                cmd.Parameters.AddWithValue("$n", overflow);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    ids.Add(r.GetInt64(0));
            }

            if (ids.Count == 0)
                return;

            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COALESCE(through_ledger_id, 0) FROM rpg_soul_balances
                    WHERE player_id=$p;
                    """;
                cmd.Parameters.AddWithValue("$p", playerId);
                var v = cmd.ExecuteScalar();
                through = v is null or DBNull ? 0L : Convert.ToInt64(v);
            }
        }

        var maxOverflowId = ids[^1];
        if (through < maxOverflowId)
            return; // never trim rows the balance has not folded in

        var minId = ids[0];
        Directory.CreateDirectory(ArchiveDir);
        var fileName = $"souls-a{playerId}-{minId}-{maxOverflowId}.sqlite";
        var absPath = Path.Combine(ArchiveDir, fileName);
        var relUri = Path.Combine("archive", fileName).Replace('\\', '/');
        if (File.Exists(absPath))
            File.Delete(absPath);

        var idList = string.Join(",", ids);
        WriteSegmentArchiveFile(absPath, "rpg_soul_ledger", idList, ids.Count);

        using (var db = OpenUnlocked())
        {
            Exec(db, "BEGIN IMMEDIATE;");
            try
            {
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = """
                        SELECT COALESCE(through_ledger_id, 0) FROM rpg_soul_balances
                        WHERE player_id=$p;
                        """;
                    cmd.Parameters.AddWithValue("$p", playerId);
                    var v = cmd.ExecuteScalar();
                    var cover = v is null or DBNull ? 0L : Convert.ToInt64(v);
                    if (cover < maxOverflowId)
                    {
                        Exec(db, "ROLLBACK;");
                        try { File.Delete(absPath); } catch { /* ignore */ }
                        return;
                    }
                }

                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = """
                        INSERT INTO archive_catalog(uri, kind, run_id, player_id, created_utc, meta_json)
                        VALUES($u, 'souls', NULL, $p, $t, $m);
                        """;
                    cmd.Parameters.AddWithValue("$u", relUri);
                    cmd.Parameters.AddWithValue("$p", playerId);
                    cmd.Parameters.AddWithValue("$t", ServerClock.UtcNowDateTime.ToString("o"));
                    cmd.Parameters.AddWithValue("$m",
                        $"{{\"minId\":{minId},\"maxId\":{maxOverflowId},\"count\":{ids.Count}}}");
                    cmd.ExecuteNonQuery();
                }

                Exec(db, $"DELETE FROM rpg_soul_ledger WHERE id IN ({idList});");
                Exec(db, "COMMIT;");
            }
            catch
            {
                try { Exec(db, "ROLLBACK;"); } catch { /* ignore */ }
                try { File.Delete(absPath); } catch { /* ignore */ }
                throw;
            }
        }
    }

    string? PromoteClosedRunCaptureCore(long runId)
    {
        string? endedUtc;
        string? existingUri;
        long? playerId;
        long hotEvents;
        long hotSpawn;
        long hotEntities;
        long hotMowers;

        using (var db = OpenUnlocked())
        {
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT ended_utc, archive_uri, player_id FROM runs WHERE id=$id;";
                cmd.Parameters.AddWithValue("$id", runId);
                using var r = cmd.ExecuteReader();
                if (!r.Read())
                    return null;
                endedUtc = r.IsDBNull(0) ? null : r.GetString(0);
                existingUri = r.IsDBNull(1) ? null : r.GetString(1);
                playerId = r.IsDBNull(2) ? null : r.GetInt64(2);
            }

            if (string.IsNullOrWhiteSpace(endedUtc))
                return null;

            if (!string.IsNullOrWhiteSpace(existingUri))
            {
                var existingAbs = ResolveArchiveAbsPath(existingUri);
                if (!string.IsNullOrEmpty(existingAbs) && File.Exists(existingAbs))
                {
                    Exec(db, "BEGIN IMMEDIATE;");
                    try
                    {
                        DeleteHotCaptureForRunUnlocked(db, runId);
                        Exec(db, "COMMIT;");
                    }
                    catch
                    {
                        try { Exec(db, "ROLLBACK;"); } catch { /* ignore */ }
                        throw;
                    }
                    return existingUri;
                }

                // URI set but file missing — clear URI/catalog and fall through to re-archive.
                Exec(db, "BEGIN IMMEDIATE;");
                try
                {
                    using (var cmd = db.CreateCommand())
                    {
                        cmd.CommandText = "UPDATE runs SET archive_uri=NULL WHERE id=$id;";
                        cmd.Parameters.AddWithValue("$id", runId);
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = db.CreateCommand())
                    {
                        cmd.CommandText = "DELETE FROM archive_catalog WHERE uri=$u;";
                        cmd.Parameters.AddWithValue("$u", existingUri);
                        cmd.ExecuteNonQuery();
                    }
                    Exec(db, "COMMIT;");
                }
                catch
                {
                    try { Exec(db, "ROLLBACK;"); } catch { /* ignore */ }
                    throw;
                }
                existingUri = null;
            }

            hotEvents = CountForRun(db, "events", runId);
            hotSpawn = CountForRun(db, "spawn_stats", runId);
            hotEntities = CountForRun(db, "entities", runId);
            hotMowers = CountForRun(db, "mowers", runId);
        }

        Directory.CreateDirectory(ArchiveDir);
        var fileName = $"capture-run-{runId}.sqlite";
        var absPath = Path.Combine(ArchiveDir, fileName);
        var relUri = Path.Combine("archive", fileName).Replace('\\', '/');
        if (File.Exists(absPath))
            File.Delete(absPath);

        WriteCaptureArchiveFile(absPath, runId, hotEvents, hotSpawn, hotEntities, hotMowers);

        using (var db = OpenUnlocked())
        {
            Exec(db, "BEGIN IMMEDIATE;");
            try
            {
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = "SELECT ended_utc, archive_uri FROM runs WHERE id=$id;";
                    cmd.Parameters.AddWithValue("$id", runId);
                    using var r = cmd.ExecuteReader();
                    if (!r.Read() || r.IsDBNull(0) || string.IsNullOrWhiteSpace(r.GetString(0)))
                    {
                        Exec(db, "ROLLBACK;");
                        try { File.Delete(absPath); } catch { /* ignore */ }
                        return null;
                    }
                    if (!r.IsDBNull(1) && !string.IsNullOrWhiteSpace(r.GetString(1)))
                    {
                        var uri = r.GetString(1);
                        DeleteHotCaptureForRunUnlocked(db, runId);
                        Exec(db, "COMMIT;");
                        try { File.Delete(absPath); } catch { /* ignore */ }
                        return uri;
                    }
                }

                var created = ServerClock.UtcNowDateTime.ToString("o");
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = "UPDATE runs SET archive_uri=$u WHERE id=$id;";
                    cmd.Parameters.AddWithValue("$u", relUri);
                    cmd.Parameters.AddWithValue("$id", runId);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = """
                        INSERT INTO archive_catalog(uri, kind, run_id, player_id, created_utc, meta_json)
                        VALUES($u, 'capture', $r, $p, $t, $m);
                        """;
                    cmd.Parameters.AddWithValue("$u", relUri);
                    cmd.Parameters.AddWithValue("$r", runId);
                    cmd.Parameters.AddWithValue("$p", (object?)playerId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$t", created);
                    cmd.Parameters.AddWithValue("$m",
                        $"{{\"events\":{hotEvents},\"spawn_stats\":{hotSpawn},\"entities\":{hotEntities},\"mowers\":{hotMowers}}}");
                    cmd.ExecuteNonQuery();
                }

                DeleteHotCaptureForRunUnlocked(db, runId);
                Exec(db, "COMMIT;");
                return relUri;
            }
            catch
            {
                try { Exec(db, "ROLLBACK;"); } catch { /* ignore */ }
                try { File.Delete(absPath); } catch { /* ignore */ }
                throw;
            }
        }
    }

    void WriteCaptureArchiveFile(string absPath, long runId, long events, long spawn, long entities, long mowers)
    {
        using var cold = SqliteConnectionFactory.Open(absPath);
        using (var cmd = cold.CreateCommand())
        {
            cmd.CommandText = "ATTACH DATABASE $p AS hot;";
            cmd.Parameters.AddWithValue("$p", _hotPath);
            cmd.ExecuteNonQuery();
        }
        try
        {
            Exec(cold, $"""
                CREATE TABLE events AS SELECT * FROM hot.events WHERE run_id={runId};
                CREATE TABLE spawn_stats AS SELECT * FROM hot.spawn_stats WHERE run_id={runId};
                CREATE TABLE entities AS SELECT * FROM hot.entities WHERE run_id={runId};
                CREATE TABLE mowers AS SELECT * FROM hot.mowers WHERE run_id={runId};
                """);
            if (CountTable(cold, "events") != events
                || CountTable(cold, "spawn_stats") != spawn
                || CountTable(cold, "entities") != entities
                || CountTable(cold, "mowers") != mowers)
                throw new InvalidOperationException($"Archive verify failed for run {runId}.");
            try { Exec(cold, "PRAGMA wal_checkpoint(TRUNCATE);"); } catch { /* ignore */ }
        }
        finally
        {
            try { Exec(cold, "DETACH DATABASE hot;"); } catch { /* ignore */ }
        }
    }

    void EnforceKeepLastNCore()
    {
        var limit = SealedCompactionPolicy.KeepLastNFullCaptureRuns;
        // Web-mode (webrpg) runs are exempt from capture KeepLastN/archiving: their events
        // replay from the logged seed, and expedition volume would otherwise evict real PvZ
        // capture runs (audit 2026-08-21). Eligible ids are fetched ONCE — the old
        // count-then-oldest loop re-scanned `runs` per promoted run (review I5).
        List<long> promote;
        using (var db = OpenUnlocked())
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT id FROM runs
                WHERE ended_utc IS NOT NULL AND (archive_uri IS NULL OR archive_uri = '')
                  AND (game IS NULL OR game LIKE 'pvzrh%')
                ORDER BY id ASC;
                """;
            var eligible = new List<long>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                eligible.Add(r.GetInt64(0));
            if (eligible.Count <= limit)
                return;
            promote = eligible.Take(eligible.Count - limit).ToList();
        }

        foreach (var runId in promote)
        {
            if (PromoteClosedRunCaptureCore(runId) is null)
                return;
        }
    }

    void TrimActivityTailsCore()
    {
        var retain = SealedCompactionPolicy.ActivityRetainTail;
        List<long> players;
        using (var db = OpenUnlocked())
        {
            players = new List<long>();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT player_id FROM pvz_activity_facts
                GROUP BY player_id
                HAVING COUNT(*) > $n;
                """;
            cmd.Parameters.AddWithValue("$n", retain);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                players.Add(r.GetInt64(0));
        }

        foreach (var playerId in players)
            TrimActivityPlayerCore(playerId, retain);
    }

    void TrimActivityPlayerCore(long playerId, int retain)
    {
        List<long> ids;
        long through;
        using (var db = OpenUnlocked())
        {
            long total;
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pvz_activity_facts WHERE player_id=$p;";
                cmd.Parameters.AddWithValue("$p", playerId);
                total = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            var overflow = total - retain;
            if (overflow <= 0)
                return;

            ids = new List<long>((int)overflow);
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT id FROM pvz_activity_facts
                    WHERE player_id=$p
                    ORDER BY id ASC
                    LIMIT $n;
                    """;
                cmd.Parameters.AddWithValue("$p", playerId);
                cmd.Parameters.AddWithValue("$n", overflow);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    ids.Add(r.GetInt64(0));
            }
            if (ids.Count == 0)
                return;

            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT COALESCE(through_fact_id, 0) FROM pvz_activity_rollups WHERE player_id=$p;";
                cmd.Parameters.AddWithValue("$p", playerId);
                var v = cmd.ExecuteScalar();
                through = v is null or DBNull ? 0L : Convert.ToInt64(v);
            }
        }

        var maxOverflowId = ids[^1];
        if (through < maxOverflowId)
            return;

        var minId = ids[0];
        var maxId = maxOverflowId;
        Directory.CreateDirectory(ArchiveDir);
        var fileName = $"activity-p{playerId}-{minId}-{maxId}.sqlite";
        var absPath = Path.Combine(ArchiveDir, fileName);
        var relUri = Path.Combine("archive", fileName).Replace('\\', '/');
        if (File.Exists(absPath))
            File.Delete(absPath);

        var idList = string.Join(",", ids);
        WriteSegmentArchiveFile(absPath, "pvz_activity_facts", idList, ids.Count);

        using (var db = OpenUnlocked())
        {
            Exec(db, "BEGIN IMMEDIATE;");
            try
            {
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = "SELECT COALESCE(through_fact_id, 0) FROM pvz_activity_rollups WHERE player_id=$p;";
                    cmd.Parameters.AddWithValue("$p", playerId);
                    var v = cmd.ExecuteScalar();
                    var cover = v is null or DBNull ? 0L : Convert.ToInt64(v);
                    if (cover < maxOverflowId)
                    {
                        Exec(db, "ROLLBACK;");
                        try { File.Delete(absPath); } catch { /* ignore */ }
                        return;
                    }
                }

                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = """
                        INSERT INTO archive_catalog(uri, kind, run_id, player_id, created_utc, meta_json)
                        VALUES($u, 'activity', NULL, $p, $t, $m);
                        """;
                    cmd.Parameters.AddWithValue("$u", relUri);
                    cmd.Parameters.AddWithValue("$p", playerId);
                    cmd.Parameters.AddWithValue("$t", ServerClock.UtcNowDateTime.ToString("o"));
                    cmd.Parameters.AddWithValue("$m", $"{{\"minId\":{minId},\"maxId\":{maxId},\"count\":{ids.Count}}}");
                    cmd.ExecuteNonQuery();
                }

                Exec(db, $"DELETE FROM pvz_activity_facts WHERE id IN ({idList});");
                Exec(db, "COMMIT;");
            }
            catch
            {
                try { Exec(db, "ROLLBACK;"); } catch { /* ignore */ }
                try { File.Delete(absPath); } catch { /* ignore */ }
                throw;
            }
        }
    }

    void TrimXpTailsCore()
    {
        var retain = SealedCompactionPolicy.XpRetainTailPerActor;
        List<(long PlayerId, string EmpireId, string Kind, int TypeId)> actors;
        using (var db = OpenUnlocked())
        {
            actors = new List<(long, string, string, int)>();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT save_id, empire_id, kind, type_id FROM rpg_xp_ledger
                GROUP BY save_id, empire_id, kind, type_id
                HAVING COUNT(*) > $n;
                """;
            cmd.Parameters.AddWithValue("$n", retain);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                actors.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));
        }

        foreach (var a in actors)
            TrimXpActorCore(a.PlayerId, a.EmpireId, a.Kind, a.TypeId, retain);
    }

    void TrimXpActorCore(long playerId, string empireId, string kind, int typeId, int retain)
    {
        List<long> ids;
        long through;
        using (var db = OpenUnlocked())
        {
            long total;
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COUNT(*) FROM rpg_xp_ledger
                    WHERE save_id=$p AND empire_id=$e AND kind=$k AND type_id=$t;
                    """;
                cmd.Parameters.AddWithValue("$p", playerId);
                cmd.Parameters.AddWithValue("$e", empireId);
                cmd.Parameters.AddWithValue("$k", kind);
                cmd.Parameters.AddWithValue("$t", typeId);
                total = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            var overflow = total - retain;
            if (overflow <= 0)
                return;

            ids = new List<long>((int)overflow);
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT id FROM rpg_xp_ledger
                    WHERE save_id=$p AND empire_id=$e AND kind=$k AND type_id=$t
                    ORDER BY id ASC
                    LIMIT $n;
                    """;
                cmd.Parameters.AddWithValue("$p", playerId);
                cmd.Parameters.AddWithValue("$e", empireId);
                cmd.Parameters.AddWithValue("$k", kind);
                cmd.Parameters.AddWithValue("$t", typeId);
                cmd.Parameters.AddWithValue("$n", overflow);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    ids.Add(r.GetInt64(0));
            }
            if (ids.Count == 0)
                return;

            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COALESCE(through_ledger_id, 0) FROM rpg_actor_progression
                    WHERE save_id=$p AND empire_id=$e AND kind=$k AND type_id=$t;
                    """;
                cmd.Parameters.AddWithValue("$p", playerId);
                cmd.Parameters.AddWithValue("$e", empireId);
                cmd.Parameters.AddWithValue("$k", kind);
                cmd.Parameters.AddWithValue("$t", typeId);
                var v = cmd.ExecuteScalar();
                through = v is null or DBNull ? 0L : Convert.ToInt64(v);
            }
        }

        var maxOverflowId = ids[^1];
        if (through < maxOverflowId)
            return;

        var minId = ids[0];
        var maxId = maxOverflowId;
        Directory.CreateDirectory(ArchiveDir);
        var fileName = $"xp-a{playerId}-{kind}-{typeId}-{minId}-{maxId}.sqlite";
        var absPath = Path.Combine(ArchiveDir, fileName);
        var relUri = Path.Combine("archive", fileName).Replace('\\', '/');
        if (File.Exists(absPath))
            File.Delete(absPath);

        var idList = string.Join(",", ids);
        WriteSegmentArchiveFile(absPath, "rpg_xp_ledger", idList, ids.Count);

        using (var db = OpenUnlocked())
        {
            Exec(db, "BEGIN IMMEDIATE;");
            try
            {
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = """
                        SELECT COALESCE(through_ledger_id, 0) FROM rpg_actor_progression
                        WHERE save_id=$p AND empire_id=$e AND kind=$k AND type_id=$t;
                        """;
                    cmd.Parameters.AddWithValue("$p", playerId);
                    cmd.Parameters.AddWithValue("$e", empireId);
                    cmd.Parameters.AddWithValue("$k", kind);
                    cmd.Parameters.AddWithValue("$t", typeId);
                    var v = cmd.ExecuteScalar();
                    var cover = v is null or DBNull ? 0L : Convert.ToInt64(v);
                    if (cover < maxOverflowId)
                    {
                        Exec(db, "ROLLBACK;");
                        try { File.Delete(absPath); } catch { /* ignore */ }
                        return;
                    }
                }

                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = """
                        INSERT INTO archive_catalog(uri, kind, run_id, player_id, created_utc, meta_json)
                        VALUES($u, 'xp', NULL, $p, $t, $m);
                        """;
                    cmd.Parameters.AddWithValue("$u", relUri);
                    cmd.Parameters.AddWithValue("$p", playerId);
                    cmd.Parameters.AddWithValue("$t", ServerClock.UtcNowDateTime.ToString("o"));
                    cmd.Parameters.AddWithValue("$m",
                        $"{{\"kind\":\"{kind}\",\"typeId\":{typeId},\"minId\":{minId},\"maxId\":{maxId},\"count\":{ids.Count}}}");
                    cmd.ExecuteNonQuery();
                }

                Exec(db, $"DELETE FROM rpg_xp_ledger WHERE id IN ({idList});");
                Exec(db, "COMMIT;");
            }
            catch
            {
                try { Exec(db, "ROLLBACK;"); } catch { /* ignore */ }
                try { File.Delete(absPath); } catch { /* ignore */ }
                throw;
            }
        }
    }

    void WriteSegmentArchiveFile(string absPath, string table, string idList, int expectedCount)
    {
        using var cold = SqliteConnectionFactory.Open(absPath);
        using (var cmd = cold.CreateCommand())
        {
            cmd.CommandText = "ATTACH DATABASE $p AS hot;";
            cmd.Parameters.AddWithValue("$p", _hotPath);
            cmd.ExecuteNonQuery();
        }
        try
        {
            Exec(cold, $"CREATE TABLE {table} AS SELECT * FROM hot.{table} WHERE id IN ({idList});");
            if (CountTable(cold, table) != expectedCount)
                throw new InvalidOperationException($"Archive verify failed for {table} ({expectedCount} rows).");
            try { Exec(cold, "PRAGMA wal_checkpoint(TRUNCATE);"); } catch { /* ignore */ }
        }
        finally
        {
            try { Exec(cold, "DETACH DATABASE hot;"); } catch { /* ignore */ }
        }
    }

    string? ResolveArchiveAbsPath(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;
        if (Path.IsPathRooted(uri))
            return uri;
        return Path.GetFullPath(Path.Combine(_dataDir, uri.Replace('/', Path.DirectorySeparatorChar)));
    }

    static void DeleteHotCaptureForRunUnlocked(SqliteConnection db, long runId)
    {
        ExecParam(db, "DELETE FROM events WHERE run_id=$id;", "$id", runId);
        ExecParam(db, "DELETE FROM spawn_stats WHERE run_id=$id;", "$id", runId);
        ExecParam(db, "DELETE FROM entities WHERE run_id=$id;", "$id", runId);
        ExecParam(db, "DELETE FROM mowers WHERE run_id=$id;", "$id", runId);
    }

    static long CountForRun(SqliteConnection db, string table, long runId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE run_id=$r;";
        cmd.Parameters.AddWithValue("$r", runId);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    static long CountTable(SqliteConnection db, string table)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }
}
