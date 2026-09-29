using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// W27 (spec-ai-commander.md §The commander loop): the AI commits for itself, inside the same
/// transaction that resolves the turn.
///
/// The fill lives here rather than at the endpoint because the barrier lives here. Filling at the
/// endpoint would leave the player's commit reporting `waiting` for a turn that did in fact resolve,
/// and would leave every non-HTTP caller — which is most of this suite — unable to advance at all.
/// </summary>
public class WorldAiCommitTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WorldAiCommitTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _store.CreateWorld(1, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, 1, "w"));
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    int Open => _store.GetWorldHeader("w")!.CurrentTurn;

    WorldTurnCommitResult EndTurn(Func<string, IFactionPolicy>? policies = null) =>
        _store.CommitWorldTurn("w", "dave", Open, policies: policies);

    // ---- the barrier releases ------------------------------------------------------------

    [Fact]
    public void The_player_ending_their_turn_is_now_enough_to_resolve_it()
    {
        // Before this task the commit path could never advance a world from outside the tests: the
        // barrier wants all three factions and nothing ever committed for the wild or Zomboss.
        var result = EndTurn();

        Assert.True(result.Advanced);
        Assert.Equal(1, Open);
        Assert.NotNull(result.StateHash);
    }

    [Fact]
    public void Every_faction_with_a_policy_files_and_commits()
    {
        EndTurn();

        var commands = _store.ListWorldCommands("w", 0);
        Assert.Contains(commands, c => c.CommanderId == "wild");
        Assert.Contains(commands, c => c.CommanderId == "zomboss");
    }

    [Fact]
    public void Committing_for_an_ai_faction_by_hand_speaks_for_it_and_the_fill_stays_out()
    {
        // The escape hatch, and it has to work: a scripted scenario drives an AI faction by filing
        // its orders and committing for it. If the fill overrode that, no test could ever script
        // what Zomboss does — it would keep filing its own opinion on top.
        _store.CommitWorldTurn("w", "zomboss", 0);
        EndTurn();

        Assert.DoesNotContain(_store.ListWorldCommands("w", 0), c => c.CommanderId == "zomboss");
        Assert.Contains(_store.ListWorldCommands("w", 0), c => c.CommanderId == "wild");
    }

    [Fact]
    public void The_fill_never_files_the_same_order_twice()
    {
        // Two turns in a row, so the second fill runs against a world the first one already moved.
        // Command ids carry the turn, so a repeat would be a primary-key collision rather than a
        // duplicate — which is a crash, not a quiet bug, but only if the ids are right.
        EndTurn();
        EndTurn();

        foreach (var turn in new[] { 0, 1 })
        {
            var ids = _store.ListWorldCommands("w", turn)
                .Select(c => $"{c.CommanderId}/{c.CommandId}")
                .ToList();
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void The_human_is_never_filled_for()
    {
        // Dave has no PolicyId. Filing on the player's behalf would be the module playing the game.
        EndTurn();
        Assert.DoesNotContain(_store.ListWorldCommands("w", 0), c => c.CommanderId == "dave");
    }

    [Fact]
    public void The_fill_does_not_disturb_a_turn_it_did_not_release()
    {
        // A world where the player is not the last to commit: the fill still runs, but the barrier
        // is what decides, and it must decide on the committers rather than on who called.
        var result = _store.CommitWorldTurn("w", "wild", 0);

        // wild committed explicitly; the fill covers zomboss; dave — the human — has not.
        Assert.False(result.Advanced);
        Assert.Equal("waiting", result.Reason);
        Assert.Equal(0, Open);
    }

    [Fact]
    public void Orders_already_filed_speak_for_a_faction_as_loudly_as_a_commit()
    {
        // The escape hatch's real shape, and it was broken. Committing *any* faction runs the fill
        // for every other AI faction, so a scenario that scripts two of them had its second one
        // filled over by whichever commit landed first — silently, with orders nobody wrote.
        _store.SubmitWorldCommand("w", new WorldCommand
        {
            CommanderId = "zomboss", CommandId = "scripted", Kind = WorldCommandKinds.StandFast
        });

        _store.CommitWorldTurn("w", "wild", 0);        // somebody else ends their turn first

        var zomboss = _store.ListWorldCommands("w", 0).Where(c => c.CommanderId == "zomboss").ToList();
        Assert.Equal(new[] { "scripted" }, zomboss.Select(c => c.CommandId));
    }

    // ---- the claim the whole design rests on ----------------------------------------------

    [Fact]
    public void Replaying_a_stored_log_ignores_the_policy_that_produced_it()
    {
        // The most important test in the module. A save is (seed, template, command log). If the AI
        // ran inside Step, replay would have to re-run it and *every* future improvement to Zomboss
        // would invalidate *every* existing save. Because a policy only files commands, the log is
        // the input and the brain that wrote it is irrelevant.
        const int turns = 3;
        var live = new List<string>();
        var log = new List<List<WorldCommand>>();

        for (var t = 0; t < turns; t++)
        {
            live.Add(EndTurn().StateHash!);
            log.Add(_store.ListWorldCommands("w", t).ToList());
        }

        Assert.NotEmpty(log[0].Where(c => c.CommanderId == "zomboss"));

        // Replay through the engine directly: no store, no fill, and no policy of any kind.
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, 1, "w");
        var replayed = new List<string>();
        for (var t = 0; t < turns; t++)
        {
            var result = TurnEngine.Step(world, log[t], 1);
            world = result.World;
            replayed.Add(result.StateHash);
        }

        Assert.Equal(live, replayed);
    }

    [Fact]
    public void A_different_policy_changes_what_is_filed_and_not_how_a_log_replays()
    {
        // The same claim from the other side: swap the brain, and the *log* differs — but replaying
        // either log reproduces its own hashes exactly. That is what makes AI work non-breaking.
        var chatty = EndTurn(_ => new FixedReasonPolicy("thinking about it"));
        var log = _store.ListWorldCommands("w", 0).ToList();

        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, 1, "w");
        Assert.Equal(chatty.StateHash, TurnEngine.Step(world, log, 1).StateHash);
    }

    // ---- reasons -------------------------------------------------------------------------

    [Fact]
    public void Every_ai_order_carries_a_reason_and_no_player_order_does()
    {
        _store.SubmitWorldCommand("w", new WorldCommand
        {
            CommanderId = "dave", CommandId = "mine", Kind = WorldCommandKinds.StandFast
        });
        EndTurn();

        var logged = _store.ListLoggedWorldCommands("w", 0);

        Assert.All(logged.Where(l => l.Command.CommanderId != "dave"),
            l => Assert.False(string.IsNullOrWhiteSpace(l.Reason)));
        Assert.Null(logged.Single(l => l.Command.CommandId == "mine").Reason);
    }

    [Fact]
    public void A_reason_longer_than_the_column_allows_is_cut_rather_than_refused()
    {
        // Bounded at the boundary like every other free-text field. An audit string is not worth
        // failing a turn over, and an unbounded one is a row nobody budgeted for.
        EndTurn(_ => new FixedReasonPolicy(new string('x', 500)));

        var reason = _store.ListLoggedWorldCommands("w", 0)
            .First(l => l.Command.CommanderId == "zomboss").Reason;

        Assert.Equal(RpgStore.MaxCommandReasonLength, reason!.Length);
    }

    // ---- failure is loud -------------------------------------------------------------------

    [Fact]
    public void A_policy_that_throws_leaves_the_world_exactly_where_it_was()
    {
        // Deliberately not caught. One transaction: it rolls back, nothing is filed, nothing is
        // committed, and the next commit throws again — visibly. A swallowed exception here would
        // turn an arithmetic bug into a faction that quietly stopped playing, which is the hardest
        // class of defect this codebase could ship.
        Assert.Throws<InvalidOperationException>(() => EndTurn(_ => new ThrowingPolicy()));

        Assert.Equal(0, Open);
        Assert.Empty(_store.ListWorldCommands("w", 0));
        Assert.Null(_store.GetWorldTurnLog("w", 0));
    }

    // ---- a policy is held to the same rules as a person -------------------------------

    [Fact]
    public void A_policy_cannot_file_on_another_factions_behalf()
    {
        // Admission checks that a commander exists and owns the entity it names — so an order the
        // wild files *as Zomboss*, naming a Zomboss entity, is perfectly legal and would be filed.
        // Zomboss would then act on orders it never chose while still waiting at the barrier.
        var error = Assert.Throws<InvalidOperationException>(() =>
            EndTurn(_ => new ImpersonatingPolicy()));

        Assert.Contains("while acting as", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, Open);
    }

    [Fact]
    public void A_policy_that_gives_one_legion_two_orders_is_a_bug_and_says_so()
    {
        var error = Assert.Throws<InvalidOperationException>(() => EndTurn(_ => new GreedyPolicy()));

        Assert.Contains("two orders", error.Message, StringComparison.Ordinal);
        Assert.Empty(_store.ListWorldCommands("w", 0));
    }

    [Fact]
    public void An_order_a_policy_files_must_pass_the_same_admission_a_person_would()
    {
        // A silent skip here would be the worst outcome: the faction commits every turn having
        // filed nothing, which is indistinguishable from standing fast on purpose.
        var error = Assert.Throws<InvalidOperationException>(() => EndTurn(_ => new IllegalPolicy()));

        Assert.Contains("inadmissible", error.Message, StringComparison.Ordinal);
        Assert.Contains("entity.unknown", error.Message, StringComparison.Ordinal);
    }

    // ---- stand-ins -------------------------------------------------------------------------

    sealed class FixedReasonPolicy : IFactionPolicy
    {
        readonly string _reason;
        public FixedReasonPolicy(string reason) => _reason = reason;

        public string PolicyId => "fixed-reason";

        public IReadOnlyList<PolicyOrder> Decide(IWorldView view, ulong seed) => new[]
        {
            new PolicyOrder(
                new WorldCommand
                {
                    CommanderId = view.FactionId,
                    CommandId = $"ai-{view.CurrentTurn}-stand",
                    Kind = WorldCommandKinds.StandFast
                },
                _reason)
        };
    }

    sealed class ThrowingPolicy : IFactionPolicy
    {
        public string PolicyId => "throws";

        public IReadOnlyList<PolicyOrder> Decide(IWorldView view, ulong seed) =>
            throw new InvalidOperationException("the brain is on fire");
    }

    /// <summary>Files a legal order — for somebody else.</summary>
    sealed class ImpersonatingPolicy : IFactionPolicy
    {
        public string PolicyId => "impersonates";

        public IReadOnlyList<PolicyOrder> Decide(IWorldView view, ulong seed) => new[]
        {
            new PolicyOrder(
                new WorldCommand
                {
                    CommanderId = "zomboss",
                    CommandId = $"ai-{view.CurrentTurn}-poach",
                    Kind = WorldCommandKinds.StandFast
                },
                "not my turn to give")
        };
    }

    /// <summary>Two orders for one legion, which the engine would silently resolve in id order.</summary>
    sealed class GreedyPolicy : IFactionPolicy
    {
        public string PolicyId => "greedy";

        public IReadOnlyList<PolicyOrder> Decide(IWorldView view, ulong seed) =>
            view.OwnForces.Count == 0
                ? Array.Empty<PolicyOrder>()
                : new[] { Order(view, "a"), Order(view, "b") };

        static PolicyOrder Order(IWorldView view, string suffix) => new(
            new WorldCommand
            {
                CommanderId = view.FactionId,
                CommandId = $"ai-{view.CurrentTurn}-{suffix}",
                Kind = WorldCommandKinds.Stance,
                EntityId = view.OwnForces[0].EntityId,
                Stance = "march"
            },
            "twice");
    }

    /// <summary>Names a legion that does not exist.</summary>
    sealed class IllegalPolicy : IFactionPolicy
    {
        public string PolicyId => "illegal";

        public IReadOnlyList<PolicyOrder> Decide(IWorldView view, ulong seed) => new[]
        {
            new PolicyOrder(
                new WorldCommand
                {
                    CommanderId = view.FactionId,
                    CommandId = $"ai-{view.CurrentTurn}-ghost",
                    Kind = WorldCommandKinds.Stance,
                    EntityId = "e-does-not-exist",
                    Stance = "march"
                },
                "ordering a ghost")
        };
    }
}
