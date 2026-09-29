using FusionRpg.Core.Saves;
using FusionRpg.Data.Notifications;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

public sealed partial class RpgStore
{
    // Structural, not tunable: the widest re-read span of any registered source (cache-notify-source
    // reads [t, t+1]), plus one turn of margin. Changing it changes whether dedup is correct, not how
    // the game feels. A source with a wider window raises it in the same reviewed change.
    const int DedupKeyMemoryWorldTurns = 3;

    static void EnsureNotificationSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_notification (
              seq          INTEGER PRIMARY KEY AUTOINCREMENT,
              save_id      INTEGER NOT NULL,
              dedup_key    TEXT    NOT NULL,
              category     TEXT    NOT NULL,
              severity     TEXT    NOT NULL,
              source_id    TEXT    NOT NULL,
              message_key  TEXT    NOT NULL,
              args_json    TEXT    NOT NULL,
              subject_key  TEXT,
              world_id     TEXT,
              world_turn   INTEGER,
              state        TEXT    NOT NULL DEFAULT 'unread',
              rev          INTEGER NOT NULL,
              created_utc  TEXT    NOT NULL,
              UNIQUE (save_id, dedup_key)
            );
            CREATE INDEX IF NOT EXISTS ix_rpg_notification_save_rev     ON rpg_notification (save_id, rev);
            CREATE INDEX IF NOT EXISTS ix_rpg_notification_save_cat_seq ON rpg_notification (save_id, category, seq);
            CREATE INDEX IF NOT EXISTS ix_rpg_notification_repeat       ON rpg_notification (save_id, category, subject_key, world_turn);

