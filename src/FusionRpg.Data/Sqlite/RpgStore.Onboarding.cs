using Microsoft.Data.Sqlite;
using FusionRpg.Contracts;
using FusionRpg.Core.Onboarding;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

public sealed partial class RpgStore
{
    public sealed record OnboardingCheckpointRow(
        long PlayerId,
        string CheckpointId,
        string State,
        long? EarnedRunId,
        string? RewardRef,
        string? PayloadJson,
        string EarnedUtc,
        string? ClaimedUtc,
        long Revision);

    public sealed record OnboardingClaimResult(
        bool Ok,
        string Reason,
        OnboardingCheckpointRow? Row);

    public sealed record OnboardingStoryRow(
        long PlayerId,
        string StoryId,
        int Version,
        string State,
        string? Outcome,
        bool Eligible,
        string? AcknowledgedUtc,
        long Revision);

    public sealed record OnboardingStoryAckResult(
        bool Ok,
        string Reason,
        OnboardingStoryRow? Row);

    /// <summary>Ensures every existing profile has the versioned Rift story row before web render.</summary>
    void EnsureOnboardingStoryRowsUnlocked(SqliteConnection db)
    {
        using var players = db.CreateCommand();
        players.CommandText = "SELECT id FROM players ORDER BY id;";
        using var reader = players.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        foreach (var id in ids) EnsureOnboardingStoryRowUnlocked(db, id);
    }

    void EnsureOnboardingStoryRowUnlocked(SqliteConnection db, long playerId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO rpg_onboarding_story(
              player_id, story_id, version, state, outcome, acknowledged_utc, revision)
            VALUES($p, $id, $v,
              CASE WHEN EXISTS(
                SELECT 1 FROM runs
                WHERE player_id=$p AND result='victory'
                  AND (game IS NULL OR game=$game)
              ) THEN 'acknowledged' ELSE 'unseen' END,
              CASE WHEN EXISTS(
                SELECT 1 FROM runs
                WHERE player_id=$p AND result='victory'
                  AND (game IS NULL OR game=$game)
              ) THEN 'skipped' ELSE NULL END,
              CASE WHEN EXISTS(
                SELECT 1 FROM runs
                WHERE player_id=$p AND result='victory'
                  AND (game IS NULL OR game=$game)
              ) THEN $utc ELSE NULL END,
              1);
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$id", OnboardingStoryIds.RiftPrologue);
        cmd.Parameters.AddWithValue("$v", OnboardingStoryIds.RiftPrologueVersion);
        cmd.Parameters.AddWithValue("$game", RpgConstants.GameId);
        cmd.Parameters.AddWithValue("$utc", ServerClock.UtcNowDateTime.ToString("o"));
        cmd.ExecuteNonQuery();

        // An old profile may have received the initial unseen row before its legacy runs were
        // assigned a player id during startup. Repair only that unacknowledged row once its
        // settled PvZ victory is visible; a player's explicit completed/skipped result wins.
        using var settleLegacy = db.CreateCommand();
        settleLegacy.CommandText = """
            UPDATE rpg_onboarding_story
            SET state='acknowledged', outcome='skipped', acknowledged_utc=$utc, revision=revision+1
            WHERE player_id=$p AND story_id=$id AND version=$v AND state='unseen'
              AND EXISTS(
                SELECT 1 FROM runs
                WHERE player_id=$p AND result='victory' AND (game IS NULL OR game=$game)
              );
            """;
        settleLegacy.Parameters.AddWithValue("$utc", ServerClock.UtcNowDateTime.ToString("o"));
        settleLegacy.Parameters.AddWithValue("$p", playerId);
        settleLegacy.Parameters.AddWithValue("$id", OnboardingStoryIds.RiftPrologue);
        settleLegacy.Parameters.AddWithValue("$v", OnboardingStoryIds.RiftPrologueVersion);
        settleLegacy.Parameters.AddWithValue("$game", RpgConstants.GameId);
        settleLegacy.ExecuteNonQuery();
    }

