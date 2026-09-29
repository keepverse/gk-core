using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Activity;
using FusionRpg.Core.Commanders;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data.Sqlite.Migrations;

/// <summary>
/// `save-identity` SE4.15 — the one-shot key-widening migration (decisions.md S3,
/// spec-save-identity.md "The migration"). Built **dormant**: nothing in `Init` calls it until SE4.20.
///
/// <para><b>Step 0, the gate.</b> A marker in <c>settings</c> means it already ran; a second run is a no-op
/// and never touches a table.</para>
///
/// <para><b>Step 1, the backup, before any write.</b> A new, never-reused
/// <c>rpg-hot.sqlite.pre-save-identity.{utcStamp}.bak</c> via <c>VACUUM INTO</c> — consistent even under
/// WAL, and it refuses an existing target, which is exactly the guarantee this wants. A memory store has
/// no file, so it skips this step. <b>If the backup fails the migration does not run and the server
/// refuses to start</b> (the exception propagates out of `Init`).</para>
///
/// <para><b>Steps 2–7, one transaction.</b> Every write happens inside it, and its LAST statement is the
/// marker, so the marker never exists without the data it describes. Any failure rolls the whole thing
/// back and leaves schema, rows and marker exactly as they were.</para>
/// </summary>
public static class SaveIdentity
{
    /// <summary>The settings key whose presence means the migration already ran.</summary>
    public const string MarkerKey = "schema.save-identity";

    /// <summary>The backup's fixed infix: `rpg-hot.sqlite` + this + `.{utcStamp}.bak`.</summary>
    public const string BackupInfix = ".pre-save-identity";

    /// <summary>
    /// Runs the migration once. Returns true when it ran, false when the marker was already there.
    /// <paramref name="dataDir"/> null/blank means a memory store: no file, so no backup.
    /// </summary>
    public static bool Migrate(SqliteConnection db, string? dataDir, TextWriter? log = null)
        => MigrateCore(db, dataDir, log, backupFailure: null, failAfterStep: null, failureInjector: null);

    /// <summary>
    /// Test-only entry: the same migration, but a failure can be injected at a chosen point to prove
    /// the transaction's atomicity. The injectors are parameters, never shared mutable statics, so a
    /// parallel test creating a store (`Init` calls <see cref="Migrate"/>, which passes none) can
    /// never trip another test's injector.
    /// </summary>
    internal static bool MigrateForTest(
        SqliteConnection db,
        string? dataDir,
        TextWriter? log,
        Action<string>? backupFailure,
        int? failAfterStep,
        Action<SqliteConnection>? failureInjector)
        => MigrateCore(db, dataDir, log, backupFailure, failAfterStep, failureInjector);

    static bool MigrateCore(
        SqliteConnection db,
        string? dataDir,
        TextWriter? log,
        Action<string>? backupFailure,
        int? failAfterStep,
        Action<SqliteConnection>? failureInjector)
    {
        if (HasMarker(db)) return false;

        var backupPath = Backup(db, dataDir, backupFailure);

        string reportJson;
        using (var tx = db.BeginTransaction())
        {
            var report = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["backupPath"] = backupPath,
                ["migratedUtc"] = ServerClock.UtcNowDateTime.ToString("o"),
            };

            RunSteps2To7(db, report, failAfterStep);

            failureInjector?.Invoke(db);

            // The transaction's LAST statement: the marker never exists without the data it describes.
            reportJson = JsonSerializer.Serialize(report);
            WriteMarker(db, reportJson);
            tx.Commit();
        }

