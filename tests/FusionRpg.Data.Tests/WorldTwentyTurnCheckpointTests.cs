using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// **Checkpoint 2 — the SSOT gate** (world-map-plan.md). Twenty scripted turns must produce a
/// stable state hash, replay byte-identically from `(seed, template, command log)`, and leave
/// exactly one turn-log row per turn.
///
/// The sharpest assertion here is store-versus-engine: replaying the command log through the pure
/// engine has to reproduce the hashes the store wrote. If persistence ever perturbs state — a
/// dropped field, a re-ordered read, a lossy round trip — the two diverge and this fails.
/// </summary>
public class WorldTwentyTurnCheckpointTests : IDisposable
{
    const int Turns = 20;
    static readonly string[] Commanders = { "dave", "wild", "zomboss" };

    /// <summary>The one commander with no policy — the only one the auto-fill leaves alone.</summary>
    const string Human = "dave";

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WorldTwentyTurnCheckpointTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    /// <summary>The scripted log: every commander stands fast, every turn.</summary>
    static IEnumerable<WorldCommand> ScriptFor(int turn) => Commanders.Select(c => new WorldCommand
    {
        CommanderId = c,
        CommandId = $"t{turn}-{c}",
        Kind = WorldCommandKinds.StandFast
    });

    List<string> PlayTwentyTurns(string worldId, ulong seed)
    {
        _store.CreateWorld(1, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed, worldId));

        var hashes = new List<string>();
        for (var turn = 0; turn < Turns; turn++)
        {
            _store.SubmitWorldCommands(worldId, ScriptFor(turn).ToList());

            // Every commander ends the *same* turn. The scripted factions commit **first**: an
            // explicit commit speaks for a faction and keeps `ai-commander`'s auto-fill out of it,
            // which is what lets a scenario script what the wild and Zomboss do. The human commits
            // last, and that is the one that releases the barrier.
            var open = _store.GetWorldHeader(worldId)!.CurrentTurn;
            WorldTurnCommitResult last = default!;
            foreach (var commander in Commanders.Where(c => c != Human).Concat(new[] { Human }))
                last = _store.CommitWorldTurn(worldId, commander, open);

            Assert.True(last.Advanced, $"turn {turn} did not advance");
            hashes.Add(last.StateHash!);
        }

        return hashes;
    }

    [Fact]
    public void Twenty_turns_advance_once_each_and_leave_one_log_row_per_turn()
    {
        PlayTwentyTurns("cp2", seed: 2026);

        Assert.Equal(Turns, _store.GetActiveWorld(1)!.CurrentTurn);
        for (var turn = 0; turn < Turns; turn++)
            Assert.NotNull(_store.GetWorldTurnLog("cp2", turn));
        Assert.Null(_store.GetWorldTurnLog("cp2", Turns));
    }

    [Fact]
    public void The_same_script_and_seed_produce_the_same_twenty_hashes()
    {
        var first = PlayTwentyTurns("cp2-a", seed: 2026);
        var second = PlayTwentyTurns("cp2-b", seed: 2026);

        Assert.Equal(first, second);
        Assert.Equal(Turns, first.Distinct().Count()); // every turn moved the world
    }

    [Fact]
    public void Replaying_the_command_log_through_the_pure_engine_reproduces_the_stored_hashes()
    {
        var stored = PlayTwentyTurns("cp2-replay", seed: 2026);

        // Nothing but (seed, template, command log) — no store state in this loop. The resolver must
        // match what the store actually commits with (RpgStore.WorldTurns.cs) or "pure engine" would
        // silently mean "pure engine, minus whichever battle kind the store's resolver wasn't the
        // default" — a gap base-defense `siege-engagement` (2026-09-06) found the hard way: this
        // scenario never happens to route a battle through `DistrictAssaultResolver`'s real-board path,
        // so the two resolvers have always agreed here by coincidence, not by construction.
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, 2026, "cp2-replay");
        var replayed = new List<string>();
        for (var turn = 0; turn < Turns; turn++)
        {
            var result = TurnEngine.Step(world, _store.ListWorldCommands("cp2-replay", turn), 2026, DistrictAssaultResolver.Instance);
            world = result.World;
            replayed.Add(result.StateHash);
        }

        Assert.Equal(stored, replayed);
    }

    [Fact]
    public void A_different_seed_produces_a_different_history()
    {
        // The seed is part of the world's state, so it is part of the hash — two campaigns that
        // played identically but rolled from different seeds are not the same world.
        var a = PlayTwentyTurns("cp2-seed-a", seed: 1);
        var b = PlayTwentyTurns("cp2-seed-b", seed: 2);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Reports_outside_the_hot_tail_re_derive_to_what_was_stored()
    {
        PlayTwentyTurns("cp2-tail", seed: 2026);

        var beforeTrim = _store.GetWorldTurnReport("cp2-tail", 0)!.Entries.ToList();
        _store.TrimWorldTurnReports("cp2-tail", keepLast: 5);

        Assert.Null(_store.GetWorldTurnLog("cp2-tail", 0)!.ReportJson);   // body gone
        Assert.NotNull(_store.GetWorldTurnLog("cp2-tail", 0)!.StateHash); // drift detector stays

        Assert.Equal(beforeTrim, _store.GetWorldTurnReport("cp2-tail", 0)!.Entries.ToList());
    }

    [Fact]
    public void The_calendar_marks_the_weeks_inside_twenty_turns()
    {
        PlayTwentyTurns("cp2-calendar", seed: 2026);

        // Turns 7 and 14 are week boundaries; the rest are ordinary days.
        Assert.Contains(_store.GetWorldTurnReport("cp2-calendar", 6)!.Entries,
            e => e.Kind == TurnReportKinds.Calendar && e.Subject == "week");
        Assert.DoesNotContain(_store.GetWorldTurnReport("cp2-calendar", 5)!.Entries,
            e => e.Kind == TurnReportKinds.Calendar);
    }
}
