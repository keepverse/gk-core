using FusionRpg.Core.Saves;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// debug-origin D1 — "a debug API may trigger a real operation, but its result must never be mistaken
/// for gameplay". <see cref="RpgStore.EnsureUniqueActorForAudit"/> (the RPG Server Debug derived-sheet
/// fixture, whose only production caller is <c>POST /api/debug/derived-audit-actor</c>) used to write an
/// ordinary <c>rpg_unique_actors</c> row, so a specimen named <c>derived-audit</c> appeared in the
/// player's own roster beside real gameplay specimens — a debug fixture wearing a gameplay costume.
///
/// <para>These tests pin BOTH halves, because either alone would be the wrong fix: the marker must
/// actually keep the row out of <see cref="RpgStore.ListUniqueActors(long)"/>, and the debug capability
/// must survive (deleting the fixture, or making it unresolvable, is a different and forbidden fix —
/// see the live-probe standard's "adapter-wrap the real endpoint, never re-implement it").
/// </para>
/// </summary>
public sealed class DebugSeededSpecimenRosterTests : IDisposable
{
    // The debug fixture's own id, duplicated here on purpose: a test that reads the constant out of the
    // server would re-derive the fixture's name from the thing under test. This is the id a user
    // actually saw in their roster.
    const string DebugSpecimenId = "derived-audit";

    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _saveId;

    public DebugSeededSpecimenRosterTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _saveId = _store.GetCurrentPlayerId();
    }

    public void Dispose() => _testStore.Dispose();

    /// <summary>A gameplay specimen on the same roster, minted through the ORDINARY path
    /// (<see cref="RpgStore.CreateUniqueActor"/>), never the audit path.</summary>
    string SeedGameplaySpecimen() => _store.CreateUniqueActor(_saveId, "plant", 3).InstanceId;

    [Fact]
    public void A_debug_seeded_specimen_is_absent_from_the_player_roster()
    {
        var real = SeedGameplaySpecimen();
        _store.EnsureUniqueActorForAudit(_saveId, DebugSpecimenId, "plant", 9, 80);

        var roster = _store.ListUniqueActors(_saveId);

        // The gameplay specimen is still there, so this is a filter on the debug row and NOT a roster
        // that silently stopped listing anything.
        Assert.Contains(real, roster.Items.Select(a => a.InstanceId));
        Assert.DoesNotContain(DebugSpecimenId, roster.Items.Select(a => a.InstanceId));
    }

    /// <summary>The capability half: the fixture is still a REAL actor, resolvable by id, so the derived
    /// coverage audit and <c>/api/actors/{id}/sheet</c> keep working. Excluding it from the roster must
    /// not have made it unreachable.</summary>
    [Fact]
    public void A_debug_seeded_specimen_is_still_resolvable_by_id()
    {
        _store.EnsureUniqueActorForAudit(_saveId, DebugSpecimenId, "plant", 9, 80);

        var actor = _store.GetUniqueActor(DebugSpecimenId);

        Assert.NotNull(actor);
        Assert.Equal(80, actor!.Level);
        Assert.Equal(_saveId, actor.PlayerId);
    }

    /// <summary>The marker is DATA, not a name: a debug fixture under ANY id is excluded, and a
    /// gameplay specimen under the very id a fixture once used is included. Without this, a filter
    /// written as <c>instance_id &lt;&gt; 'derived-audit'</c> would pass both tests above and still be
    /// wrong — it would leak the next debug fixture and hide a real specimen.</summary>
    [Fact]
    public void The_roster_filter_follows_the_marker_not_the_name()
    {
        var gameplayWithTheOldName = SeedGameplaySpecimen();   // a GUID, not "derived-audit"
        _store.EnsureUniqueActorForAudit(_saveId, "some-other-debug-fixture", "plant", 9, 80);

        var roster = _store.ListUniqueActors(_saveId).Items.Select(a => a.InstanceId).ToList();

        Assert.Contains(gameplayWithTheOldName, roster);
        Assert.DoesNotContain("some-other-debug-fixture", roster);
    }

    /// <summary>"Ensure" means a KNOWN state, so a RE-RUN of the debug seed must re-stamp the marker. This
    /// is the only migration a data-driven marker can honestly perform: a row written by an older build
    /// keeps <c>debug_seeded = 0</c> until the debug path next touches it, and the store cannot classify a
    /// row it did not write. Pinning it here means the re-stamp is a guaranteed behaviour of the debug
    /// path, not an accident of its SQL.</summary>
    [Fact]
    public void Re_running_the_debug_seed_re_stamps_an_already_written_row()
    {
        _store.EnsureUniqueActorForAudit(_saveId, DebugSpecimenId, "plant", 9, 80);

        // Second run with a different level: the same upsert path a debug workflow repeats.
        _store.EnsureUniqueActorForAudit(_saveId, DebugSpecimenId, "plant", 9, 12);

        var actor = _store.GetUniqueActor(DebugSpecimenId);
        Assert.Equal(12, actor!.Level);                      // the upsert still works…
        Assert.DoesNotContain(DebugSpecimenId,               // …and the roster is still clean.
            _store.ListUniqueActors(_saveId).Items.Select(a => a.InstanceId));
    }

    /// <summary>The legacy case, and the one a real save is actually in: a debug row written by an older
    /// build carries no marker, because the column did not exist. Re-running the debug seed must move it
    /// out of the roster — this is what makes the UPDATE branch of the upsert load-bearing, and the
    /// INSERT branch alone would leave every already-seeded save showing the fixture forever.</summary>
    [Fact]
    public void A_debug_row_an_older_build_wrote_is_re_stamped_out_of_the_roster_on_the_next_seed()
    {
        using var legacy = DataTestStore.CreateWithPreInitHot(seed =>
        {
            using var create = seed.CreateCommand();
            create.CommandText = """
                CREATE TABLE rpg_unique_actors (
                  instance_id TEXT NOT NULL PRIMARY KEY,
                  player_id INTEGER NOT NULL,
                  side TEXT NOT NULL,
                  type_id INTEGER NOT NULL,
                  phase TEXT NOT NULL,
                  level INTEGER NOT NULL DEFAULT 1,
                  xp INTEGER NOT NULL DEFAULT 0,
                  match_key TEXT,
                  last_ptr TEXT,
                  deploy_correlation_id TEXT,
                  revision INTEGER NOT NULL DEFAULT 0,
                  created_utc TEXT NOT NULL,
                  updated_utc TEXT NOT NULL,
                  empire_id TEXT
                );
                """;
            create.ExecuteNonQuery();
        });

        var saveId = legacy.Store.GetCurrentPlayerId();
        var empire = legacy.Store.HumanEmpireOf(saveId).Value;
        using (var conn = SqliteConnectionFactory.Open(legacy.Store.HotPath))
        using (var insert = conn.CreateCommand())
        {
            // The row an older build wrote: a debug fixture, unmarked, indistinguishable from gameplay.
            insert.CommandText = """
                INSERT INTO rpg_unique_actors(
                  instance_id, player_id, empire_id, side, type_id, phase, level, xp,
                  revision, created_utc, updated_utc)
                VALUES($id, $pid, $emp, 'plant', 9, 'Roster', 80, 0, 0, $now, $now);
                """;
            insert.Parameters.AddWithValue("$id", DebugSpecimenId);
            insert.Parameters.AddWithValue("$pid", saveId);
            insert.Parameters.AddWithValue("$emp", empire);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
            insert.ExecuteNonQuery();
        }

        // Before the seed: the legacy row is a roster row, exactly as a user saw it.
        Assert.Contains(DebugSpecimenId, legacy.Store.ListUniqueActors(saveId).Items.Select(a => a.InstanceId));

        legacy.Store.EnsureUniqueActorForAudit(saveId, DebugSpecimenId, "plant", 9, 80);

        // After: gone from the roster, still resolvable by id -- the capability survived.
        Assert.DoesNotContain(DebugSpecimenId, legacy.Store.ListUniqueActors(saveId).Items.Select(a => a.InstanceId));
        Assert.NotNull(legacy.Store.GetUniqueActor(DebugSpecimenId));
    }

    /// <summary>The schema half: <c>debug_seeded</c> is ADDITIVE and DEFAULTED, so a store that already
    /// held roster rows opens against the widened table and those rows still read as gameplay rows. A
    /// column added without a default would either take every existing save's roster down with it or
    /// silently hide every specimen a player already owned — the migration's whole risk surface.</summary>
    [Fact]
    public void A_save_predating_the_column_keeps_its_gameplay_rows_in_the_roster()
    {
        using var legacy = DataTestStore.CreateWithPreInitHot(seed =>
        {
            // The near-miss shape: `empire_id` present (SE4.14), `debug_seeded` not yet.
            using var create = seed.CreateCommand();
            create.CommandText = """
                CREATE TABLE rpg_unique_actors (
                  instance_id TEXT NOT NULL PRIMARY KEY,
                  player_id INTEGER NOT NULL,
                  side TEXT NOT NULL,
                  type_id INTEGER NOT NULL,
                  phase TEXT NOT NULL,
                  level INTEGER NOT NULL DEFAULT 1,
                  xp INTEGER NOT NULL DEFAULT 0,
                  match_key TEXT,
                  last_ptr TEXT,
                  deploy_correlation_id TEXT,
                  revision INTEGER NOT NULL DEFAULT 0,
                  created_utc TEXT NOT NULL,
                  updated_utc TEXT NOT NULL,
                  empire_id TEXT
                );
                """;
            create.ExecuteNonQuery();
        });

        var saveId = legacy.Store.GetCurrentPlayerId();
        var empire = legacy.Store.HumanEmpireOf(saveId).Value;
        using (var conn = SqliteConnectionFactory.Open(legacy.Store.HotPath))
        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO rpg_unique_actors(
                  instance_id, player_id, empire_id, side, type_id, phase, level, xp,
                  revision, created_utc, updated_utc)
                VALUES('pre-marker-specimen', $pid, $emp, 'plant', 3, 'Roster', 5, 0, 0, $now, $now);
                """;
            insert.Parameters.AddWithValue("$pid", saveId);
            insert.Parameters.AddWithValue("$emp", empire);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
            insert.ExecuteNonQuery();
        }

        var roster = legacy.Store.ListUniqueActors(saveId).Items.Select(a => a.InstanceId).ToList();

        Assert.Contains("pre-marker-specimen", roster);
    }
}
