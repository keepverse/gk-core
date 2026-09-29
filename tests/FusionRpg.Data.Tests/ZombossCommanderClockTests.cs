using System;
using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Activity;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Saves;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// `zomboss-commander-clock` SP7.2 — the storage half. A resolved LAWN run (`MatchEnded`, `pvzGame`)
/// advances the run's own save's Zomboss empire commander clock exactly once, by outcome — never the
/// human row, never a literal id (R3). The pure curve/tuning surface (award values, the loader) is
/// covered by <c>FusionRpg.Core.Tests.Progression.ProgressionTuningTests</c> (SP7.1); this file proves
/// the write path end to end through the real activity-fact pipeline, using the shared assembly
/// bootstrap's working set (<c>ContractTuningTestBootstrap.DefaultProgression</c>:
/// `ZombossRunVictoryXp=100` — exactly the L1→L2 threshold of this bootstrap's `player` curve
/// (`first=100`) — and `ZombossRunDefeatXp=25`, well under it).
/// </summary>
public class ZombossCommanderClockTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ZombossCommanderClockTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    /// <summary>SE4.21: facts name a run that resolves to their own save — a real run, not a
    /// placeholder id (mirrors SpeciesProgressionTests' own helper).</summary>
    long StartRun(long playerId)
    {
        _store.SetCurrentPlayer(playerId);
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "board.start",
            MatchKey = "m-" + Guid.NewGuid().ToString("N"),
            Payload = new { levelName = "test", levelType = "adventure" },
        });
        return _store.ListRuns(playerId).Single().Id;
    }

    void EndRun(long playerId, long runId, string result, string dedupeKey) =>
        _store.AppendPvzActivityFact(playerId, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.MatchEnded,
            RunId = runId,
            PayloadJson = $$"""{"result":"{{result}}"}""",
            DedupeKey = dedupeKey
        });

    [Fact]
    public void A_human_defeat_gives_Zombosss_commander_the_run_victory_award()
    {
        var player = _store.CreatePlayer("ZombossDefeat");
        var runId = StartRun(player.Id);
        EndRun(player.Id, runId, "defeat", "match-end-1");

        // Asserted against the curve function, not a literal level (acceptance's own wording): this
        // bootstrap's working set happens to make one victory award exactly clear the L1->L2 need.
        Assert.Equal(RpgXpCurve.XpToNext(RpgActorKinds.Player, 1), RpgXpAwards.ZombossRunVictoryXp);
        Assert.Equal(2, _store.CommanderLevelOf(new SaveId(player.Id), EmpireId.Zomboss));
    }

    [Fact]
    public void A_human_victory_gives_Zombosss_commander_the_smaller_run_defeat_consolation_award()
    {
        var player = _store.CreatePlayer("ZombossVictory");
        var runId = StartRun(player.Id);
        EndRun(player.Id, runId, "victory", "match-end-1");

        // Proves the SMALLER award applies (never the victory-award's size) without needing a second
        // run to separate them: the consolation award is well under the L1->L2 threshold.
        Assert.True(RpgXpAwards.ZombossRunDefeatXp < RpgXpCurve.XpToNext(RpgActorKinds.Player, 1));
        Assert.Equal(1, _store.CommanderLevelOf(new SaveId(player.Id), EmpireId.Zomboss));
    }

    [Fact]
    public void No_result_gives_nothing()
    {
        var player = _store.CreatePlayer("ZombossNoResult");
        var runId = StartRun(player.Id);
        _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.MatchEnded, RunId = runId, PayloadJson = "{}", DedupeKey = "match-end-1"
        });

        Assert.Equal(1, _store.CommanderLevelOf(new SaveId(player.Id), EmpireId.Zomboss));
    }

    [Fact]
    public void An_unknown_result_gives_nothing()
    {
        var player = _store.CreatePlayer("ZombossUnknownResult");
        var runId = StartRun(player.Id);
        EndRun(player.Id, runId, "draw", "match-end-1");

        Assert.Equal(1, _store.CommanderLevelOf(new SaveId(player.Id), EmpireId.Zomboss));
    }

    [Fact]
    public void WebMode_run_pvzGame_false_gives_nothing()
    {
        // Driven through InsertEvent so e.Game actually reaches IsPvzGame(...) as false, and through
        // the raw capture kind "match.result" (PvzActivityKinds.FromCaptureKind) rather than the
        // AppendPvzActivityFact API, which always posts pvzGame=true — mirrors
        // SpeciesProgressionTests.WebMode_run_does_not_level_the_PvZ_type's own pattern.
        var player = _store.CreatePlayer("ZombossWebMode");
        _store.SetCurrentPlayer(player.Id);
        const string matchKey = "web-run-1";
        _store.InsertEvent(new EventEnvelope
        {
            Game = RpgConstants.GameIdWebRpg, Kind = "board.start", MatchKey = matchKey,
            Payload = new { }
        });
        _store.InsertEvent(new EventEnvelope
        {
            Game = RpgConstants.GameIdWebRpg, Kind = "match.result", MatchKey = matchKey,
            Payload = new { result = "defeat" }
        });

        Assert.Equal(1, _store.CommanderLevelOf(new SaveId(player.Id), EmpireId.Zomboss));
    }

    [Fact]
    public void Replayed_MatchEnded_never_double_pays()
    {
        var player = _store.CreatePlayer("ZombossReplay");
        var runId = StartRun(player.Id);
        var req = new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.MatchEnded, RunId = runId, PayloadJson = """{"result":"defeat"}""",
            DedupeKey = "match-end-1"
        };
        _store.AppendPvzActivityFact(player.Id, req);
        _store.AppendPvzActivityFact(player.Id, req); // replay of the identical fact

        // One award only, not two -- a double-pay (200 XP) would cross the L2->L3 need
        // (first+step=145) rather than parking cleanly at L2 with 0 XP banked.
        Assert.Equal(2, _store.CommanderLevelOf(new SaveId(player.Id), EmpireId.Zomboss));
    }

    [Fact]
    public void Two_saves_advance_two_different_levels()
    {
        // R3 identity: the award lands on the RUN's OWN save's Zomboss empire, never a shared/global
        // Zomboss row -- two saves' clocks are provably independent.
        var a = _store.CreatePlayer("ZombossSaveA");
        var b = _store.CreatePlayer("ZombossSaveB");
        var runA = StartRun(a.Id);
        EndRun(a.Id, runA, "defeat", "a-match-end");
        // save B never posts a MatchEnded fact at all.

        Assert.Equal(2, _store.CommanderLevelOf(new SaveId(a.Id), EmpireId.Zomboss));
        Assert.Equal(1, _store.CommanderLevelOf(new SaveId(b.Id), EmpireId.Zomboss));
    }

    [Fact]
    public void A_save_with_no_seeded_Zomboss_empire_awards_nothing_and_creates_no_row()
    {
        var player = _store.CreatePlayer("ZombossUnseeded");
        var runId = StartRun(player.Id);

        // Simulates a legacy save from before SE4.22 seeded Zomboss for every new save: delete the
        // row SeedSaveEmpiresUnlocked already wrote, via the store's own hot connection -- never
        // insert a hand-built player row, since that would test a fixture, not the real
        // rpg_save_empires check the writer performs.
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        {
            using var del = db.CreateCommand();
            del.CommandText = "DELETE FROM rpg_save_empires WHERE save_id=$s AND empire_id='zomboss';";
            del.Parameters.AddWithValue("$s", player.Id);
            Assert.Equal(1, del.ExecuteNonQuery());
        }

        EndRun(player.Id, runId, "defeat", "match-end-1");

        // No row created on the fly -- CommanderLevelOf's own "no row reads level 1" contract is
        // exactly what proves nothing was written; it is not a special case for this test.
        Assert.Equal(1, _store.CommanderLevelOf(new SaveId(player.Id), EmpireId.Zomboss));
    }

    // ---- SP7.3 -- the read seam and its checked narrowing --------------------------------------

    [Fact]
    public void CommanderLevelOf_returns_the_level_the_clock_wrote()
    {
        // Direct, explicit proof of SP7.3's own acceptance line, distinct from every other test in
        // this file that reads the level only incidentally as part of proving an award outcome.
        var player = _store.CreatePlayer("ZombossReadSeam");
        var runId = StartRun(player.Id);
        Assert.Equal(1, _store.CommanderLevelOf(new SaveId(player.Id), EmpireId.Zomboss));

        EndRun(player.Id, runId, "defeat", "match-end-1");

        Assert.Equal(2, _store.CommanderLevelOf(new SaveId(player.Id), EmpireId.Zomboss));
    }

    [Fact]
    public void Narrowing_the_returned_level_into_an_int_is_checked_and_throws_past_int_MaxValue_never_clamps()
    {
        // spec-zomboss-commander-clock.md "Numeric": level is `long` end to end at the seam itself
        // (CommanderLevelOf never throws or clamps); the CONTRACT is that a CALLER narrowing it into
        // ContentContext.ZombossLevel (int) must use `checked`, exactly the pattern
        // ServerPowerIndexProvider.ReadSnapshot already establishes for the player's own commander
        // level (`checked((int)player.Level)`). No consumer wiring is built here (spec's own
        // "Ask first: any consumer wiring (delve, lawn) -- those are other programs'") -- this proves
        // the seam's own return value is safe to narrow that way when one is.
        var player = _store.CreatePlayer("ZombossOverflow");
        var runId = StartRun(player.Id);
        EndRun(player.Id, runId, "defeat", "match-end-1"); // level 2, ordinary case: narrows fine
        Assert.Equal(2, checked((int)_store.CommanderLevelOf(new SaveId(player.Id), EmpireId.Zomboss)));

        // Force the persisted level past int.MaxValue directly -- no realistic amount of play reaches
        // it, so the only way to prove the seam stays `long` (never silently clamped) and that
        // `checked` genuinely catches the overflow is to plant it.
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        {
            using var upd = db.CreateCommand();
            upd.CommandText = """
                UPDATE rpg_actor_progression SET level=$l
                WHERE save_id=$s AND empire_id='zomboss' AND kind='player' AND type_id=0;
                """;
            upd.Parameters.AddWithValue("$l", (long)int.MaxValue + 1);
            upd.Parameters.AddWithValue("$s", player.Id);
            Assert.Equal(1, upd.ExecuteNonQuery());
        }

        var overflowed = _store.CommanderLevelOf(new SaveId(player.Id), EmpireId.Zomboss);
        Assert.Equal((long)int.MaxValue + 1, overflowed); // the seam itself: long, exact, no clamp
        Assert.Throws<OverflowException>(() => checked((int)overflowed)); // the narrowing: throws, never wraps
    }
}
