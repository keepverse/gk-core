using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// C4a: rpg_web_match_log — the durable idempotency anchor written BEFORE ingest — plus the
/// dedicated explicit-player single-transaction web insert and the boot-sweep query for the
/// crash window between log append and report ingest.
/// </summary>
public class WebMatchStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WebMatchStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static BattleSetup Setup() => new()
    {
        WaveId = "rift-skirmish",
        Squad = new[]
        {
            new BattleActorSetup
            {
                Key = "squad:0", Side = "squad", SpeciesId = "web-species", TypeId = 10_001, Level = 6,
                MaxHp = BattleRuleset.BaseHp(6), Atk = BattleRuleset.BaseAtk(6), Defense = BattleRuleset.BaseDefense(6)
            }
        },
        Wave = new[]
        {
            new BattleActorSetup
            {
                Key = "wave:0", Side = "wave", SpeciesId = "web-species", TypeId = 10_002, Level = 1,
                MaxHp = BattleRuleset.BaseHp(1), Atk = BattleRuleset.BaseAtk(1), Defense = BattleRuleset.BaseDefense(1)
            }
        }
    };

    static List<EventEnvelope> StampedEvents(string matchKey, ulong seed)
    {
        var events = BattleReportEmitter.Emit(BattleEngine.Resolve(Setup(), seed), matchKey).ToList();
        // The service stamps strictly monotonic t at ingest; tests play that role here.
        var t0 = DateTime.UtcNow;
        for (var i = 0; i < events.Count; i++)
            events[i].T = t0.AddMilliseconds(i).ToString("o");
        return events;
    }

    WebMatchLogEntry Append(long playerId, string correlation, string matchKey, ulong seed = 42)
    {
        var (created, entry) = _store.AppendWebMatchLog(
            playerId, correlation, matchKey, "{\"waveId\":\"rift-skirmish\"}", seed,
            BattleRuleset.EngineVersion, BattleRuleset.RulesetVersion, SeededRng.RngAlgoVersion);
        Assert.True(created);
        return entry;
    }

    [Fact]
    public void Log_appends_before_ingest_and_sweeps_the_crash_window()
    {
        var matchKey = "web-crash-1";
        Append(1, "corr-1", matchKey, seed: 7);

        // Crash window: log exists, no run row yet — the boot sweep must surface it.
        var unresolved = Assert.Single(_store.ListUnresolvedWebMatches());
        Assert.Equal(matchKey, unresolved.MatchKey);
        Assert.Equal(7UL, unresolved.Seed);
        Assert.Equal("{\"waveId\":\"rift-skirmish\"}", unresolved.SetupJson);

        // Re-ingest (deterministic from setup+seed) links the run and clears the sweep.
        _store.InsertWebMatchEvents(1, matchKey, StampedEvents(matchKey, 7));
        Assert.Empty(_store.ListUnresolvedWebMatches());
        var entry = _store.TryGetWebMatchLog(1, "corr-1");
        Assert.NotNull(entry!.RunId);
        var run = _store.ListRuns().Single(r => r.MatchKey == matchKey);
        Assert.Equal(RpgConstants.GameIdWebRpg, run.Game);
        Assert.Equal(entry.RunId, run.Id);
    }

    [Fact]
    public void Correlation_is_unique_per_player_and_replays_return_the_stored_row()
    {
        var first = Append(1, "corr-dup", "web-a");
        var (createdAgain, replay) = _store.AppendWebMatchLog(
            1, "corr-dup", "web-DIFFERENT", "{}", 99,
            BattleRuleset.EngineVersion, BattleRuleset.RulesetVersion, SeededRng.RngAlgoVersion);

        Assert.False(createdAgain);
        Assert.Equal(first.MatchKey, replay.MatchKey); // stored row wins, nothing overwritten
        Assert.Equal(first.Seed, replay.Seed);

        var p2 = _store.CreatePlayer("second");
        var (createdOther, _) = _store.AppendWebMatchLog(
            p2.Id, "corr-dup", "web-b", "{}", 1,
            BattleRuleset.EngineVersion, BattleRuleset.RulesetVersion, SeededRng.RngAlgoVersion);
        Assert.True(createdOther); // uniqueness is per player
    }

    [Fact]
    public void Web_insert_honors_the_explicit_player()
    {
        var p2 = _store.CreatePlayer("web-player");
        var matchKey = "web-explicit-1";
        Append(p2.Id, "corr-p2", matchKey);
        _store.InsertWebMatchEvents(p2.Id, matchKey, StampedEvents(matchKey, 42));

        var run = _store.ListRuns(p2.Id).Single(r => r.MatchKey == matchKey);
        Assert.Equal(p2.Id, run.PlayerId); // never mis-credited to current_player_id
        Assert.DoesNotContain(_store.ListRuns(), r => r.MatchKey == matchKey); // not under player 1
    }

    [Fact]
    public void Environment_stamp_round_trips_and_defaults_to_null()
    {
        var (created, stamped) = _store.AppendWebMatchLog(
            1, "corr-stamp", "web-stamp-1", "{}", 5,
            BattleRuleset.EngineVersion, BattleRuleset.RulesetVersion, SeededRng.RngAlgoVersion,
            BattleEnvironment.Stamp);
        Assert.True(created);
        Assert.Equal(BattleEnvironment.Stamp, stamped.EnvironmentStamp);

        // Both read paths must return the stored stamp — the boot sweep's cross-arch
        // guard compares it against BattleEnvironment.Stamp before re-resolving.
        Assert.Equal(BattleEnvironment.Stamp, _store.TryGetWebMatchLog(1, "corr-stamp")!.EnvironmentStamp);
        Assert.Equal(BattleEnvironment.Stamp,
            _store.ListUnresolvedWebMatches().Single(e => e.MatchKey == "web-stamp-1").EnvironmentStamp);

        var unstamped = Append(1, "corr-unstamped", "web-stamp-2"); // helper omits the stamp
        Assert.Null(unstamped.EnvironmentStamp);
        Assert.Null(_store.TryGetWebMatchLog(1, "corr-unstamped")!.EnvironmentStamp);
    }

    [Fact]
    public void Refused_rows_leave_the_sweep_window_for_good()
    {
        var keep = Append(1, "corr-keep", "web-healable");
        var refuse = Append(1, "corr-refuse", "web-refused");
        Assert.Equal(2, _store.ListUnresolvedWebMatches().Count);

        _store.MarkWebMatchSweepRefused(refuse.Id, "platform 'X64/linux/net8' != 'X64/windows/net8'");

        // Gone from the sweep, but still on disk with its reason — forensics, not deletion.
        var remaining = Assert.Single(_store.ListUnresolvedWebMatches());
        Assert.Equal(keep.MatchKey, remaining.MatchKey);
        var stored = _store.TryGetWebMatchLog(1, "corr-refuse");
        Assert.NotNull(stored);
        Assert.Contains("platform", stored!.SweepRefused!);

        // Idempotent: re-marking must not overwrite the original reason/time.
        _store.MarkWebMatchSweepRefused(refuse.Id, "second reason");
        Assert.DoesNotContain("second reason", _store.TryGetWebMatchLog(1, "corr-refuse")!.SweepRefused!);
    }

    [Fact]
    public void Refused_rows_cannot_starve_the_sweep_window()
    {
        // The regression that motivated the terminal state: the sweep query is
        // ORDER BY id ASC LIMIT n, so unmarked refusals at the low end of the id space would
        // crowd every newer row out of the window and crash recovery would silently die.
        for (var i = 0; i < 12; i++)
            _store.MarkWebMatchSweepRefused(Append(1, $"corr-old-{i}", $"web-old-{i}").Id, "stale ruleset");

        var fresh = Append(1, "corr-fresh", "web-fresh");
        var window = _store.ListUnresolvedWebMatches(limit: 10);

        Assert.Contains(window, e => e.MatchKey == fresh.MatchKey);
        Assert.DoesNotContain(window, e => e.MatchKey.StartsWith("web-old-", StringComparison.Ordinal));
    }

    [Fact]
    public void Old_databases_gain_the_stamp_column_and_legacy_rows_read_null()
    {
        // A pre-stamp database: rpg_web_match_log exists WITHOUT environment_stamp and already
        // holds a row. Init() must migrate it (EnsureColumn) so old rows read null — the sweep
        // guard treats null as "trust the version columns alone", never a crash or a refusal.
        // Built in memory via CreateWithPreInitHot: the legacy table shape is seeded before Init.
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
                  run_id INTEGER,
                  t TEXT NOT NULL,
                  UNIQUE(player_id, correlation_id)
                );
                INSERT INTO rpg_web_match_log(player_id, correlation_id, match_key, setup_json, seed,
                  engine_version, ruleset_version, rng_algo_version, t)
                VALUES(1,'corr-legacy','web-legacy','{}','9',1,1,1,'2026-01-01T00:00:00Z');
                """;
            cmd.ExecuteNonQuery();
        });

        var store = test.Store;
        var legacy = store.TryGetWebMatchLog(1, "corr-legacy");
        Assert.NotNull(legacy);
        Assert.Null(legacy!.EnvironmentStamp);
        Assert.Equal("web-legacy", legacy.MatchKey);

        var (created, entry) = store.AppendWebMatchLog(
            1, "corr-new", "web-migrated", "{}", 2,
            BattleRuleset.EngineVersion, BattleRuleset.RulesetVersion, SeededRng.RngAlgoVersion,
            "test-stamp");
        Assert.True(created);
        Assert.Equal("test-stamp", entry.EnvironmentStamp);
    }

    [Fact]
    public void Web_insert_is_one_transaction()
    {
        var matchKey = "web-atomic-1";
        Append(1, "corr-atomic", matchKey);
        var events = StampedEvents(matchKey, 42);
        // A tail event that cannot be stored (System.Text.Json refuses NaN). This used to be a second board.start, which
        // violated the unique match_key; since lawn-combat-wire L-N29 a repeated board.start fills the existing run instead.
        events.Add(new EventEnvelope { T = DateTime.UtcNow.ToString("o"), Game = events[0].Game, Kind = "web.poison", MatchKey = matchKey, Payload = new { n = double.NaN } });

        Assert.ThrowsAny<Exception>(() => _store.InsertWebMatchEvents(1, matchKey, events));
        Assert.DoesNotContain(_store.ListRuns(), r => r.MatchKey == matchKey);
        Assert.Single(_store.ListUnresolvedWebMatches()); // still swept next boot
    }
}
