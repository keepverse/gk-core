using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// combat-ai `replay-identity` (spec-replay-identity.md §4, CAI2.2): the nullable
/// <c>rpg_web_match_log.combat_ai_profile</c> column — the DENIED Data path every combat-ai lane
/// was waiting on, and the one the five-step pin resolution reads.
/// <para>
/// <b>Scope: this is a store test, not a policy test.</b> The store is opaque text — it writes the
/// stamp, reads it back, and never interprets one character of it. Which version a stamp names, and
/// whether a host can still supply it, is <c>CombatAiProfilePin</c>'s decision (Server side) and
/// <c>CombatAiProfileIdentity</c>'s (Core side); duplicating either here would be a second
/// implementation of the same rule. The stamp below is built through the real
/// <see cref="ContentHashStamp"/> rather than hand-written so the store is fed the true grammar
/// without this file owning that grammar.
/// </para>
/// <para>In memory throughout (<c>testing-standard.md</c> R1/R2, <c>guard-test-substrate.ps1</c>):
/// nothing here writes a file, so there is no directory to clean up.</para>
/// </summary>
public class WebMatchLogProfileStampTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WebMatchLogProfileStampTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    /// <summary>The durable compact form, built by the mechanism that owns it.</summary>
    static string Stamp(int version, string hash, params string[] partIds) =>
        new ContentHashStamp(version, hash,
            partIds.ToDictionary(id => id, _ => "0123456789abcdef", StringComparer.Ordinal)).ToCompact();

    static readonly string SomeStamp = Stamp(2, "fedcba9876543210", "lawn/default", "*/default");

    (bool Created, WebMatchLogEntry Entry) Append(
        string correlationId, string matchKey, string? combatAiProfile = null, long playerId = 1)
    {
        var (created, entry) = _store.AppendWebMatchLog(
            playerId, correlationId, matchKey, "{\"waveId\":\"rift-skirmish\"}", 42,
            BattleRuleset.EngineVersion, BattleRuleset.RulesetVersion, SeededRng.RngAlgoVersion,
            combatAiProfile: combatAiProfile);
        Assert.True(created);
        return (created, entry);
    }

    /// <summary>
    /// Every existing caller omits <c>combatAiProfile</c> (it is the trailing optional parameter) and
    /// must keep compiling AND keep meaning the same thing: NULL, the pre-combat-ai shape. If a later
    /// edit ever made the omission default to the current profile, this row would be the lie.
    /// </summary>
    [Fact]
    public void An_append_that_names_no_profile_stores_null()
    {
        var entry = Append("corr-unstamped", "web-unstamped").Entry;

        Assert.Null(entry.CombatAiProfile);

        var reread = _store.TryGetWebMatchLog(1, "corr-unstamped");
        Assert.NotNull(reread);
        Assert.Null(reread!.CombatAiProfile);
    }

    /// <summary>
    /// The pin reads the stamp from BOTH read paths: <see cref="RpgStore.TryGetWebMatchLog"/> for the
    /// two correlation-replay branches and <see cref="RpgStore.ListUnresolvedWebMatches"/> for the
    /// boot sweep. A column added to the INSERT and forgotten in one SELECT would leave the sweep
    /// refusing or trusting a null it never had — so both are asserted, byte-for-byte.
    /// </summary>
    [Fact]
    public void A_stamped_row_round_trips_byte_for_byte_through_both_read_paths()
    {
        var appended = Append("corr-stamped", "web-stamped", SomeStamp).Entry;
        Assert.Equal(SomeStamp, appended.CombatAiProfile); // read back through the append's own echo

        var byCorrelation = _store.TryGetWebMatchLog(1, "corr-stamped");
        Assert.NotNull(byCorrelation);
        Assert.Equal(SomeStamp, byCorrelation!.CombatAiProfile);

        // Still unresolved (no ingest), so the boot sweep's own query must carry the column too.
        var unresolved = Assert.Single(_store.ListUnresolvedWebMatches());
        Assert.Equal(SomeStamp, unresolved.CombatAiProfile);
        Assert.Equal("web-stamped", unresolved.MatchKey);

        // The neighbouring stamps are untouched by the addition: a new column is additive, and the
        // pair stays distinct so neither can be mistaken for the other.
        Assert.Null(unresolved.ProfileId);
        Assert.NotEqual(unresolved.CombatAiProfile, unresolved.ContentHash);
    }

    /// <summary>
    /// Idempotent addition on an EXISTING database, preserving the rows already in it — the shape
    /// every shipped save has. The legacy table below is the pre-column DDL and the row is written
    /// before <c>Init()</c>, so the migration path is exercised exactly as a real upgrade runs it
    /// (a fresh database would never prove it: its CREATE already contains the column).
    /// </summary>
    [Fact]
    public void The_column_is_added_once_on_an_existing_database_and_earlier_rows_keep_null()
    {
        using var upgraded = DataTestStore.CreateWithPreInitHot(seed =>
        {
            Exec(seed, """
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
                  UNIQUE(player_id, correlation_id)
                );
                """);
            Exec(seed, """
                INSERT INTO rpg_web_match_log(
                  player_id, correlation_id, match_key, setup_json, seed,
                  engine_version, ruleset_version, rng_algo_version, t)
                VALUES(1,'corr-legacy','web-legacy','{}','7',1,5,1,'2026-01-01T00:00:00.0000000Z');
                """);
        });

        // The pre-existing row survives the ADD COLUMN with no stamp and no lost fields.
        var legacy = upgraded.Store.TryGetWebMatchLog(1, "corr-legacy");
        Assert.NotNull(legacy);
        Assert.Null(legacy!.CombatAiProfile);
        Assert.Equal("web-legacy", legacy.MatchKey);
        Assert.Equal(7UL, legacy.Seed);

        // Reopening is the idempotency: a second process over the same database adds nothing and
        // drops nothing. A non-idempotent migration would throw or duplicate here.
        using var reopened = upgraded.Reopen();
        var again = reopened.TryGetWebMatchLog(1, "corr-legacy");
        Assert.NotNull(again);
        Assert.Null(again!.CombatAiProfile);

        using (var probe = SqliteConnectionFactory.Open(reopened.HotPath))
        {
            var count = ScalarInt(probe, "SELECT COUNT(*) FROM pragma_table_info('rpg_web_match_log') WHERE name='combat_ai_profile';");
            Assert.Equal(1, count);

            // Nullable: the column carries no NOT NULL, which is what lets an old row read as NULL
            // rather than forcing a fabricated stamp onto history.
            var notNull = ScalarInt(probe, "SELECT \"notnull\" FROM pragma_table_info('rpg_web_match_log') WHERE name='combat_ai_profile';");
            Assert.Equal(0, notNull);
        }
    }

    static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static long ScalarInt(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
