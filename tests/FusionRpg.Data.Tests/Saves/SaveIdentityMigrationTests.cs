using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.World;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using FusionRpg.Data.Sqlite.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Saves;

/// <summary>
/// `save-identity` SE4.15 — the migration shell: the gate, the marker and the one transaction. The disk
/// half (the backup) is <see cref="SaveIdentityBackupDiskTests"/>, tagged `DiskSemantics`. All store
/// tests here run in memory (testing-standard.md).
/// </summary>
[Trait("VerificationId", "data.save-identity")]
public class SaveIdentityMigrationTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SaveIdentityMigrationTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    bool Migrate(TextWriter? log = null) => SaveIdentityFixtures.MigrateAgain(_testStore);

    bool HasMarker()
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        return SaveIdentity.HasMarker(db);
    }

    [Fact]
    public void A_fresh_boot_has_the_marker_and_a_second_run_is_a_no_op()
    {
        // SE4.20: Init runs the migration. On a fresh database the widened DDL is already in place, so
        // the run only writes its marker (an empty report) — no rename.
        Assert.True(HasMarker());

        using var db = SaveIdentityFixtures.Open(_testStore);
        Assert.False(SaveIdentity.Migrate(db, _testStore.DataDir));
    }

    [Fact]
    public void An_injected_failure_leaves_no_marker_and_a_later_run_still_works()
    {
        var threw = Record.Exception(() => SaveIdentityFixtures.MigrateAgain(_testStore,
            failureInjector: _ => throw new InvalidOperationException("injected (test)")));
        Assert.IsType<InvalidOperationException>(threw);
        Assert.False(HasMarker());

        // Nothing half-done: with no injector the migration runs cleanly and writes its marker.
        Assert.True(Migrate());
        Assert.True(HasMarker());
    }

    [Fact]
    public void The_marker_is_written_at_boot()
    {
        Assert.True(HasMarker());
    }
}

/// <summary>
/// `save-identity` SE4.16 — step 2: is the one legacy row named `Zomboss` also a save? The rule is
/// "Zomboss-only iff every reference is one the Zomboss path writes", and a tie goes to "save". Each
/// fixture is built from real production calls; one test per evidence kind alone.
/// </summary>
[Trait("VerificationId", "data.save-identity")]
public class SaveIdentityLegacyZombossTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SaveIdentityLegacyZombossTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    [Fact]
    public void A_row_with_only_allowed_zomboss_writes_is_not_a_save()
    {
        var id = SaveIdentityFixtures.ZombossOnly(_testStore);

        var verdict = SaveIdentityFixtures.Classify(_testStore);

        Assert.True(verdict.Found);
        Assert.Equal(id, verdict.Id);
        Assert.False(verdict.IsSave);
    }

    [Fact]
    public void The_current_save_setting_alone_makes_it_a_save()
    {
        var id = SaveIdentityFixtures.ZombossOnly(_testStore);
        Assert.True(_store.SetCurrentPlayer(id));

        var verdict = SaveIdentityFixtures.Classify(_testStore);

        Assert.True(verdict.IsSave);
        Assert.Equal("settings.current_player_id", verdict.Evidence);
    }

    [Fact]
    public void A_save_empires_row_alone_makes_it_a_save()
    {
        var id = SaveIdentityFixtures.ZombossOnly(_testStore);
        using (var db = SaveIdentityFixtures.Open(_testStore))
            RpgStore.SeedSaveEmpiresUnlocked(db, id);

        var verdict = SaveIdentityFixtures.Classify(_testStore);

        Assert.True(verdict.IsSave);
        Assert.Equal("rpg_save_empires", verdict.Evidence);
    }

    [Fact]
    public void A_non_zomboss_specimen_alone_makes_it_a_save()
    {
        var id = SaveIdentityFixtures.ZombossOnly(_testStore);
        SaveIdentityFixtures.Summon(_testStore, id);

        var verdict = SaveIdentityFixtures.Classify(_testStore);

        Assert.True(verdict.IsSave);
        Assert.Equal("rpg_creature_profiles.origin", verdict.Evidence);
    }

    [Fact]
    public void An_item_row_alone_makes_it_a_save()
    {
        var id = SaveIdentityFixtures.ZombossOnly(_testStore);
        _store.AdjustStock(id.ToString(), "item.iron-band", 1);

        var verdict = SaveIdentityFixtures.Classify(_testStore);

        Assert.True(verdict.IsSave);
        Assert.Equal("rpg_item_stock", verdict.Evidence);
    }

    [Fact]
    public void A_world_alone_makes_it_a_save()
    {
        var id = SaveIdentityFixtures.ZombossOnly(_testStore);
        var (ok, reason, _) = _store.CreateWorld(id,
            WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, 1, "w"));
        Assert.True(ok, reason);

        var verdict = SaveIdentityFixtures.Classify(_testStore);

        Assert.True(verdict.IsSave);
        Assert.Equal("rpg_worlds", verdict.Evidence);
    }

    [Fact]
    public void An_allocation_alone_makes_it_a_save()
    {
        var id = SaveIdentityFixtures.ZombossOnly(_testStore);
        _store.SaveAllocation(AllocationScope.Commander, $"player:{id}",
            AptitudeAllocation.Single(AllocationScope.Commander, AptitudeCatalog.All[0].Id, 1));

        var verdict = SaveIdentityFixtures.Classify(_testStore);

        Assert.True(verdict.IsSave);
        Assert.Equal("rpg_aptitude_allocation", verdict.Evidence);
    }

    [Fact]
    public void A_save_named_zomboss_that_owns_a_run_stays_a_save()
    {
        var save = _store.CreatePlayer("Zomboss");
        Assert.True(_store.SetCurrentPlayer(save.Id));
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"), Kind = "board.start",
            MatchKey = "m-name-collision", Payload = new { levelName = "test", levelType = "adventure" },
        });

        var verdict = SaveIdentityFixtures.Classify(_testStore);

        Assert.True(verdict.Found);
        Assert.Equal(save.Id, verdict.Id);
        Assert.True(verdict.IsSave);
    }

    [Fact]
    public void A_save_named_zomboss_created_before_the_first_deploy_stays_a_save()
    {
        var save = _store.CreatePlayer("Zomboss");
        SaveIdentityFixtures.Summon(_testStore, save.Id);   // a summon, and no run

        var verdict = SaveIdentityFixtures.Classify(_testStore);

        Assert.True(verdict.Found);
        Assert.Equal(save.Id, verdict.Id);
        Assert.True(verdict.IsSave);
    }
}

