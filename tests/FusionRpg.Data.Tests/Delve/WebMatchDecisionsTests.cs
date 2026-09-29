using FusionRpg.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Delve;

/// <summary>
/// D2.15 (spec-delve-battle-profile.md §4b) — `rpg_web_match_log` gains `profile_id`;
/// `RpgStore.WriteWebMatchDecisions` is the column's first writer for `decisions_json` (B21's own
/// column, never written by anything in `src/` before this task).
/// </summary>
public class WebMatchDecisionsTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WebMatchDecisionsTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    RpgStore NewStore() => _store;

    [Fact]
    public void WriteWebMatchDecisions_round_trips_a_trace_and_AppendWebMatchLog_records_the_profile_id()
    {
        var store = NewStore();
        var (created, entry) = store.AppendWebMatchLog(
            playerId: 1, correlationId: "delve:1:0:0:p0", matchKey: "delve-1-0-0-p0",
            setupJson: "{\"waveId\":\"delve-encounter\"}", seed: 42UL,
            engineVersion: 1, rulesetVersion: 1, rngAlgoVersion: 1, profileId: "delve");
        Assert.True(created);
        Assert.Equal("delve", entry.ProfileId);
        Assert.Null(entry.DecisionsJson); // no writer has run yet

        var trace = "{\"decisions\":[{\"tick\":0,\"actionId\":\"basic-attack\"}]}";
        store.WriteWebMatchDecisions(entry.Id, trace);

        var reloaded = store.TryGetWebMatchLog(playerId: 1, correlationId: "delve:1:0:0:p0");
        Assert.NotNull(reloaded);
        Assert.Equal(trace, reloaded!.DecisionsJson);
        Assert.Equal("delve", reloaded.ProfileId); // untouched by the decisions write
    }

    [Fact]
    public void A_non_delve_match_carries_no_profile_id_the_default_stays_null()
    {
        var store = NewStore();
        var (_, entry) = store.AppendWebMatchLog(
            playerId: 2, correlationId: "exp:1:0", matchKey: "exp-1-0",
            setupJson: "{}", seed: 7UL, engineVersion: 1, rulesetVersion: 1, rngAlgoVersion: 1);

        Assert.Null(entry.ProfileId);
    }

    [Fact]
    public void An_old_schema_database_missing_profile_id_migrates_and_keeps_its_existing_row()
    {
        // The exact pre-D2.15 shape: every rpg_web_match_log column up to decisions_json, minted by
        // hand rather than through RpgStore, matching EligibilityAxisMigrationTests' own "a real
        // CREATE TABLE that never named them" precedent for EnsureColumn migrations. Built in memory
        // via CreateWithPreInitHot: the legacy table shape is seeded before Init.
        using var test = DataTestStore.CreateWithPreInitHot(seed =>
        {
            using var cmd = seed.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE rpg_web_match_log (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  player_id INTEGER NOT NULL,
                  correlation_id TEXT NOT NULL,
                  match_key TEXT NOT NULL UNIQUE,
                  setup_json TEXT NOT NULL,
                  seed TEXT NOT NULL,
                  engine_version INTEGER NOT NULL,
                  ruleset_version INTEGER NOT NULL,
                  rng_algo_version INTEGER NOT NULL,
                  environment_stamp TEXT,
                  sweep_refused TEXT,
                  run_id INTEGER,
                  t TEXT NOT NULL,
                  content_hash TEXT,
                  decisions_json TEXT,
                  UNIQUE(player_id, correlation_id)
                );
                INSERT INTO rpg_web_match_log
                  (player_id, correlation_id, match_key, setup_json, seed, engine_version,
                   ruleset_version, rng_algo_version, t)
                VALUES (9, 'pre-existing-corr', 'pre-existing-key', '{}', '99', 1, 1, 1, '2026-01-01T00:00:00Z');
                """;
            cmd.ExecuteNonQuery();
        });

        var store = test.Store; // Init()'s EnsureColumn must add profile_id without disturbing the row above
        var migrated = store.TryGetWebMatchLog(playerId: 9, correlationId: "pre-existing-corr");

        Assert.NotNull(migrated);
        Assert.Equal("pre-existing-key", migrated!.MatchKey);
        Assert.Null(migrated.ProfileId); // the new column, absent on the old row, reads back as null rather than throwing
    }
}
