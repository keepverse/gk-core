using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// W7 (spec-turn-engine.md §Persistence): one turn is one transaction. The barrier waits for every
/// commander, the log keeps hash + versions always and the report only for a hot tail, and an older
/// report is re-derived from the command log rather than stored forever.
/// </summary>
public class WorldTurnCommitTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WorldTurnCommitTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _store.CreateWorld(1, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, 1, "w"));
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static readonly string[] AllCommanders = { "dave", "wild", "zomboss" };

    /// <summary>The turn currently open, which is the turn a commit has to name.</summary>
    int Open => _store.GetWorldHeader("w")!.CurrentTurn;

    WorldTurnCommitResult Commit(string commanderId) => _store.CommitWorldTurn("w", commanderId, Open);

    /// <summary>Commits every commander and returns the last result — the turn fires on the last one.</summary>
    WorldTurnCommitResult CommitAll(params string[] commanders)
    {
        // `params` with no arguments is an empty array, not null — spell the default out.
        var who = commanders.Length == 0 ? AllCommanders : commanders;
        var open = Open;   // they all end the same turn, so read it once

        // The human goes last: a faction with a policy commits itself the moment anyone ends the
        // turn, so committing Dave first would resolve it before the others got a word in.
        WorldTurnCommitResult last = default!;
        foreach (var c in who.Where(c => c != "dave").Concat(who.Where(c => c == "dave")))
            last = _store.CommitWorldTurn("w", c, open);
        return last;
    }

    [Fact]
    public void The_turn_fires_only_when_the_last_commander_commits()
    {
        // The barrier is unchanged — it still wants every commander. What changed in W27 is who
        // supplies them: a faction with a policy commits itself, so the *human* is the last one
        // outstanding and their commit is what releases the turn.
        var wild = Commit("wild");
        Assert.True(wild.Ok);
        Assert.False(wild.Advanced);
        Assert.Equal(0, _store.GetActiveWorld(1)!.CurrentTurn);

        var human = Commit("dave");
        Assert.True(human.Advanced);
        Assert.Equal(1, _store.GetActiveWorld(1)!.CurrentTurn);
    }

    [Fact]
    public void Committing_twice_is_idempotent_and_does_not_advance_twice()
    {
        // Committing the same *open* turn again is absorbed: the commit row is already there and
        // the barrier has nothing new to weigh.
        var wild = Commit("wild");
        Assert.True(wild.Ok);
        var repeat = Commit("wild");
        Assert.True(repeat.Ok);
        Assert.False(repeat.Advanced);
        Assert.Equal(0, _store.GetActiveWorld(1)!.CurrentTurn);

        Assert.True(Commit("dave").Advanced);
        Assert.Equal(1, _store.GetActiveWorld(1)!.CurrentTurn);

        // A duplicate for the turn that already resolved is *refused* rather than absorbed. That
        // distinction is the whole of W25: absorbed, a retry would end the next turn instead.
        var stale = _store.CommitWorldTurn("w", "dave", 0);
        Assert.False(stale.Ok);
        Assert.Equal("turn.stale", stale.Reason);
        Assert.Equal(1, _store.GetActiveWorld(1)!.CurrentTurn);
    }

    [Fact]
    public void Each_turn_writes_exactly_one_log_row_carrying_its_hash_and_versions()
    {
        CommitAll();
        CommitAll();

        var t0 = _store.GetWorldTurnLog("w", 0)!;
        var t1 = _store.GetWorldTurnLog("w", 1)!;

        Assert.False(string.IsNullOrWhiteSpace(t0.StateHash));
        Assert.NotEqual(t0.StateHash, t1.StateHash); // the turn number alone moves the world
        Assert.Equal(TurnEngine.EngineVersion, t0.EngineVersion);
        Assert.Equal(TurnEngine.RulesetVersion, t0.RulesetVersion);
        Assert.Null(_store.GetWorldTurnLog("w", 2));
    }

    [Fact]
    public void The_state_hash_in_the_log_matches_the_world_it_describes()
    {
        CommitAll();

        var log = _store.GetWorldTurnLog("w", 0)!;
        var world = _store.LoadWorldState("w")!;
        Assert.Equal(StateHasher.Hash(world), log.StateHash);
    }

    [Fact]
    public void Orders_belong_to_the_turn_they_were_filed_in()
    {
        _store.SubmitWorldCommand("w", new WorldCommand
        {
            CommanderId = "dave", CommandId = "c1", Kind = WorldCommandKinds.StandFast
        });
        CommitAll();

        // Dave's own order, filed by hand. The AI factions file their own alongside it, which is
        // why this counts the player's rather than the turn's.
        Assert.Single(_store.ListWorldCommands("w", 0).Where(c => c.CommanderId == "dave"));
        Assert.Empty(_store.ListWorldCommands("w", 1)); // the new turn starts clean
    }

    [Fact]
    public void A_stored_report_comes_back_as_it_was_written()
    {
        _store.SubmitWorldCommand("w", new WorldCommand
        {
            CommanderId = "dave", CommandId = "c1", Kind = WorldCommandKinds.StandFast
        });
        CommitAll();

        var report = _store.GetWorldTurnReport("w", 0);
        Assert.NotNull(report);
        Assert.Contains(report!.Accepted, e => e.Subject == "c1");
    }

    [Fact]
    public void A_trimmed_report_is_re_derived_identically_from_the_command_log()
    {
        _store.SubmitWorldCommand("w", new WorldCommand
        {
            CommanderId = "dave", CommandId = "c1", Kind = WorldCommandKinds.StandFast
        });
        CommitAll();

        var stored = _store.GetWorldTurnReport("w", 0)!;
        var storedEntries = stored.Entries.ToList();

        _store.TrimWorldTurnReports("w", keepLast: 0); // the report body is gone; hash and versions stay
        Assert.NotNull(_store.GetWorldTurnLog("w", 0));

        var rederived = _store.GetWorldTurnReport("w", 0);
        Assert.NotNull(rederived);
        Assert.Equal(storedEntries, rederived!.Entries.ToList());
    }

    /// <summary>
    /// world-stage — found by actually watching a turn play back through `PlaybackRail`, not by a
    /// test: only `Entries` was ever persisted, never `Phases` (`TurnReport.Phases`, the locked order
    /// `BeginPhase` records live), so a phase that ran with zero entries (`Growth`'s own named
    /// no-op, most turns) silently vanished the moment a stored report was reloaded — exactly the
    /// silent gap `PlaybackRail`'s own GG-17 discipline exists to prevent, just one layer lower than
    /// where that discipline could see it. This is the direct regression: reload a freshly-committed,
    /// still-hot (never trimmed) report and prove every phase the live engine ran survives, in order
    /// — not only the ones an entry happened to land in.
    /// </summary>
    [Fact]
    public void A_reloaded_report_keeps_every_phase_the_engine_ran_even_one_with_no_entries()
    {
        var header = _store.GetWorldHeader("w")!;
        var live = TurnEngine.Step(
            WorldTemplateCatalog.Build(header.TemplateId, header.Seed, "w"),
            new[] { new WorldCommand { CommanderId = "dave", CommandId = "c1", Kind = WorldCommandKinds.StandFast } },
            header.Seed);
        // Sanity: this fixture actually exercises the bug — at least one phase the live engine ran
        // carries no entry of its own, so a reconstruction from entries alone would have to drop it.
        Assert.Contains(live.Report.Phases, p => live.Report.Entries.All(e => e.Phase != p));

        _store.SubmitWorldCommand("w", new WorldCommand
        {
            CommanderId = "dave", CommandId = "c1", Kind = WorldCommandKinds.StandFast
        });
        CommitAll();

        var reloaded = _store.GetWorldTurnReport("w", 0);
        Assert.NotNull(reloaded);
        Assert.Equal(live.Report.Phases, reloaded!.Phases);
    }

    /// <summary>
    /// world-map W58's own acceptance: "a test asserts the stored-versus-engine replay refuses across
    /// the [RulesetVersion] bump rather than fabricating a report." `GetWorldTurnReport`'s own doc
    /// comment already states the contract ("Re-derivation refuses across a version change rather
    /// than fabricating a report the current engine would not have produced") — this is the first
    /// test that actually forces the mismatch and proves it, rather than trusting the comment.
    /// Stamps the log row's own `ruleset_version` column one below whatever the live engine reports,
    /// which stays meaningful across any future bump too, not only W58's real 6 → 7 one.
    /// </summary>
    [Fact]
    public void A_stored_log_at_an_older_ruleset_version_refuses_to_re_derive_rather_than_fabricating()
    {
        _store.SubmitWorldCommand("w", new WorldCommand
        {
            CommanderId = "dave", CommandId = "c1", Kind = WorldCommandKinds.StandFast
        });
        CommitAll();

        // Force re-derivation to be the only possible path — the same setup
        // `A_trimmed_report_is_re_derived_identically_from_the_command_log` already uses.
        _store.TrimWorldTurnReports("w", keepLast: 0);
        Assert.NotNull(_store.GetWorldTurnReport("w", 0)); // sanity: re-derivation genuinely works today

        using (var hot = SqliteConnectionFactory.Open(_store.HotPath))
        {
            using var cmd = hot.CreateCommand();
            cmd.CommandText = "UPDATE rpg_world_turn_log SET ruleset_version = $rv WHERE world_id = $w AND turn = 0;";
            cmd.Parameters.AddWithValue("$rv", TurnEngine.RulesetVersion - 1);
            cmd.Parameters.AddWithValue("$w", "w");
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }

        // The row is still there — this is a version refusal, not a missing row.
        Assert.NotNull(_store.GetWorldTurnLog("w", 0));
        Assert.Null(_store.GetWorldTurnReport("w", 0));
    }

    // ---- W25: a commit names the turn it means to end ---------------------------------

    [Fact]
    public void A_commit_that_names_the_wrong_turn_is_refused_and_changes_nothing()
    {
        var refused = _store.CommitWorldTurn("w", "dave", expectedTurn: 7);

        Assert.False(refused.Ok);
        Assert.Equal("turn.stale", refused.Reason);
        Assert.False(refused.Advanced);
        Assert.Equal(0, _store.GetActiveWorld(1)!.CurrentTurn);

        // And nothing was recorded: the barrier still has nobody in it, so naming the right turn
        // afterwards behaves exactly as a first commit would — which, with the AI filling for the
        // other two factions, means it resolves the turn.
        var proper = Commit("dave");
        Assert.True(proper.Ok);
        Assert.True(proper.Advanced);
    }

    [Fact]
    public void A_retried_commit_cannot_resolve_a_second_turn()
    {
        // The regression this task exists for. Today the barrier needs all three commanders, so a
        // retry is harmless; once `ai-commander` auto-commits the other two, *any* commit can be
        // the one that releases it — and a client that never saw its response would resend, read
        // the new current turn, and burn a turn the player never played.
        CommitAll();
        Assert.Equal(1, _store.GetActiveWorld(1)!.CurrentTurn);

        var retry = _store.CommitWorldTurn("w", "dave", expectedTurn: 0);

        Assert.False(retry.Ok);
        Assert.Equal("turn.stale", retry.Reason);
        Assert.Equal(1, _store.GetActiveWorld(1)!.CurrentTurn);
        Assert.Null(_store.GetWorldTurnLog("w", 1));
    }

    [Fact]
    public void A_commit_naming_a_turn_that_has_not_happened_yet_is_refused_too()
    {
        // Stale in both directions: the check is "the turn I am looking at", not "not the past".
        var ahead = _store.CommitWorldTurn("w", "dave", expectedTurn: 1);

        Assert.False(ahead.Ok);
        Assert.Equal("turn.stale", ahead.Reason);
        Assert.Equal(0, _store.GetActiveWorld(1)!.CurrentTurn);
    }

    [Fact]
    public void An_unknown_world_or_commander_is_refused_before_the_turn_is_checked()
    {
        // Order matters for the message: "who are you" is more useful than "wrong turn" when both
        // are true, and a stranger should not learn which turn is open.
        Assert.Equal("world.unknown", _store.CommitWorldTurn("nope", "dave", 999).Reason);
        Assert.Equal("commander.unknown", _store.CommitWorldTurn("w", "stranger", 999).Reason);
    }

    [Fact]
    public void An_unknown_world_or_commander_is_refused()
    {
        Assert.False(_store.CommitWorldTurn("nope", "dave", 0).Ok);
        Assert.False(_store.CommitWorldTurn("w", "stranger", 0).Ok);
        Assert.Equal(0, _store.GetActiveWorld(1)!.CurrentTurn);
    }
}
