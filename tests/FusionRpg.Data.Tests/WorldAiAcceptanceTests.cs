using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// **Checkpoint 10 — wave-2b acceptance** (world-map-plan.md W38). Twenty turns played against
/// something that cannot see you.
///
/// Unlike the wave-1 scenario this one is *not* scripted for the AI: Zomboss and the wild decide for
/// themselves, every turn, from their own fog. What is asserted is therefore not a plot but a set of
/// properties — that the run is reproducible, that it replays byte-identically from the command log
/// alone, and the one this module exists for: **no order ever names ground its commander has not
/// seen.**
/// </summary>
public class WorldAiAcceptanceTests : IDisposable
{
    const int Turns = 20;
    const ulong Seed = 90210;

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WorldAiAcceptanceTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    /// <summary>
    /// The shipped map, unaltered.
    ///
    /// This used to add a Zomboss warband by hand, because the template shipped without one — he had
    /// a faction, a fortress and no army, so a brain gave him nothing to do. Found by playing twenty
    /// turns rather than by any test: every suite agreed the AI worked, and it did; there was simply
    /// nobody to be. The band is in the template now and this is a plain build again.
    /// </summary>
    static WorldState World(string worldId) =>
        WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, Seed, worldId);

    /// <summary>Twenty turns where only the human commits and everyone else thinks for themselves.</summary>
    IReadOnlyList<string> Play(string worldId)
    {
        _store.CreateWorld(1, World(worldId));

        var hashes = new List<string>();
        for (var turn = 0; turn < Turns; turn++)
        {
            var open = _store.GetWorldHeader(worldId)!.CurrentTurn;
            var result = _store.CommitWorldTurn(worldId, "dave", open);

            Assert.True(result.Advanced, $"turn {turn} did not advance: {result.Reason}");
            hashes.Add(result.StateHash!);
        }

        return hashes;
    }

    // ---- reproducibility ---------------------------------------------------------------------

    [Fact]
    public void Twenty_turns_of_live_thinking_advance_once_each()
    {
        var hashes = Play("ai-accept");

        Assert.Equal(Turns, hashes.Count);
        Assert.Equal(Turns, hashes.Distinct(StringComparer.Ordinal).Count());   // the world moves every turn

        for (var turn = 0; turn < Turns; turn++)
            Assert.NotNull(_store.GetWorldTurnLog("ai-accept", turn));

        Assert.Null(_store.GetWorldTurnLog("ai-accept", Turns));
    }

    [Fact]
    public void The_same_seed_thinks_the_same_thoughts_twice()
    {
        // A policy is pure in (belief, seed). If this ever drifts, the AI has picked up a clock, a
        // hash-ordered iteration, or an unowned die.
        Assert.Equal(Play("ai-twin-a"), Play("ai-twin-b"));
    }

    [Fact]
    public void The_pure_engine_reproduces_the_stored_hashes_from_the_command_log_alone()
    {
        // ⭐ The claim the whole module rests on. The log contains what the AI decided; replay never
        // re-runs a policy. Zomboss's brain can be rewritten in a later wave and this still holds,
        // which is what makes AI work non-breaking rather than save-breaking.
        var live = Play("ai-replay");

        var world = World("ai-replay");
        var replayed = new List<string>();

        for (var turn = 0; turn < Turns; turn++)
        {
            var result = TurnEngine.Step(world, _store.ListWorldCommands("ai-replay", turn), Seed);
            world = result.World;
            replayed.Add(result.StateHash);
        }

        Assert.Equal(live, replayed);
    }

    // ---- the property this module exists for ---------------------------------------------------

    [Fact]
    public void No_order_ever_names_ground_its_commander_had_not_seen()
    {
        // The fog-honesty sweep, over every order of every turn. Belief is read from the world *as
        // it was when the order was filed* — the turn's start — because that is what the policy had.
        Play("ai-fog");

        var world = World("ai-fog");
        var leaks = new List<string>();

        for (var turn = 0; turn < Turns; turn++)
        {
            var commands = _store.ListWorldCommands("ai-fog", turn);

            foreach (var command in commands)
            {
                if (command.SectorId is not { } named) continue;

                var view = new BelievedWorldView(world, command.CommanderId);
                if (view.Believed(named) is null)
                    leaks.Add($"turn {turn}: {command.CommanderId} named {named}, which it had never seen");
            }

            world = TurnEngine.Step(world, commands, Seed).World;
        }

        Assert.True(leaks.Count == 0, string.Join("\n", leaks));
    }

    [Fact]
    public void Every_order_an_ai_filed_says_why()
    {
        Play("ai-reasons");

        var explained = 0;
        for (var turn = 0; turn < Turns; turn++)
            foreach (var logged in _store.ListLoggedWorldCommands("ai-reasons", turn))
            {
                if (logged.Command.CommanderId == "dave") continue;

                Assert.False(string.IsNullOrWhiteSpace(logged.Reason),
                    $"turn {turn}: {logged.Command.CommanderId} filed {logged.Command.Kind} with no reason");
                explained++;
            }

        Assert.True(explained >= Turns, $"only {explained} explained orders across {Turns} turns");
    }

    // ---- and it is actually playing ---------------------------------------------------------------

    [Fact]
    public void Zomboss_does_something_other_than_stand_still()
    {
        // The difference between "the seam works" and "there is somebody on the other side". If this
        // ever goes quiet, the rules stopped firing and every test above would still pass.
        Play("ai-active");

        var kinds = new HashSet<string>(StringComparer.Ordinal);
        for (var turn = 0; turn < Turns; turn++)
            foreach (var command in _store.ListWorldCommands("ai-active", turn))
                if (command.CommanderId == "zomboss")
                    kinds.Add(command.Kind);

        Assert.Contains(kinds, k => k != WorldCommandKinds.StandFast);
    }

    [Fact]
    public void The_wild_stay_where_they_are_because_a_hazard_is_not_an_empire()
    {
        // They keep `stand-fast` on purpose: an expansionist wild would race the player for every
        // sector and turn a map with danger on it into a map with two opponents on it.
        Play("ai-wild");

        for (var turn = 0; turn < Turns; turn++)
            foreach (var command in _store.ListWorldCommands("ai-wild", turn))
                if (command.CommanderId == "wild")
                    Assert.Equal(WorldCommandKinds.StandFast, command.Kind);
    }
}