/// <summary>
/// `save-identity` SE4.17 — steps 3–4: seed every save's empires, and rebuild the two Tier A tables with
/// `(save_id, empire_id)` as the owner key. The old tables are renamed and kept.
/// </summary>
[Trait("VerificationId", "data.save-identity")]
public class SaveIdentityTierATests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SaveIdentityTierATests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    bool Migrate() => SaveIdentityFixtures.MigrateAgain(_testStore);

    static void Exec(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    static long Scalar(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0L : Convert.ToInt64(v);
    }

    static long Count(SqliteConnection db, string sql, params (string Name, object Value)[] parameters) =>
        Scalar(db, sql, parameters);

    [Fact]
    public void Step_three_seeds_a_save_that_lost_its_empires()
    {
        var save = _store.CreatePlayer("Hero");
        using (var db = SaveIdentityFixtures.Open(_testStore))
            Exec(db, "DELETE FROM rpg_save_empires WHERE save_id = $s;", ("$s", save.Id));
        Assert.Empty(_store.EmpiresOf(save.Id));

        Assert.True(Migrate());

        Assert.NotEmpty(_store.EmpiresOf(save.Id));
    }

    [Fact]
    public void A_progression_row_is_readable_by_save_and_empire_and_a_fresh_boot_never_renames()
    {
        var save = _store.CreatePlayer("Hero");
        _store.SeedRpgProgressionDemo(save.Id);

        Assert.True(Migrate());

        var human = _store.HumanEmpireOf(save.Id).Value;
        using var db = SaveIdentityFixtures.Open(_testStore);
        Assert.True(Count(db, "SELECT COUNT(*) FROM rpg_actor_progression WHERE save_id = $s;", ("$s", save.Id)) > 0);
        Assert.Equal(0L, Count(db, "SELECT COUNT(*) FROM rpg_actor_progression WHERE empire_id <> $e;", ("$e", human)));
        // SE4.20: Init already widened this table, so the run never renames it.
        Assert.Equal(0L, Count(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='rpg_actor_progression__pre_save_identity';"));
    }

    [Fact]
    public void An_old_shape_store_is_rebuilt_rows_copied_and_the_legacy_tables_kept()
    {
        using var legacy = SaveIdentityFixtures.LegacyShape();

        // Init already migrated it.
        using var db = SaveIdentityFixtures.Open(legacy);
        Assert.Equal(3L, Count(db, "SELECT COUNT(*) FROM rpg_actor_progression__pre_save_identity;"));
        Assert.Equal(2L, Count(db, "SELECT COUNT(*) FROM rpg_xp_ledger__pre_save_identity;"));
        var human = legacy.Store.HumanEmpireOf(1).Value;
        Assert.Equal(2L, Count(db, "SELECT COUNT(*) FROM rpg_actor_progression WHERE save_id = 1 AND empire_id = $e;", ("$e", human)));
        // The orphan row's player is no save, so it stays behind and is not copied.
        Assert.Equal(0L, Count(db, "SELECT COUNT(*) FROM rpg_actor_progression WHERE save_id = 999;"));
        Assert.Equal(1L, Count(db, "SELECT COUNT(*) FROM rpg_actor_progression__pre_save_identity WHERE player_id = 999;"));
        Assert.Equal(1L, Count(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_rpg_actor_progression_save_empire';"));
        Assert.Equal(1L, Count(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_rpg_xp_ledger_save_empire';"));
        // The hand-seeded Zomboss row had no evidence, so it is archived.
        Assert.Equal(1L, Count(db, "SELECT COUNT(*) FROM players WHERE id = 2 AND archived_utc IS NOT NULL;"));
    }
}

/// <summary>
/// `save-identity` SE4.18 — step 5: backfill `rpg_unique_actors.empire_id`, and re-home a Zomboss-only
/// legacy row's specimens by match provenance, then by "the only save", else retire them in place.
/// </summary>
[Trait("VerificationId", "data.save-identity")]
public class SaveIdentitySpecimenBackfillTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SaveIdentitySpecimenBackfillTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    bool Migrate() => SaveIdentityFixtures.MigrateAgain(_testStore);

    static object? Scalar(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteScalar();
    }

    [Fact]
    public void A_saves_own_specimen_gets_its_human_empire_whatever_its_origin()
    {
        var save = _store.CreatePlayer("Zomboss");
        // A zomboss-origin specimen owned by a real save (the name collision) is the player's play state.
        var specimen = SaveIdentityFixtures.MintZombossOriginSpecimen(_testStore, save.Id);
        Assert.Equal(save.Id, specimen.Actor.PlayerId);

        Assert.True(Migrate());

        var human = _store.HumanEmpireOf(save.Id).Value;
        using var db = SaveIdentityFixtures.Open(_testStore);
        Assert.Equal(human, Scalar(db, "SELECT empire_id FROM rpg_unique_actors WHERE instance_id = $i;", ("$i", specimen.Actor.InstanceId)));
    }

    [Fact]
    public void One_save_and_no_provenance_rehomes_to_that_save()
    {
        var (zombossId, specimenId) = SaveIdentityFixtures.ZombossWithSpecimen(_testStore);

        Assert.True(Migrate());

        using var db = SaveIdentityFixtures.Open(_testStore);
        Assert.Equal(1L, Scalar(db, "SELECT player_id FROM rpg_unique_actors WHERE instance_id = $i;", ("$i", specimenId)));
        Assert.Equal("zomboss", Scalar(db, "SELECT empire_id FROM rpg_unique_actors WHERE instance_id = $i;", ("$i", specimenId)));
        Assert.NotEqual(zombossId, Scalar(db, "SELECT player_id FROM rpg_unique_actors WHERE instance_id = $i;", ("$i", specimenId)));
    }

    [Fact]
    public void A_lawn_session_provenance_rehomes_to_the_runs_save()
    {
        var (_, specimenId) = SaveIdentityFixtures.ZombossWithSpecimen(_testStore);
        var save = _store.CreatePlayer("Hero");
        var matchKey = SaveIdentityFixtures.StartRunFor(_testStore, save.Id, "m-session");
        SaveIdentityFixtures.Deploy(_testStore, specimenId, matchKey);

        Assert.True(Migrate());

        using var db = SaveIdentityFixtures.Open(_testStore);
        Assert.Equal(save.Id, Convert.ToInt64(Scalar(db, "SELECT player_id FROM rpg_unique_actors WHERE instance_id = $i;", ("$i", specimenId))));
        Assert.Equal("zomboss", Scalar(db, "SELECT empire_id FROM rpg_unique_actors WHERE instance_id = $i;", ("$i", specimenId)));
    }

    [Fact]
    public void A_lawn_xp_receipt_provenance_rehomes_after_the_session_and_column_are_gone()
    {
        // The receipt's own match_key is the last provenance to survive: board.end clears the
        // specimen's column and deletes its lawn session. The receipt row is inserted here through the
        // table's own shape because the award that writes it is tuning-gated (specimenLawnKill), and that
        // tuning is off in this assembly's bootstrap — the migration's input is the row, not how it got
        // written.
        var (_, specimenId) = SaveIdentityFixtures.ZombossWithSpecimen(_testStore);
        var save = _store.CreatePlayer("Hero");
        var matchKey = SaveIdentityFixtures.StartRunFor(_testStore, save.Id, "m-receipt");
        SaveIdentityFixtures.Deploy(_testStore, specimenId, matchKey);
        // The state board.end's recovery leaves: the specimen is back on the roster with no match key
        // and its lawn session is gone. Only the receipt keeps the match key.
        SaveIdentityFixtures.RecoverToRosterKeepingReceipt(_testStore, specimenId, matchKey);

        using (var db = SaveIdentityFixtures.Open(_testStore))
        {
            Assert.True(string.IsNullOrEmpty(Scalar(db, "SELECT match_key FROM rpg_unique_actors WHERE instance_id = $i;", ("$i", specimenId)) as string));
            Assert.Equal(0L, Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM rpg_unique_lawn_sessions WHERE instance_id = $i;", ("$i", specimenId))));
            Assert.Equal(1L, Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM rpg_unique_lawn_xp_receipts WHERE instance_id = $i;", ("$i", specimenId))));
        }

        Assert.True(Migrate());

        using (var db = SaveIdentityFixtures.Open(_testStore))
            Assert.Equal(save.Id, Convert.ToInt64(Scalar(db, "SELECT player_id FROM rpg_unique_actors WHERE instance_id = $i;", ("$i", specimenId))));
    }

    [Fact]
    public void Two_saves_and_no_provenance_retire_on_the_legacy_row()
    {
        var (zombossId, specimenId) = SaveIdentityFixtures.ZombossWithSpecimen(_testStore);
        _store.CreatePlayer("Hero");

        Assert.True(Migrate());

        using var db = SaveIdentityFixtures.Open(_testStore);
        Assert.Equal(zombossId, Convert.ToInt64(Scalar(db, "SELECT player_id FROM rpg_unique_actors WHERE instance_id = $i;", ("$i", specimenId))));
        Assert.Equal("zomboss", Scalar(db, "SELECT empire_id FROM rpg_unique_actors WHERE instance_id = $i;", ("$i", specimenId)));
        Assert.Equal("Retired", Scalar(db, "SELECT phase FROM rpg_unique_actors WHERE instance_id = $i;", ("$i", specimenId)));
    }

    [Fact]
    public void Provenance_survives_the_sweep_and_moves_the_session_with_the_specimen()
    {
        var (_, specimenId) = SaveIdentityFixtures.ZombossWithSpecimen(_testStore);
        var save = _store.CreatePlayer("Hero");
        var matchKey = SaveIdentityFixtures.StartRunFor(_testStore, save.Id, "m-sweep");
        SaveIdentityFixtures.Deploy(_testStore, specimenId, matchKey);

        Assert.True(Migrate());

        // The re-homing moved the session with the specimen, before the sweep runs.
        using (var db = SaveIdentityFixtures.Open(_testStore))
        {
            Assert.Equal(save.Id, Convert.ToInt64(Scalar(db, "SELECT player_id FROM rpg_unique_actors WHERE instance_id = $i;", ("$i", specimenId))));
            Assert.Equal(save.Id, Convert.ToInt64(Scalar(db, "SELECT player_id FROM rpg_unique_lawn_sessions WHERE instance_id = $i;", ("$i", specimenId))));
        }

        // The boot sweep runs after the migration (SE4.20's ordering): it returns the stale ActiveBound
        // specimen to Roster, but the re-homing already happened from the match key it clears.
        _store.CloseAbandonedRunsAndSweep();

        using (var db = SaveIdentityFixtures.Open(_testStore))
            Assert.Equal(save.Id, Convert.ToInt64(Scalar(db, "SELECT player_id FROM rpg_unique_actors WHERE instance_id = $i;", ("$i", specimenId))));
    }

    [Fact]
    public void A_rehomed_specimens_legacy_contract_is_released()
    {
        var (_, specimenId) = SaveIdentityFixtures.ZombossWithSpecimen(_testStore);

        using (var before = SaveIdentityFixtures.Open(_testStore))
            Assert.Equal(1L, Convert.ToInt64(Scalar(before, "SELECT bound FROM rpg_creature_contracts WHERE instance_id = $i;", ("$i", specimenId))));

        Assert.True(Migrate());

        using var db = SaveIdentityFixtures.Open(_testStore);
        Assert.Equal(0L, Convert.ToInt64(Scalar(db, "SELECT bound FROM rpg_creature_contracts WHERE instance_id = $i;", ("$i", specimenId))));
        Assert.NotNull(Scalar(db, "SELECT released_utc FROM rpg_creature_contracts WHERE instance_id = $i;", ("$i", specimenId)));
    }
}

/// <summary>
/// `save-identity` SE4.19 — step 6 (the legacy row's fate), the report the marker carries, and the
/// atom's byte-identical guarantee for the human empire's numbers.
/// </summary>
[Trait("VerificationId", "data.save-identity")]
public class SaveIdentityReportTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SaveIdentityReportTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    bool Migrate() => SaveIdentityFixtures.MigrateAgain(_testStore);

    static object? Scalar(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteScalar();
    }

    static long Count(SqliteConnection db, string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToInt64(Scalar(db, sql, parameters) ?? 0L);

    static List<string> Rows(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var rows = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var parts = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++) parts.Add(reader.IsDBNull(i) ? "∅" : reader.GetValue(i).ToString()!);
            rows.Add(string.Join("|", parts));
        }
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    string MarkerJson()
    {
        using var db = SaveIdentityFixtures.Open(_testStore);
        return (string)Scalar(db, "SELECT json FROM settings WHERE key = $k;", ("$k", SaveIdentity.MarkerKey))!;
    }

    [Fact]
    public void A_zomboss_only_legacy_row_is_archived_and_kept_with_its_codex()
    {
        var zombossId = SaveIdentityFixtures.ZombossOnly(_testStore);
        long codexBefore;
        using (var db = SaveIdentityFixtures.Open(_testStore))
            codexBefore = Count(db, "SELECT COUNT(*) FROM rpg_creature_codex WHERE player_id = $p;", ("$p", zombossId));
        Assert.True(codexBefore > 0);

        Assert.True(Migrate());

        using (var db = SaveIdentityFixtures.Open(_testStore))
        {
            Assert.NotNull(Scalar(db, "SELECT archived_utc FROM players WHERE id = $p;", ("$p", zombossId)));
            Assert.Equal(codexBefore, Count(db, "SELECT COUNT(*) FROM rpg_creature_codex WHERE player_id = $p;", ("$p", zombossId)));
        }
    }

    [Fact]
    public void A_legacy_row_that_is_a_save_keeps_both_empires_and_is_not_archived()
    {
        var save = _store.CreatePlayer("Zomboss");

        Assert.True(Migrate());

        Assert.True(_store.EmpiresOf(save.Id).Count >= 2);
        using var db = SaveIdentityFixtures.Open(_testStore);
        Assert.True(Scalar(db, "SELECT archived_utc FROM players WHERE id = $p;", ("$p", save.Id)) is null or DBNull);
    }

    [Fact]
    public void The_marker_report_carries_every_field_the_spec_lists()
    {
        var zombossId = SaveIdentityFixtures.ZombossOnly(_testStore);

        Assert.True(Migrate());

        using var doc = JsonDocument.Parse(MarkerJson());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("savesSeeded").GetArrayLength() >= 1);
        Assert.True(root.GetProperty("legacyZombossFound").GetBoolean());
        Assert.Equal(zombossId, root.GetProperty("legacyZombossId").GetInt64());
        Assert.False(root.GetProperty("legacyZombossIsSave").GetBoolean());
        Assert.True(root.GetProperty("legacyDecision").GetString()!.Length > 0);
        Assert.True(root.TryGetProperty("backupPath", out _));
        Assert.True(root.TryGetProperty("specimensStampedBySave", out _));
        Assert.True(root.TryGetProperty("zombossSpecimensKeptOnSave", out _));
        Assert.True(root.TryGetProperty("rehomedByProvenance", out _));
        Assert.True(root.TryGetProperty("rehomedByOnlySave", out _));
        Assert.True(root.TryGetProperty("unattributedSpecimenIds", out var unattributed));
        Assert.Equal(JsonValueKind.Array, unattributed.ValueKind);
        Assert.True(root.TryGetProperty("legacyArchivedUtc", out var archived));
        Assert.NotEqual(JsonValueKind.Null, archived.ValueKind);
        var tierA = root.GetProperty("tierA");
        Assert.Contains(tierA.EnumerateArray(), t => t.GetProperty("table").GetString() == "rpg_actor_progression");
        Assert.Contains(tierA.EnumerateArray(), t => t.GetProperty("table").GetString() == "rpg_xp_ledger");
    }

    [Fact]
    public void Human_progression_rows_and_allocations_are_byte_identical()
    {
        var save = _store.CreatePlayer("Hero");
        _store.SeedRpgProgressionDemo(save.Id);
        _store.SaveAllocation(AllocationScope.Commander, $"player:{save.Id}",
            AptitudeAllocation.Single(AllocationScope.Commander, AptitudeCatalog.All[0].Id, 3));

        List<string> progressionBefore, allocationsBefore;
        using (var db = SaveIdentityFixtures.Open(_testStore))
        {
            progressionBefore = Rows(db, "SELECT kind, type_id, level, xp, highest_level, demotion_count, through_ledger_id, scope_key FROM rpg_actor_progression WHERE save_id = $p;", ("$p", save.Id));
            allocationsBefore = Rows(db, "SELECT scope, scope_key, aptitude_id, points FROM rpg_aptitude_allocation WHERE scope_key = $k;", ("$k", $"player:{save.Id}"));
        }
        Assert.NotEmpty(progressionBefore);

        Assert.True(Migrate());

        using (var db = SaveIdentityFixtures.Open(_testStore))
        {
            Assert.Equal(progressionBefore,
                Rows(db, "SELECT kind, type_id, level, xp, highest_level, demotion_count, through_ledger_id, scope_key FROM rpg_actor_progression WHERE save_id = $p;", ("$p", save.Id)));
            Assert.Equal(allocationsBefore,
                Rows(db, "SELECT scope, scope_key, aptitude_id, points FROM rpg_aptitude_allocation WHERE scope_key = $k;", ("$k", $"player:{save.Id}")));
        }
    }

    [Fact]
    public void An_injected_failure_at_step_five_rolls_back_schema_rows_and_marker()
    {
        SaveIdentityFixtures.ZombossOnly(_testStore);
        _store.SeedRpgProgressionDemo(1);
        long progressionBefore;
        using (var db = SaveIdentityFixtures.Open(_testStore))
            progressionBefore = Count(db, "SELECT COUNT(*) FROM rpg_actor_progression;");
        Assert.True(progressionBefore > 0);

        Assert.Throws<InvalidOperationException>(() => SaveIdentityFixtures.MigrateAgain(_testStore,
            failAfterStep: 5));

        using (var check = SaveIdentityFixtures.Open(_testStore))
        {
            Assert.Equal(0L, Count(check, "SELECT COUNT(*) FROM settings WHERE key = $k;", ("$k", SaveIdentity.MarkerKey)));
            Assert.Equal(0L, Count(check, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='rpg_actor_progression__pre_save_identity';"));
            Assert.Equal(progressionBefore, Count(check, "SELECT COUNT(*) FROM rpg_actor_progression;"));
            Assert.Equal(0L, Count(check, "SELECT COUNT(*) FROM players WHERE archived_utc IS NOT NULL;"));
        }
    }
}