        log?.WriteLine($"[save-identity] migrated save identities; report: {reportJson}");
        return true;
    }

    /// <summary>Step 0 — has this migration already run on this store?</summary>
    public static bool HasMarker(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM settings WHERE key=$k;";
        cmd.Parameters.AddWithValue("$k", MarkerKey);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    /// <summary>
    /// Steps 2–7 — the key widening itself. SE4.16 fills step 2 (is the legacy Zomboss row a save);
    /// SE4.17 adds steps 3–4 (seed every save's empires; rebuild the two Tier A tables), SE4.18 step 5
    /// (the `empire_id` backfill and legacy Zomboss re-homing) and SE4.19 step 6 (the legacy row's
    /// fate). Each writes only through <paramref name="db"/>'s ambient transaction and records what it
    /// did into the report.
    /// </summary>
    static void RunSteps2To7(SqliteConnection db, Dictionary<string, object?> report, int? failAfterStep)
    {
        var legacy = FindLegacyZombossRow(db);
        report["legacyZombossFound"] = legacy.Found;
        report["legacyZombossId"] = legacy.Found ? legacy.Id : null;
        report["legacyZombossIsSave"] = legacy.IsSave;
        report["legacyDecision"] = legacy.Evidence;

        var seeded = SeedEverySave(db, legacy);
        report["savesSeeded"] = seeded;

        var rebuild = RebuildTierATables(db, legacy);
        report["tierA"] = rebuild.Select(r => new Dictionary<string, object?>
        {
            ["table"] = r.Table,
            ["copied"] = r.Copied,
            ["leftBehind"] = r.LeftBehind,
        }).ToList();

        BackfillSpecimenEmpires(db, legacy, report, failAfterStep);

        ApplyLegacyRowFate(db, legacy, report);
    }

    static void MaybeFailAfterStep(int step, int? failAfterStep)
    {
        if (failAfterStep == step)
            throw new InvalidOperationException($"injected failure after step {step} (test)");
    }

    /// <summary>Step 6 — the legacy row's fate. A Zomboss-only row is archived (`players.archived_utc`,
    /// kept, codex and all); a legacy row that is a save stays a save with both empires. The column is
    /// added here only if `Init` has not already (`EnsureColumn` runs before the migration in SE4.20).</summary>
    internal static void ApplyLegacyRowFate(SqliteConnection db, LegacyZombossVerdict legacy,
        Dictionary<string, object?> report)
    {
        if (!legacy.Found || legacy.IsSave)
        {
            report["legacyArchivedUtc"] = null;
            return;
        }

        var now = ServerClock.UtcNowDateTime.ToString("o");
        Exec(db, "UPDATE players SET archived_utc = COALESCE(archived_utc, $t) WHERE id = $id;",
            ("$t", now), ("$id", legacy.Id));
        report["legacyArchivedUtc"] = now;
    }

    /// <summary>Step 5 — `rpg_unique_actors.empire_id` is **set on every row** (never only the nulls):
    /// a save's own specimens get its human empire whatever their `origin`; a Zomboss-only legacy row's
    /// specimens get `zomboss` and a save by (a) match provenance, (b) being the only save, else (c)
    /// stay `Retired` on the legacy row. A re-homed specimen brings its lawn session and recovery row,
    /// and its legacy contract is released (`bound = 0`); nothing is deleted.</summary>
    internal static void BackfillSpecimenEmpires(SqliteConnection db, LegacyZombossVerdict legacy,
        Dictionary<string, object?> report, int? failAfterStep)
    {
        var saves = SavesForMigration(db, legacy);

        long stamped = 0;
        foreach (var saveId in saves)
        {
            var empire = RpgStore.HumanEmpireOfOrNull(db, saveId);
            if (empire is null) continue;
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE rpg_unique_actors SET empire_id = $e WHERE player_id = $p;";
            cmd.Parameters.AddWithValue("$e", empire.Value.Value);
            cmd.Parameters.AddWithValue("$p", saveId);
            stamped += cmd.ExecuteNonQuery();
        }
        report["specimensStampedBySave"] = stamped;
        report["zombossSpecimensKeptOnSave"] = ScalarLong(db, """
            SELECT COUNT(*) FROM rpg_unique_actors a
            JOIN rpg_creature_profiles p ON p.instance_id = a.instance_id
            WHERE p.origin = 'zomboss' AND a.player_id <> $z;
            """, ("$z", legacy.Found ? legacy.Id : 0L));

        long byProvenance = 0, byOnlySave = 0;
        var unattributed = new List<string>();
        if (legacy.Found && !legacy.IsSave)
        {
            var now = ServerClock.UtcNowDateTime.ToString("o");
            foreach (var instanceId in SpecimensOf(db, legacy.Id))
            {
                var save = ProvenanceSaveUnlocked(db, instanceId);
                var rule = "provenance";
                if (save is null && saves.Count == 1)
                {
                    save = saves[0];
                    rule = "only-save";
                }

                if (save is null)
                {
                    Exec(db, $"UPDATE rpg_unique_actors SET empire_id = '{EmpireId.Zomboss.Value}', phase = '{UniqueActorPhases.Retired}' WHERE instance_id = $i;", ("$i", instanceId));
                    unattributed.Add(instanceId);
                    continue;
                }

                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = $"UPDATE rpg_unique_actors SET player_id = $s, empire_id = '{EmpireId.Zomboss.Value}' WHERE instance_id = $i;";
                    cmd.Parameters.AddWithValue("$s", save.Value);
                    cmd.Parameters.AddWithValue("$i", instanceId);
                    cmd.ExecuteNonQuery();
                }
                Exec(db, "UPDATE rpg_unique_lawn_sessions SET player_id = $s WHERE instance_id = $i;", ("$s", save.Value), ("$i", instanceId));
                Exec(db, "UPDATE rpg_unique_actor_recovery SET player_id = $s WHERE instance_id = $i;", ("$s", save.Value), ("$i", instanceId));
                Exec(db, "UPDATE rpg_creature_contracts SET bound = 0, released_utc = COALESCE(released_utc, $t) WHERE instance_id = $i;", ("$t", now), ("$i", instanceId));

                if (rule == "provenance") byProvenance++;
                else byOnlySave++;
            }
        }

        report["rehomedByProvenance"] = byProvenance;
        report["rehomedByOnlySave"] = byOnlySave;
        report["unattributedSpecimenIds"] = unattributed;

        MaybeFailAfterStep(5, failAfterStep);
    }

    /// <summary>The live saves a migration sees: every `players` row but a Zomboss-only legacy row.</summary>
    static List<long> SavesForMigration(SqliteConnection db, LegacyZombossVerdict legacy)
    {
        var ids = new List<long>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM players ORDER BY id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
        }
        if (legacy.Found && !legacy.IsSave) ids.Remove(legacy.Id);
        return ids;
    }

    static List<string> SpecimensOf(SqliteConnection db, long playerId)
    {
        var ids = new List<string>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT instance_id FROM rpg_unique_actors WHERE player_id = $p ORDER BY instance_id;";
        cmd.Parameters.AddWithValue("$p", playerId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>
    /// Rule (a): a `match_key` on the specimen, its lawn session, or any of its lawn-XP receipts joins
    /// `runs.match_key → runs.player_id`. The specimen's own column is cleared on return to roster, but a
    /// receipt keeps its match key for good — and the boot sweep runs **after** this migration (SE4.20's
    /// ordering), so an `ActiveBound` specimen is still attributed from the column it will lose.
    /// </summary>
    static long? ProvenanceSaveUnlocked(SqliteConnection db, string instanceId)
    {
        foreach (var matchKey in MatchKeysForSpecimen(db, instanceId))
        {
            if (string.IsNullOrWhiteSpace(matchKey)) continue;
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT player_id FROM runs WHERE match_key = $k LIMIT 1;";
            cmd.Parameters.AddWithValue("$k", matchKey);
            if (cmd.ExecuteScalar() is long save) return save;
        }
        return null;
    }

    static IEnumerable<string> MatchKeysForSpecimen(SqliteConnection db, string instanceId)
    {
        yield return MatchKey(db, "rpg_unique_actors", instanceId);
        yield return MatchKey(db, "rpg_unique_lawn_sessions", instanceId);

        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT match_key FROM rpg_unique_lawn_xp_receipts WHERE instance_id = $i ORDER BY created_utc, match_key;";
        cmd.Parameters.AddWithValue("$i", instanceId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0) && !string.IsNullOrWhiteSpace(reader.GetString(0)))
                yield return reader.GetString(0);
        }
    }

    static string? MatchKey(SqliteConnection db, string table, string instanceId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT match_key FROM \"{table}\" WHERE instance_id = $i LIMIT 1;";
        cmd.Parameters.AddWithValue("$i", instanceId);
        return cmd.ExecuteScalar() as string;
    }

    static long ScalarLong(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    /// <summary>One Tier A table's rebuild: the new name's row count and what stayed on the legacy table.</summary>
    internal readonly record struct TierATableRebuild(string Table, long Copied, long LeftBehind);

    /// <summary>Step 3 — every `players` row is a save except a Zomboss-only legacy row. Idempotent with
    /// SE4.12's own seeding (`INSERT OR IGNORE`), so a boot before and after this agree.</summary>
    internal static List<long> SeedEverySave(SqliteConnection db, LegacyZombossVerdict legacy)
    {
        var ids = new List<long>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM players ORDER BY id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
        }

        var seeded = new List<long>();
        foreach (var id in ids)
        {
            if (legacy.Found && !legacy.IsSave && id == legacy.Id) continue;
            RpgStore.SeedSaveEmpiresUnlocked(db, id);
            seeded.Add(id);
        }
        return seeded;
    }

    /// <summary>Step 4 — rebuild `rpg_actor_progression` and `rpg_xp_ledger` with `(save_id, empire_id)`
    /// as the owner key. The old tables are renamed and kept; every row is copied onto its save's human
    /// empire (history is never re-attributed). A Zomboss-only legacy row's rows are not copied and are
    /// counted, because they stay in the retained legacy table.</summary>
    internal static List<TierATableRebuild> RebuildTierATables(SqliteConnection db, LegacyZombossVerdict legacy)
    {
        var results = new List<TierATableRebuild>();
        if (!ColumnExists(db, "rpg_actor_progression", "empire_id"))
        {
            RenameIfPresent(db, "rpg_actor_progression");
            Exec(db, """
                CREATE TABLE rpg_actor_progression (
                  save_id INTEGER NOT NULL,
                  empire_id TEXT NOT NULL,
                  kind TEXT NOT NULL,
                  type_id INTEGER NOT NULL,
                  level INTEGER NOT NULL DEFAULT 1,
                  xp INTEGER NOT NULL DEFAULT 0,
                  highest_level INTEGER NOT NULL DEFAULT 1,
                  demotion_count INTEGER NOT NULL DEFAULT 0,
                  revision INTEGER NOT NULL DEFAULT 0,
                  updated_utc TEXT NOT NULL,
                  through_ledger_id INTEGER NOT NULL DEFAULT 0,
                  xp_by_reason_json TEXT,
                  scope_key TEXT,
                  PRIMARY KEY (save_id, empire_id, kind, type_id)
                );
                CREATE INDEX ix_rpg_actor_progression_save_empire
                  ON rpg_actor_progression(save_id, empire_id, kind);
                """);
            // `scope_key` was added by an EnsureColumn before the migration (Init's ordering), so a row
            // range built from the producer's own store always carries it; only a hand-built legacy
            // table could lack it, and the fresh-DB path skips the rebuild entirely.
            var hasScopeKey = ColumnExists(db, LegacyName("rpg_actor_progression"), "scope_key");
            var copy = hasScopeKey
                ? "INSERT INTO rpg_actor_progression(save_id, empire_id, kind, type_id, level, xp, highest_level, demotion_count, revision, updated_utc, through_ledger_id, xp_by_reason_json, scope_key) "
                  + "SELECT player_id, $emp, kind, type_id, level, xp, highest_level, demotion_count, revision, updated_utc, through_ledger_id, xp_by_reason_json, scope_key FROM rpg_actor_progression__pre_save_identity WHERE player_id = $p;"
                : "INSERT INTO rpg_actor_progression(save_id, empire_id, kind, type_id, level, xp, highest_level, demotion_count, revision, updated_utc, through_ledger_id, xp_by_reason_json) "
                  + "SELECT player_id, $emp, kind, type_id, level, xp, highest_level, demotion_count, revision, updated_utc, through_ledger_id, xp_by_reason_json FROM rpg_actor_progression__pre_save_identity WHERE player_id = $p;";
            results.Add(CopyOntoHumanEmpire(db, legacy, "rpg_actor_progression", copy));
        }
        else
        {
            results.Add(new TierATableRebuild("rpg_actor_progression", 0, 0));
        }

        if (!ColumnExists(db, "rpg_xp_ledger", "empire_id"))
        {
            RenameIfPresent(db, "rpg_xp_ledger");
            Exec(db, """
                CREATE TABLE rpg_xp_ledger (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  save_id INTEGER NOT NULL,
                  empire_id TEXT NOT NULL,
                  kind TEXT NOT NULL,
                  type_id INTEGER NOT NULL,
                  run_id INTEGER NOT NULL DEFAULT 0,
                  t TEXT NOT NULL,
                  delta INTEGER NOT NULL,
                  reason TEXT NOT NULL,
                  activity_fact_id INTEGER,
                  level_before INTEGER NOT NULL,
                  xp_before INTEGER NOT NULL,
                  level_after INTEGER NOT NULL,
                  xp_after INTEGER NOT NULL,
                  demotion_before INTEGER NOT NULL,
                  demotion_after INTEGER NOT NULL,
                  payload_json TEXT,
                  dedupe_key TEXT NOT NULL,
                  UNIQUE (save_id, empire_id, kind, type_id, reason, dedupe_key)
                );
                CREATE INDEX ix_rpg_xp_ledger_save_empire
                  ON rpg_xp_ledger(save_id, empire_id, id);
                """);
            const string copy = "INSERT INTO rpg_xp_ledger(save_id, empire_id, kind, type_id, run_id, t, delta, reason, activity_fact_id, level_before, xp_before, level_after, xp_after, demotion_before, demotion_after, payload_json, dedupe_key) "
                + "SELECT player_id, $emp, kind, type_id, run_id, t, delta, reason, activity_fact_id, level_before, xp_before, level_after, xp_after, demotion_before, demotion_after, payload_json, dedupe_key FROM rpg_xp_ledger__pre_save_identity WHERE player_id = $p;";
            results.Add(CopyOntoHumanEmpire(db, legacy, "rpg_xp_ledger", copy));
        }
        else
        {
            results.Add(new TierATableRebuild("rpg_xp_ledger", 0, 0));
        }

        return results;
    }

    static string LegacyName(string table) => table + "__pre_save_identity";

    static void RenameIfPresent(SqliteConnection db, string table)
    {
        if (TableExists(db, LegacyName(table))) return;
        Exec(db, $"ALTER TABLE {table} RENAME TO {LegacyName(table)};");
    }

    /// <summary>Copies a legacy table's rows one save at a time — each save's rows get that save's human
    /// empire. A row whose player is not a seeded save (the Zomboss-only legacy row, or an unknown id)
    /// stays behind and is counted.</summary>
    static TierATableRebuild CopyOntoHumanEmpire(SqliteConnection db, LegacyZombossVerdict legacy,
        string table, string copySql)
    {
        var sources = new List<long>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = $"SELECT DISTINCT player_id FROM {LegacyName(table)} ORDER BY player_id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) sources.Add(reader.GetInt64(0));
        }

        long copied = 0, left = 0;
        foreach (var playerId in sources)
        {
            var empire = RpgStore.HumanEmpireOfOrNull(db, playerId);
            if (empire is null || (legacy.Found && !legacy.IsSave && playerId == legacy.Id))
            {
                left += CountForSave(db, LegacyName(table), playerId);
                continue;
            }

            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = copySql;
                cmd.Parameters.AddWithValue("$emp", empire.Value.Value);
                cmd.Parameters.AddWithValue("$p", playerId);
                copied += cmd.ExecuteNonQuery();
            }
        }
        return new TierATableRebuild(table, copied, left);
    }

    static long CountForSave(SqliteConnection db, string table, long playerId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE player_id = $p;";
        cmd.Parameters.AddWithValue("$p", playerId);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    static bool TableExists(SqliteConnection db, string table) =>
        Exists(db, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;", ("$name", table));

    static bool ColumnExists(SqliteConnection db, string table, string column)
    {
        if (!TableExists(db, table)) return false;
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static void Exec(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>What step 2 decides about the one row named `Zomboss` the code used to look up.</summary>
    internal readonly record struct LegacyZombossVerdict(long Id, bool Found, bool IsSave, string Evidence);

    /// <summary>
    /// **The one historical name lookup** (spec step 2): the lowest-id row named `Zomboss`, exactly the
    /// rule `EnsureZombossPlayer` used. A row is Zomboss-only only if every reference is one the Zomboss
    /// path writes; any other reference makes it a save, because the name collision is real (`POST
    /// /api/players` accepts any name) and a false "Zomboss-only" would hide a player's save.
    /// </summary>
    internal static LegacyZombossVerdict FindLegacyZombossRow(SqliteConnection db)
    {
        long id;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM players WHERE name = 'Zomboss' ORDER BY id LIMIT 1;";
            var value = cmd.ExecuteScalar();
            if (value is null or DBNull) return new(0, false, false, "no row named Zomboss");
            id = Convert.ToInt64(value);
        }

        var evidence = SaveEvidence(db, id);
        return evidence is null
            ? new(id, true, false, "every reference is on the Zomboss path")
            : new(id, true, true, evidence);
    }

    /// <summary>
    /// Step 2's evidence, strongest first, and null when the row is Zomboss-only. Every check is an
    /// allowlist of what the Zomboss path writes (`RpgStore.ZombossDeploy.cs`, `EnsureZombossPlayer`,
    /// `MintCreatureUnlocked`); anything else is human evidence. A tie goes to "save".
    /// </summary>
    static string? SaveEvidence(SqliteConnection db, long id)
    {
        // The current save is human evidence. (init also seeds the current save's empires, so the
        // registry check below would catch it too; this reports the decision the spec names first.)
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT json FROM settings WHERE key = 'current_player_id' LIMIT 1;";
            if (cmd.ExecuteScalar() is string current && current == id.ToString())
                return "settings.current_player_id";
        }

        // Since SE4.12 only the save path seeds the registry — and `EnsureZombossPlayer` deliberately
        // does not — so this is decisive on its own (it is also why SE4.12 ran first).
        if (Exists(db, "SELECT 1 FROM rpg_save_empires WHERE save_id = $id LIMIT 1;", ("$id", id)))
            return "rpg_save_empires";

        // A specimen that is not a Zomboss mint is the human's: the player saw it, owns it, may have
        // played with it. `origin` lives on the profile, keyed by the specimen's instance.
        if (Exists(db, "SELECT 1 FROM rpg_unique_actors a JOIN rpg_creature_profiles p ON p.instance_id = a.instance_id WHERE a.player_id = $id AND COALESCE(p.origin, '') <> 'zomboss' LIMIT 1;", ("$id", id)))
            return "rpg_creature_profiles.origin";

        // A person edited one of the row's specimens: equipment or a stat-mod row is instance-keyed,
        // so the player-id sweep below cannot see it. The retired legacy equipment table's own name
        // is asked through RpgStore.HasLegacyEquipmentRowsUnlocked, not spelled here directly --
        // LegacyEquipTableRetirementGuardTests.cs's own allowlist names only RpgStore.cs and
        // RpgStore.UniqueActors.cs as files that may still name that table.
        if (RpgStore.HasLegacyEquipmentRowsUnlocked(db, id))
            return "legacy-unique-equipment-row";
        if (Exists(db, "SELECT 1 FROM rpg_unique_stat_mods m JOIN rpg_unique_actors a ON a.instance_id = m.instance_id WHERE a.player_id = $id LIMIT 1;", ("$id", id)))
            return "rpg_unique_stat_mods";

        // The only activity fact the Zomboss path writes is `ExtraSpawnFired` (RecordExtraSpawnIntent).
        if (Exists(db, "SELECT 1 FROM pvz_activity_facts WHERE player_id = $id AND kind <> 'ExtraSpawnFired' LIMIT 1;", ("$id", id)))
            return "pvz_activity_facts";
        if (RollupHasHumanCounter(db, id)) return "pvz_activity_rollups";

        // Any other player-keyed or owner-scoped row: items, souls, summons, runs, worlds, presets,
        // respec, allocation scope keys, and every table added later (unclassified means "save").
        return FirstForeignReference(db, id);
    }

    /// <summary>Tier A/B and instance-keyed tables whose every reference the Zomboss path writes.</summary>
    static readonly HashSet<string> ZombossPathTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "players",
        "pvz_stat_revisions",
        "pvz_activity_revisions",
        "pvz_activity_facts",
        "pvz_activity_rollups",
        "rpg_onboarding_story",
        "rpg_unique_actors",
        "rpg_creature_codex",
        "rpg_creature_contracts",
        "rpg_contract_state",
        "rpg_unique_lawn_sessions",
        "rpg_unique_actor_recovery",
    };

    /// <summary>
    /// An activity rollup counts every kind; the Zomboss path only ever bumps `extraSpawnsFired`, so any
    /// other non-zero counter is human play. Parsed through the same Core type the writer uses.
    /// </summary>
    static bool RollupHasHumanCounter(SqliteConnection db, long id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT counters_json FROM pvz_activity_rollups WHERE player_id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteScalar() is not string json) return false;
        try
        {
            var counters = JsonSerializer.Deserialize<PvzActivityRollupCounters>(json, CamelCase)
                ?? new PvzActivityRollupCounters();
            return counters.MatchesStarted > 0 || counters.MatchesEnded > 0 || counters.Victories > 0
                || counters.Defeats > 0 || counters.ZombiesKilled > 0 || counters.PlantsLost > 0
                || counters.PlantsPlaced > 0;
        }
        catch (JsonException)
        {
            // An unreadable rollup is evidence enough to keep the row a save (a tie goes to "save").
            return true;
        }
    }

    static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// The first reference on a table the Zomboss path does not write, or null. Sweeps every table in
    /// the schema: `player_id` / `owner_player_id` columns, `owner_kind`/`owner_key` rows, and
    /// `scope`/`scope_key` rows whose key names `player:{id}`. A table added later is caught without
    /// editing this list — unclassified means "save", never "Zomboss".
    /// </summary>
    static string? FirstForeignReference(SqliteConnection db, long id)
    {
        foreach (var (table, columns) in Tables(db))
        {
            if (ZombossPathTables.Contains(table)) continue;

            if (columns.Contains("owner_kind") && columns.Contains("owner_key"))
            {
                if (Exists(db, $"SELECT 1 FROM \"{table}\" WHERE owner_kind = 'player' AND owner_key = $key LIMIT 1;", ("$key", id.ToString())))
                    return table;
            }
            else if (columns.Contains("scope") && columns.Contains("scope_key"))
            {
                if (Exists(db, $"SELECT 1 FROM \"{table}\" WHERE scope_key = 'player:' || $key OR scope_key LIKE 'player:' || $key || ':%' LIMIT 1;", ("$key", id.ToString())))
                    return table;
            }

            var playerColumn = columns.Contains("player_id") ? "player_id"
                : columns.Contains("owner_player_id") ? "owner_player_id" : null;
            // Bound as text: `player_id` is INTEGER on most tables but TEXT on the item/human-identity
            // ones, and SQLite applies the column's affinity to the parameter either way.
            if (playerColumn is not null
                && Exists(db, $"SELECT 1 FROM \"{table}\" WHERE \"{playerColumn}\" = $key LIMIT 1;", ("$key", id.ToString())))
                return table;
        }
        return null;
    }

    /// <summary>Every user table and its columns, read from the schema so a new table is never missed.</summary>
    static IEnumerable<(string Table, List<string> Columns)> Tables(SqliteConnection db)
    {
        var names = new List<string>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(0));
        }

        foreach (var name in names)
        {
            var columns = new List<string>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info(\"{name}\");";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) columns.Add(reader.GetString(1));
            }
            yield return (name, columns);
        }
    }

    static bool Exists(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    /// <summary>Step 1 — a consistent copy before any write. Returns the backup path, or null for memory.</summary>
    static string? Backup(SqliteConnection db, string? dataDir, Action<string>? backupFailure)
    {
        if (string.IsNullOrWhiteSpace(dataDir)) return null;

        var baseStamp = ServerClock.UtcNowDateTime;
        var target = BackupPath(dataDir, baseStamp, 0);

        // `VACUUM INTO` refuses an existing target, and two attempts can share a millisecond (a failed
        // attempt is fast), so nudge the stamp until the name is new. The name is never reused.
        for (var backoff = 1; File.Exists(target); backoff++)
            target = BackupPath(dataDir, baseStamp, backoff);

        try
        {
            backupFailure?.Invoke(target);

            using var cmd = db.CreateCommand();
            cmd.CommandText = "VACUUM INTO $target;";
            cmd.Parameters.AddWithValue("$target", target);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // No backup, no migration: a key widening without a copy to restore is unrecoverable.
            throw new InvalidOperationException(
                $"save-identity: the backup to '{target}' failed, so the migration did not run "
                + $"and the server must not start: {ex.Message}", ex);
        }

        return target;
    }

    static string BackupPath(string dataDir, DateTime baseStamp, int backoffMs) =>
        Path.Combine(dataDir,
            $"rpg-hot.sqlite{BackupInfix}.{baseStamp.AddMilliseconds(backoffMs):yyyyMMdd'T'HHmmssfff'Z'}.bak");

    static void WriteMarker(SqliteConnection db, string reportJson)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings(key, json, updated_utc) VALUES($k, $j, $t)
            ON CONFLICT(key) DO UPDATE SET json=$j, updated_utc=$t;
            """;
        cmd.Parameters.AddWithValue("$k", MarkerKey);
        cmd.Parameters.AddWithValue("$j", reportJson);
        cmd.Parameters.AddWithValue("$t", ServerClock.UtcNowDateTime.ToString("o"));
        cmd.ExecuteNonQuery();
    }
}