            CREATE TABLE IF NOT EXISTS rpg_notification_save_rev (
              save_id  INTEGER PRIMARY KEY,
              rev      INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS rpg_notification_key (
              save_id     INTEGER NOT NULL,
              dedup_key   TEXT    NOT NULL,
              world_turn  INTEGER NOT NULL,
              PRIMARY KEY (save_id, dedup_key)
            );

            CREATE TABLE IF NOT EXISTS rpg_notification_source_cursor (
              source_id  TEXT    NOT NULL,
              scope_key  TEXT    NOT NULL,
              position   INTEGER NOT NULL,
              PRIMARY KEY (source_id, scope_key)
            );
            """);
    }

    /// <summary>notify-store spec §2, property 1-3. One transaction: per save, ledger
    /// INSERT OR IGNORE decides "new" (row insert is the second line of defence), bump that save's
    /// rev once per inserted row, prune every touched category to its newest
    /// <paramref name="retainPerCategory"/> rows, age the key ledger, then upsert the source cursor
    /// when <paramref name="cursor"/> is set. Returns, per save, only the rows that were inserted AND
    /// survived the prune, in seq order.</summary>
    public IReadOnlyDictionary<SaveId, IReadOnlyList<NotificationRow>> AppendNotificationTurn(
        IReadOnlyList<NotificationSaveAppend> saves, int retainPerCategory, string createdUtc,
        NotificationCursorAdvance? cursor)
    {
        if (saves is null) throw new ArgumentNullException(nameof(saves));
        if (retainPerCategory <= 0) throw new ArgumentOutOfRangeException(nameof(retainPerCategory));

        var result = new Dictionary<SaveId, IReadOnlyList<NotificationRow>>();

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            try
            {
                foreach (var save in saves)
                {
                    var insertedSeqs = new List<long>();
                    var touchedCategories = new HashSet<string>(StringComparer.Ordinal);
                    var maxTurn = 0;

                    foreach (var row in save.Rows)
                    {
                        if (row.WorldTurn is { } t && t > maxTurn) maxTurn = t;

                        if (row.WorldTurn.HasValue)
                        {
                            using var ledger = db.CreateCommand();
                            ledger.Transaction = tx;
                            ledger.CommandText = """
                                INSERT OR IGNORE INTO rpg_notification_key (save_id, dedup_key, world_turn) VALUES ($p, $k, $t);
                                SELECT changes();
                                """;
                            ledger.Parameters.AddWithValue("$p", save.SaveId.Value);
                            ledger.Parameters.AddWithValue("$k", row.DedupKey);
                            ledger.Parameters.AddWithValue("$t", row.WorldTurn.Value);
                            var ledgerChanges = Convert.ToInt64(ledger.ExecuteScalar());
                            // 0 => the key was stored before (even if its row is gone): nothing new.
                            if (ledgerChanges == 0) continue;
                        }

                        var rev = BumpSaveRevUnlocked(db, tx, save.SaveId);

                        using var insert = db.CreateCommand();
                        insert.Transaction = tx;
                        insert.CommandText = """
                            INSERT OR IGNORE INTO rpg_notification
                              (save_id, dedup_key, category, severity, source_id, message_key, args_json,
                               subject_key, world_id, world_turn, rev, created_utc)
                            VALUES ($p, $k, $c, $sev, $src, $m, $a, $subj, $w, $t, $rev, $now);
                            SELECT CASE WHEN changes() = 1 THEN last_insert_rowid() END;
                            """;
                        insert.Parameters.AddWithValue("$p", save.SaveId.Value);
                        insert.Parameters.AddWithValue("$k", row.DedupKey);
                        insert.Parameters.AddWithValue("$c", row.Category);
                        insert.Parameters.AddWithValue("$sev", row.Severity);
                        insert.Parameters.AddWithValue("$src", row.SourceId);
                        insert.Parameters.AddWithValue("$m", row.MessageKey);
                        insert.Parameters.AddWithValue("$a", row.ArgsJson);
                        insert.Parameters.AddWithValue("$subj", (object?)row.SubjectKey ?? DBNull.Value);
                        insert.Parameters.AddWithValue("$w", (object?)row.WorldId ?? DBNull.Value);
                        insert.Parameters.AddWithValue("$t", (object?)row.WorldTurn ?? DBNull.Value);
                        insert.Parameters.AddWithValue("$rev", rev);
                        insert.Parameters.AddWithValue("$now", createdUtc);
                        var seqResult = insert.ExecuteScalar();
                        if (seqResult is null || seqResult is DBNull) continue; // the row already existed (UNIQUE)

                        insertedSeqs.Add(Convert.ToInt64(seqResult));
                        touchedCategories.Add(row.Category);
                    }

                    foreach (var category in touchedCategories)
                    {
                        // Retention tail (AGENTS.md "Caps" exemption: retention tails). Not a
                        // progression ceiling: it bounds history rows, never a magnitude. The depth
                        // is the tunable notification.v1.json retainPerCategory (R-N2), applied per
                        // (save, category) so a noisy category can never evict a quiet one's history.
                        using var prune = db.CreateCommand();
                        prune.Transaction = tx;
                        prune.CommandText = """
                            DELETE FROM rpg_notification
                            WHERE save_id = $p AND category = $c
                              AND seq NOT IN (
                                SELECT seq FROM rpg_notification WHERE save_id = $p AND category = $c
                                ORDER BY seq DESC LIMIT $n
                              );
                            """;
                        prune.Parameters.AddWithValue("$p", save.SaveId.Value);
                        prune.Parameters.AddWithValue("$c", category);
                        prune.Parameters.AddWithValue("$n", retainPerCategory);
                        prune.ExecuteNonQuery();
                    }

                    var survivors = new List<NotificationRow>();
                    foreach (var seq in insertedSeqs)
                    {
                        using var read = db.CreateCommand();
                        read.Transaction = tx;
                        read.CommandText = """
                            SELECT seq, rev, dedup_key, category, severity, source_id, message_key, args_json,
                                   subject_key, world_id, world_turn, state, created_utc
                            FROM rpg_notification WHERE seq = $seq;
                            """;
                        read.Parameters.AddWithValue("$seq", seq);
                        using var r = read.ExecuteReader();
                        if (r.Read()) survivors.Add(ReadNotificationRow(r));
                    }
                    result[save.SaveId] = survivors;

                    // Ledger aging - bounded by the sources' own re-read window, not by history
                    // length. A key stored under turn t is gone after an append under t + N + 1.
                    using var age = db.CreateCommand();
                    age.Transaction = tx;
                    age.CommandText = "DELETE FROM rpg_notification_key WHERE save_id = $p AND world_turn < $bound;";
                    age.Parameters.AddWithValue("$p", save.SaveId.Value);
                    age.Parameters.AddWithValue("$bound", maxTurn - DedupKeyMemoryWorldTurns);
                    age.ExecuteNonQuery();
                }

                if (cursor is not null)
                {
                    using var upsert = db.CreateCommand();
                    upsert.Transaction = tx;
                    upsert.CommandText = """
                        INSERT INTO rpg_notification_source_cursor (source_id, scope_key, position)
                        VALUES ($s, $k, $pos)
                        ON CONFLICT (source_id, scope_key) DO UPDATE SET position = $pos;
                        """;
                    upsert.Parameters.AddWithValue("$s", cursor.SourceId);
                    upsert.Parameters.AddWithValue("$k", cursor.ScopeKey);
                    upsert.Parameters.AddWithValue("$pos", cursor.Position);
                    upsert.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        return result;
    }

    static long BumpSaveRevUnlocked(SqliteConnection db, SqliteTransaction tx, SaveId saveId)
    {
        long current = 0;
        using (var read = db.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT rev FROM rpg_notification_save_rev WHERE save_id = $s;";
            read.Parameters.AddWithValue("$s", saveId.Value);
            var existing = read.ExecuteScalar();
            if (existing is not null && existing is not DBNull) current = Convert.ToInt64(existing);
        }
        var next = checked(current + 1);
        using (var upsert = db.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO rpg_notification_save_rev (save_id, rev) VALUES ($s, $r)
                ON CONFLICT (save_id) DO UPDATE SET rev = $r;
                """;
            upsert.Parameters.AddWithValue("$s", saveId.Value);
            upsert.Parameters.AddWithValue("$r", next);
            upsert.ExecuteNonQuery();
        }
        return next;
    }

    /// <summary>New rows and state changes: rev &gt; sinceRev, rev ascending.</summary>
    public NotificationPage ListNotificationChanges(SaveId saveId, long sinceRev, int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT seq, rev, dedup_key, category, severity, source_id, message_key, args_json,
                   subject_key, world_id, world_turn, state, created_utc
            FROM rpg_notification WHERE save_id = $s AND rev > $since ORDER BY rev ASC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$s", saveId.Value);
        cmd.Parameters.AddWithValue("$since", sinceRev);
        cmd.Parameters.AddWithValue("$limit", limit + 1);
        return ReadPage(cmd, limit);
    }

    public NotificationPage ListNotificationsByCategory(SaveId saveId, string category, long? beforeSeq, int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = beforeSeq is null
            ? """
              SELECT seq, rev, dedup_key, category, severity, source_id, message_key, args_json,
                     subject_key, world_id, world_turn, state, created_utc
              FROM rpg_notification WHERE save_id = $s AND category = $c ORDER BY seq DESC LIMIT $limit;
              """
            : """
              SELECT seq, rev, dedup_key, category, severity, source_id, message_key, args_json,
                     subject_key, world_id, world_turn, state, created_utc
              FROM rpg_notification WHERE save_id = $s AND category = $c AND seq < $before ORDER BY seq DESC LIMIT $limit;
              """;
        cmd.Parameters.AddWithValue("$s", saveId.Value);
        cmd.Parameters.AddWithValue("$c", category);
        if (beforeSeq is not null) cmd.Parameters.AddWithValue("$before", beforeSeq.Value);
        cmd.Parameters.AddWithValue("$limit", limit + 1);
        return ReadPage(cmd, limit);
    }

    static NotificationPage ReadPage(SqliteCommand cmd, int limit)
    {
        var rows = new List<NotificationRow>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) rows.Add(ReadNotificationRow(r));
        }
        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new NotificationPage(rows, hasMore);
    }

    /// <summary>Allowed moves: unread-&gt;read, unread|read-&gt;dismissed, and the one undo
    /// dismissed-&gt;read. Anything else, or a seq the prune already removed, is a no-op for that
    /// seq. Each changed row gets a new rev.</summary>
    public IReadOnlyList<NotificationStateChange> SetNotificationState(SaveId saveId, IReadOnlyList<long> seqs, string state)
    {
        if (seqs is null) throw new ArgumentNullException(nameof(seqs));
        var allowedFrom = state switch
        {
            "dismissed" => new HashSet<string>(StringComparer.Ordinal) { "unread", "read" },
            "read" => new HashSet<string>(StringComparer.Ordinal) { "unread", "dismissed" },
            _ => new HashSet<string>(StringComparer.Ordinal), // "unread" (or anything else) is never a valid target
        };

        var changes = new List<NotificationStateChange>();
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            try
            {
                foreach (var seq in seqs.Distinct())
                {
                    string? current;
                    using (var read = db.CreateCommand())
                    {
                        read.Transaction = tx;
                        read.CommandText = "SELECT state FROM rpg_notification WHERE seq = $seq AND save_id = $s;";
                        read.Parameters.AddWithValue("$seq", seq);
                        read.Parameters.AddWithValue("$s", saveId.Value);
                        var found = read.ExecuteScalar();
                        current = found is null or DBNull ? null : (string)found;
                    }
                    if (current is null || current == state || !allowedFrom.Contains(current)) continue;

                    var rev = BumpSaveRevUnlocked(db, tx, saveId);
                    using (var update = db.CreateCommand())
                    {
                        update.Transaction = tx;
                        update.CommandText = "UPDATE rpg_notification SET state = $state, rev = $rev WHERE seq = $seq;";
                        update.Parameters.AddWithValue("$state", state);
                        update.Parameters.AddWithValue("$rev", rev);
                        update.Parameters.AddWithValue("$seq", seq);
                        update.ExecuteNonQuery();
                    }
                    changes.Add(new NotificationStateChange(seq, rev));
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        return changes;
    }

    /// <summary>True when (save, category, subject) has a stored row with world_turn &gt;= fromTurn -
    /// the repeat-window read (notify-service §Design 2).</summary>
    public bool HasRecentNotification(SaveId saveId, string category, string subjectKey, int fromTurn)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(
              SELECT 1 FROM rpg_notification
              WHERE save_id = $s AND category = $c AND subject_key = $subj AND world_turn >= $from
            );
            """;
        cmd.Parameters.AddWithValue("$s", saveId.Value);
        cmd.Parameters.AddWithValue("$c", category);
        cmd.Parameters.AddWithValue("$subj", subjectKey);
        cmd.Parameters.AddWithValue("$from", fromTurn);
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    public long? GetNotificationCursor(string sourceId, string scopeKey)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT position FROM rpg_notification_source_cursor WHERE source_id = $s AND scope_key = $k;";
        cmd.Parameters.AddWithValue("$s", sourceId);
        cmd.Parameters.AddWithValue("$k", scopeKey);
        var found = cmd.ExecuteScalar();
        return found is null or DBNull ? null : Convert.ToInt64(found);
    }

    /// <summary>Only for initialising an absent cursor (notify-service §3 step 2). Advancing a
    /// cursor over published rows goes through AppendNotificationTurn, never through this.</summary>
    public void InitNotificationCursor(string sourceId, string scopeKey, long position)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO rpg_notification_source_cursor (source_id, scope_key, position)
                VALUES ($s, $k, $pos);
                """;
            cmd.Parameters.AddWithValue("$s", sourceId);
            cmd.Parameters.AddWithValue("$k", scopeKey);
            cmd.Parameters.AddWithValue("$pos", position);
            cmd.ExecuteNonQuery();
        }
    }

    static NotificationRow ReadNotificationRow(SqliteDataReader r) => new(
        Seq: r.GetInt64(0),
        Rev: r.GetInt64(1),
        DedupKey: r.GetString(2),
        Category: r.GetString(3),
        Severity: r.GetString(4),
        SourceId: r.GetString(5),
        MessageKey: r.GetString(6),
        ArgsJson: r.GetString(7),
        SubjectKey: r.IsDBNull(8) ? null : r.GetString(8),
        WorldId: r.IsDBNull(9) ? null : r.GetString(9),
        WorldTurn: r.IsDBNull(10) ? null : r.GetInt32(10),
        State: r.GetString(11),
        CreatedUtc: r.GetString(12));
}
