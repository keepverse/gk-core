using FusionRpg.Contracts;
using FusionRpg.Data.Policies;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

public sealed partial class RpgStore
{
    public StorageSummaryDto GetStorageSummary()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            var summary = new StorageSummaryDto();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM archive_catalog;";
                summary.ArchiveCount = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COUNT(*) FROM runs r
                    WHERE r.ended_utc IS NOT NULL AND r.ended_utc != ''
                      AND (
                        EXISTS (SELECT 1 FROM events e WHERE e.run_id = r.id)
                        OR EXISTS (SELECT 1 FROM spawn_stats s WHERE s.run_id = r.id)
                        OR EXISTS (SELECT 1 FROM entities n WHERE n.run_id = r.id)
                        OR EXISTS (SELECT 1 FROM mowers m WHERE m.run_id = r.id)
                      );
                    """;
                summary.ClosedRunsStillHot = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM runs WHERE ended_utc IS NULL OR ended_utc = '';";
                summary.OpenRuns = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT EXISTS(
                      SELECT 1 FROM pvz_activity_facts
                      GROUP BY player_id
                      HAVING COUNT(*) > $n
                    );
                    """;
                cmd.Parameters.AddWithValue("$n", SealedCompactionPolicy.ActivityRetainTail);
                summary.ActivityOverTail = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
            }
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT EXISTS(
                      SELECT 1 FROM rpg_xp_ledger
                      GROUP BY save_id, empire_id, kind, type_id
                      HAVING COUNT(*) > $n
                    );
                    """;
                cmd.Parameters.AddWithValue("$n", SealedCompactionPolicy.XpRetainTailPerActor);
                summary.XpOverTail = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
            }
            return summary;
        }
    }

    public StoragePurgeResultDto DeleteArchives(IReadOnlyList<string> uris)
    {
        RequireFileArchive();
        var result = new StoragePurgeResultDto();
        if (uris.Count == 0)
            return result;

        lock (_gate)
        {
            using var dbCheck = OpenUnlocked();
            if (HasAnyActiveBoundUnlocked(dbCheck))
            {
                result.Refused = uris.Count;
                result.Reason = "unique.active_bound";
                return result;
            }
            SqliteConnection.ClearAllPools();
            using var db = OpenUnlocked();
            Exec(db, "BEGIN IMMEDIATE;");
            try
            {
                foreach (var raw in uris)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        result.Refused++;
                        continue;
                    }
                    var uri = raw.Trim().Replace('\\', '/');
                    var abs = TryResolveSafeArchivePath(uri);
                    if (abs is null)
                    {
                        result.Refused++;
                        continue;
                    }

                    try
                    {
                        if (!TryDeleteArchiveFile(abs))
                        {
                            result.Refused++;
                            continue;
                        }
                    }
                    catch
                    {
                        result.Refused++;
                        continue;
                    }

                    using (var cmd = db.CreateCommand())
                    {
                        cmd.CommandText = "DELETE FROM archive_catalog WHERE uri=$u;";
                        cmd.Parameters.AddWithValue("$u", uri);
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = db.CreateCommand())
                    {
                        cmd.CommandText = "UPDATE runs SET archive_uri=NULL WHERE archive_uri=$u;";
                        cmd.Parameters.AddWithValue("$u", uri);
                        cmd.ExecuteNonQuery();
                    }
                    result.Deleted++;
                }
                Exec(db, "COMMIT;");
            }
            catch
            {
                try { Exec(db, "ROLLBACK;"); } catch { /* ignore */ }
                throw;
            }
        }
        return result;
    }

    public StoragePurgeResultDto PurgeClosedRunCapture(IReadOnlyList<long> runIds)
    {
        RequireFileArchive();
        var result = new StoragePurgeResultDto();
        if (runIds.Count == 0)
            return result;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (HasAnyActiveBoundUnlocked(db))
            {
                result.Refused = runIds.Distinct().Count();
                result.Reason = "unique.active_bound";
                return result;
            }
            Exec(db, "BEGIN IMMEDIATE;");
            try
            {
                foreach (var runId in runIds.Distinct())
                {
                    if (!TryGetRunEnded(db, runId, out var ended) || string.IsNullOrWhiteSpace(ended))
                    {
                        result.Refused++;
                        continue;
                    }
                    DeleteHotCaptureForRunUnlocked(db, runId);
                    result.Deleted++;
                }
                Exec(db, "COMMIT;");
            }
            catch
            {
                try { Exec(db, "ROLLBACK;"); } catch { /* ignore */ }
                throw;
            }
        }
        return result;
    }

    public StoragePurgeResultDto DeleteClosedRuns(IReadOnlyList<long> runIds)
    {
        RequireFileArchive();
        var result = new StoragePurgeResultDto();
        if (runIds.Count == 0)
            return result;

        lock (_gate)
        {
            using var dbProbe = OpenUnlocked();
            if (HasAnyActiveBoundUnlocked(dbProbe))
            {
                result.Refused = runIds.Distinct().Count();
                result.Reason = "unique.active_bound";
                return result;
            }
            SqliteConnection.ClearAllPools();
            using var db = OpenUnlocked();
            Exec(db, "BEGIN IMMEDIATE;");
            try
            {
                foreach (var runId in runIds.Distinct())
                {
                    if (!TryGetRunEnded(db, runId, out var ended) || string.IsNullOrWhiteSpace(ended))
                    {
                        result.Refused++;
                        continue;
                    }

                    string? archiveUri = null;
                    using (var cmd = db.CreateCommand())
                    {
                        cmd.CommandText = "SELECT archive_uri FROM runs WHERE id=$id;";
                        cmd.Parameters.AddWithValue("$id", runId);
                        var v = cmd.ExecuteScalar();
                        archiveUri = v is null or DBNull ? null : Convert.ToString(v);
                    }

                    DeleteHotCaptureForRunUnlocked(db, runId);
                    ExecParam(db, "DELETE FROM runs WHERE id=$id;", "$id", runId);

                    if (!string.IsNullOrWhiteSpace(archiveUri))
                    {
                        using (var cmd = db.CreateCommand())
                        {
                            cmd.CommandText = "DELETE FROM archive_catalog WHERE uri=$u OR run_id=$r;";
                            cmd.Parameters.AddWithValue("$u", archiveUri);
                            cmd.Parameters.AddWithValue("$r", runId);
                            cmd.ExecuteNonQuery();
                        }
                        var abs = TryResolveSafeArchivePath(archiveUri);
                        if (abs is not null)
                            TryDeleteArchiveFile(abs);
                    }
                    else
                    {
                        using (var cmd = db.CreateCommand())
                        {
                            cmd.CommandText = "DELETE FROM archive_catalog WHERE run_id=$r;";
                            cmd.Parameters.AddWithValue("$r", runId);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    result.Deleted++;
                }
                Exec(db, "COMMIT;");
            }
            catch
            {
                try { Exec(db, "ROLLBACK;"); } catch { /* ignore */ }
                throw;
            }
        }
        return result;
    }

    public void TrimHotTailsNow() => CompactAfterRunClosed(null);

    /// <summary>Archive path must resolve under ArchiveDir (no escape).</summary>
    string? TryResolveSafeArchivePath(string uri)
    {
        var candidate = ResolveArchiveAbsPath(uri);
        if (string.IsNullOrEmpty(candidate))
            return null;
        var full = Path.GetFullPath(candidate);
        var root = Path.GetFullPath(ArchiveDir);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return null;
        return full;
    }

    /// <summary>Delete archive sqlite + wal/shm. Missing file is OK. Returns false on IO failure of main file.</summary>
    static bool TryDeleteArchiveFile(string abs)
    {
        try
        {
            if (File.Exists(abs))
                File.Delete(abs);
        }
        catch
        {
            return false;
        }
        foreach (var side in new[] { abs + "-wal", abs + "-shm" })
        {
            try { if (File.Exists(side)) File.Delete(side); } catch { /* ignore */ }
        }
        return true;
    }

    static bool TryGetRunEnded(SqliteConnection db, long runId, out string? endedUtc)
    {
        endedUtc = null;
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT ended_utc FROM runs WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", runId);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return false;
        endedUtc = r.IsDBNull(0) ? null : r.GetString(0);
        return true;
    }
}