    public IReadOnlyList<OnboardingStoryRow>? ListOnboardingStories(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return null;
            EnsureOnboardingStoryRowUnlocked(db, playerId);
            var hasVictory = HasSettledPvzVictoryUnlocked(db, playerId);
            var hasActiveRun = HasActivePvzRunUnlocked(db, playerId);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT player_id, story_id, version, state, outcome, acknowledged_utc, revision
                FROM rpg_onboarding_story
                WHERE player_id=$p
                ORDER BY story_id, version;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            using var reader = cmd.ExecuteReader();
            var rows = new List<OnboardingStoryRow>();
            while (reader.Read())
            {
                var storyId = reader.GetString(1);
                var version = reader.GetInt32(2);
                var state = reader.GetString(3);
                rows.Add(new OnboardingStoryRow(
                    reader.GetInt64(0), storyId, version, state,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    string.Equals(storyId, OnboardingStoryIds.RiftPrologue, StringComparison.Ordinal)
                        && version == OnboardingStoryIds.RiftPrologueVersion
                        && string.Equals(state, OnboardingStoryStates.Unseen, StringComparison.Ordinal)
                        && !hasVictory
                        && !hasActiveRun,
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt64(6)));
            }
            return rows;
        }
    }

    public OnboardingStoryAckResult AcknowledgeOnboardingStory(
        long playerId, string storyId, int version, string outcome)
    {
        if (!string.Equals(storyId, OnboardingStoryIds.RiftPrologue, StringComparison.Ordinal))
            return new(false, "onboarding.story-unknown", null);
        if (version != OnboardingStoryIds.RiftPrologueVersion)
            return new(false, "onboarding.story-version-unsupported", null);
        if (!OnboardingStoryOutcomes.Ordered.Contains(outcome, StringComparer.Ordinal))
            return new(false, "onboarding.story-outcome-unknown", null);

        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null)
                return new(false, "onboarding.player-not-found", null);
            EnsureOnboardingStoryRowUnlocked(db, playerId);
            Exec(db, "BEGIN IMMEDIATE;");
            try
            {
                var existing = ReadOnboardingStoryUnlocked(db, playerId, storyId, version);
                if (existing is null)
                {
                    Exec(db, "ROLLBACK;");
                    return new(false, "onboarding.story-unknown", null);
                }
                if (string.Equals(existing.State, OnboardingStoryStates.Acknowledged, StringComparison.Ordinal))
                {
                    var same = string.Equals(existing.Outcome, outcome, StringComparison.Ordinal);
                    Exec(db, "COMMIT;");
                    return new(same, same ? "" : "onboarding.story-conflict", existing);
                }

                using var update = db.CreateCommand();
                update.CommandText = """
                    UPDATE rpg_onboarding_story
                    SET state='acknowledged', outcome=$o, acknowledged_utc=$utc, revision=revision+1
                    WHERE player_id=$p AND story_id=$id AND version=$v AND state='unseen';
                    """;
                update.Parameters.AddWithValue("$o", outcome);
                update.Parameters.AddWithValue("$utc", ServerClock.UtcNowDateTime.ToString("o"));
                update.Parameters.AddWithValue("$p", playerId);
                update.Parameters.AddWithValue("$id", storyId);
                update.Parameters.AddWithValue("$v", version);
                if (update.ExecuteNonQuery() == 0)
                {
                    Exec(db, "ROLLBACK;");
                    return new(false, "onboarding.story-conflict", ReadOnboardingStoryUnlocked(db, playerId, storyId, version));
                }

                var row = ReadOnboardingStoryUnlocked(db, playerId, storyId, version);
                Exec(db, "COMMIT;");
                return new(true, "", row);
            }
            catch
            {
                try { Exec(db, "ROLLBACK;"); } catch { }
                throw;
            }
        }
    }

    /// <summary>Housekeeping only: a settled first PvZ victory bypasses an unseen prologue.</summary>
    void MarkOnboardingStorySkippedUnlocked(SqliteConnection db, long playerId, string acknowledgedUtc)
    {
        // A defensive migration seam: normal bootstrap already makes this a no-op, but a capture
        // transaction must not miss the independent story write for a legacy profile.
        EnsureOnboardingStoryRowUnlocked(db, playerId);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE rpg_onboarding_story
            SET state='acknowledged', outcome='skipped', acknowledged_utc=$utc, revision=revision+1
            WHERE player_id=$p AND story_id=$id AND version=$v AND state='unseen';
            """;
        cmd.Parameters.AddWithValue("$utc", acknowledgedUtc);
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$id", OnboardingStoryIds.RiftPrologue);
        cmd.Parameters.AddWithValue("$v", OnboardingStoryIds.RiftPrologueVersion);
        cmd.ExecuteNonQuery();
    }

    static bool HasSettledPvzVictoryUnlocked(SqliteConnection db, long playerId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM runs
            WHERE player_id=$p AND result='victory' AND (game IS NULL OR game=$game)
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$game", RpgConstants.GameId);
        return cmd.ExecuteScalar() is not null;
    }

    static bool HasActivePvzRunUnlocked(SqliteConnection db, long playerId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM runs
            WHERE player_id=$p AND ended_utc IS NULL AND (game IS NULL OR game=$game)
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$game", RpgConstants.GameId);
        return cmd.ExecuteScalar() is not null;
    }

    static OnboardingStoryRow? ReadOnboardingStoryUnlocked(
        SqliteConnection db, long playerId, string storyId, int version)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT player_id, story_id, version, state, outcome, acknowledged_utc, revision
            FROM rpg_onboarding_story
            WHERE player_id=$p AND story_id=$id AND version=$v;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$id", storyId);
        cmd.Parameters.AddWithValue("$v", version);
        long rowPlayerId;
        string rowStoryId;
        int rowVersion;
        string state;
        string? outcome;
        string? acknowledgedUtc;
        long revision;
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) return null;
            rowPlayerId = reader.GetInt64(0);
            rowStoryId = reader.GetString(1);
            rowVersion = reader.GetInt32(2);
            state = reader.GetString(3);
            outcome = reader.IsDBNull(4) ? null : reader.GetString(4);
            acknowledgedUtc = reader.IsDBNull(5) ? null : reader.GetString(5);
            revision = reader.GetInt64(6);
        }
        return new OnboardingStoryRow(
            rowPlayerId, rowStoryId, rowVersion, state, outcome,
            string.Equals(state, OnboardingStoryStates.Unseen, StringComparison.Ordinal)
                && !HasSettledPvzVictoryUnlocked(db, playerId)
                && !HasActivePvzRunUnlocked(db, playerId),
            acknowledgedUtc, revision);
    }

    public IReadOnlyList<OnboardingCheckpointRow>? ListOnboardingCheckpoints(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return null;
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT player_id, checkpoint_id, state, earned_run_id, reward_ref, payload_json,
                       earned_utc, claimed_utc, revision
                FROM rpg_onboarding_checkpoint
                WHERE player_id=$p
                ORDER BY CASE checkpoint_id
                    WHEN $first THEN 1 WHEN $species THEN 2 WHEN $equipment THEN 3 ELSE 99 END,
                    checkpoint_id;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$first", OnboardingCheckpointIds.FirstWinDave);
            cmd.Parameters.AddWithValue("$species", OnboardingCheckpointIds.Level3GeneralSpecies);
            cmd.Parameters.AddWithValue("$equipment", OnboardingCheckpointIds.Level4DaveEquipment);
            using var reader = cmd.ExecuteReader();
            var rows = new List<OnboardingCheckpointRow>();
            while (reader.Read()) rows.Add(ReadOnboardingCheckpoint(reader));
            return rows;
        }
    }

    /// <summary>Inserts an earned row once. The primary key is the durable idempotency gate.</summary>
    public bool TryEarnOnboardingCheckpoint(
        long playerId, string checkpointId, long? earnedRunId, string? rewardRef, string? payloadJson,
        string? earnedUtc = null)
    {
        if (!OnboardingCheckpointIds.Ordered.Contains(checkpointId, StringComparer.Ordinal))
            throw new ArgumentException("unknown onboarding checkpoint", nameof(checkpointId));
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) throw new InvalidOperationException("player not found");
            return TryEarnOnboardingCheckpointUnlocked(
                db, playerId, checkpointId, earnedRunId, rewardRef, payloadJson, earnedUtc);
        }
    }

    bool TryEarnOnboardingCheckpointUnlocked(
        SqliteConnection db, long playerId, string checkpointId, long? earnedRunId, string? rewardRef,
        string? payloadJson, string? earnedUtc = null)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO rpg_onboarding_checkpoint(
              player_id, checkpoint_id, state, earned_run_id, reward_ref, payload_json, earned_utc, revision)
            VALUES($p,$id,'earned',$run,$ref,$payload,$utc,1);
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$id", checkpointId);
        cmd.Parameters.AddWithValue("$run", (object?)earnedRunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ref", (object?)rewardRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$payload", (object?)payloadJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$utc", earnedUtc ?? ServerClock.UtcNowDateTime.ToString("o"));
        return cmd.ExecuteNonQuery() > 0;
    }

    public OnboardingClaimResult ClaimOnboardingCheckpoint(long playerId, string checkpointId)
    {
        if (!OnboardingCheckpointIds.Ordered.Contains(checkpointId, StringComparer.Ordinal))
            return new(false, "onboarding.checkpoint-unknown", null);
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null)
                return new(false, "onboarding.player-not-found", null);
            Exec(db, "BEGIN IMMEDIATE;");
            try
            {
                var row = ReadOnboardingCheckpointUnlocked(db, playerId, checkpointId);
                if (row is null)
                {
                    Exec(db, "ROLLBACK;");
                    return new(false, "onboarding.checkpoint-locked", null);
                }
                if (string.Equals(row.State, "claimed", StringComparison.Ordinal)
                    || row.ClaimedUtc is not null)
                {
                    Exec(db, "COMMIT;");
                    return new(false, "onboarding.checkpoint-already-claimed", row);
                }

                var claimedUtc = ServerClock.UtcNowDateTime.ToString("o");
                using var update = db.CreateCommand();
                update.CommandText = """
                    UPDATE rpg_onboarding_checkpoint
                    SET state='claimed', claimed_utc=$utc, revision=revision+1
                    WHERE player_id=$p AND checkpoint_id=$id AND state='earned';
                    """;
                update.Parameters.AddWithValue("$utc", claimedUtc);
                update.Parameters.AddWithValue("$p", playerId);
                update.Parameters.AddWithValue("$id", checkpointId);
                if (update.ExecuteNonQuery() == 0)
                {
                    Exec(db, "ROLLBACK;");
                    return new(false, "onboarding.checkpoint-already-claimed", ReadOnboardingCheckpointUnlocked(db, playerId, checkpointId));
                }

                var claimed = ReadOnboardingCheckpointUnlocked(db, playerId, checkpointId);
                Exec(db, "COMMIT;");
                return new(true, "", claimed);
            }
            catch
            {
                try { Exec(db, "ROLLBACK;"); } catch { }
                throw;
            }
        }
    }

    static OnboardingCheckpointRow ReadOnboardingCheckpoint(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt64(8));

    static OnboardingCheckpointRow? ReadOnboardingCheckpointUnlocked(
        SqliteConnection db, long playerId, string checkpointId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT player_id, checkpoint_id, state, earned_run_id, reward_ref, payload_json,
                   earned_utc, claimed_utc, revision
            FROM rpg_onboarding_checkpoint
            WHERE player_id=$p AND checkpoint_id=$id;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$id", checkpointId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadOnboardingCheckpoint(reader) : null;
    }

    // ---- rift-gate first-open-signal ------------------------------------------------------------
    //
    // A durable once-per-player fact: "the web FE has been opened". A ROW keyed on player_id, not a
    // session flag (mirroring OnboardingStoryRow), so it survives restart and is correct however the
    // player reached the FE — the menu tombstone, F10, the launcher's "Open RPG UI", or a plain
    // browser visit. It is a TRIGGER only: the capture mechanism that consumes it is a separate
    // program. Nothing here reads or writes the story ledger.

    /// <summary>The durable first-open fact plus whether it is actionable right now.</summary>
    public sealed record FirstOpenRow(long PlayerId, string OpenedUtc, long Revision);

    /// <summary>
    /// Records the first open, once. <c>INSERT OR IGNORE</c> on the <c>player_id</c> primary key is the
    /// idempotency gate (the same discipline as <c>TryEarnOnboardingCheckpoint</c>): a second open is a
    /// no-op, never a conflict. Returns true when this call actually created the row.
    /// </summary>
    public bool RecordFirstOpen(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return false;
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO rpg_first_open(player_id, opened_utc, revision)
                VALUES($p, $utc, 1);
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$utc", ServerClock.UtcNowDateTime.ToString("o"));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>The fact, or null when the FE has never been opened for this player.</summary>
    public FirstOpenRow? GetFirstOpen(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return null;
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT player_id, opened_utc, revision FROM rpg_first_open WHERE player_id=$p;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? new FirstOpenRow(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2))
                : null;
        }
    }

    /// <summary>
    /// Whether the first-open fact can be acted on yet. The capture program needs the injector running
    /// and in-game (it reads live Almanac state), so "the injector is connected" is the honest
    /// condition — the same 5-second heartbeat window <see cref="InjectorConnected"/> already models,
    /// not a proxy for it. An unactionable fact stays pending until an injector connects; nothing here
    /// blocks the FE.
    /// </summary>
    public bool IsFirstOpenActionable => InjectorConnected;
}
