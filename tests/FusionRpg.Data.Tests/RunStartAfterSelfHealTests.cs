using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// lawn-combat-wire L-N29 (live 2026-09-15): the injector enqueues events that its own <c>board.start</c> handling emits
/// (<c>cheat.apply</c>, <c>debug.effect.cleared</c>) before <c>board.start</c> itself. The first of them self-heals the run
/// ([[match-key-orphan-drops-soul-earn]]), so <c>board.start</c> used to INSERT a second row for the same
/// <c>match_key</c>, violate <c>ix_runs_match_key</c>, and roll back its whole ingest batch — losing
/// <c>board.start</c>'s metadata on every run since 2026-09-07 and, at random, neighbours such as
/// <c>debug.level.enter</c> and <c>board.modifiers</c>.
/// </summary>
public class RunStartAfterSelfHealTests : IDisposable
{
    readonly DataTestStore _testStore = DataTestStore.Create();
    RpgStore Store => _testStore.Store;

    public void Dispose() => _testStore.Dispose();

    static EventEnvelope Ev(string kind, string matchKey, object payload) => new()
    {
        T = DateTime.UtcNow.ToString("o"),
        Game = RpgConstants.GameId39,
        Kind = kind,
        MatchKey = matchKey,
        Payload = payload,
    };

    static object StartPayload() => new
    {
        levelName = "Adventure 2",
        levelType = "Advanture",
        boardLevel = 2,
        modifiers = new { zombieCountMultiplier = 1 },
    };

    [Fact]
    public void A_board_start_behind_a_self_healed_run_fills_that_run_and_keeps_its_batch()
    {
        var matchKey = Guid.NewGuid().ToString("N");
        Store.InsertEvents(new[]
        {
            Ev("cheat.apply", matchKey, new { id = "E-ZC" }),
            Ev("board.start", matchKey, StartPayload()),
            Ev("debug.level.enter", matchKey, new { ok = true }),
            Ev("zombie.die", matchKey, new { ptr = "0xlate", type = 0 }),
        });

        var run = Assert.Single(Store.ListRuns(), r => r.MatchKey == matchKey);
        Assert.Equal("Advanture", run.LevelType);
        Assert.Equal(2, run.BoardLevel);
        Assert.Equal("Adventure 2", run.LevelName);
        Assert.NotNull(run.Modifiers);

        var kinds = Store.ListEvents(500, 0).Where(e => e.MatchKey == matchKey).Select(e => e.Kind).ToList();
        Assert.Equal(new[] { "cheat.apply", "board.start", "debug.level.enter", "zombie.die" }, kinds);
        Assert.Equal(SoulEarnPolicy.KillDelta, Store.GetSoulBalance(1).Balance);
    }

    [Fact]
    public void A_board_start_in_a_later_batch_than_the_self_heal_also_fills_the_run()
    {
        var matchKey = Guid.NewGuid().ToString("N");
        Store.InsertEvents(new[] { Ev("debug.effect.cleared", matchKey, new { }) });
        Store.InsertEvents(new[] { Ev("board.start", matchKey, StartPayload()) });

        var run = Assert.Single(Store.ListRuns(), r => r.MatchKey == matchKey);
        Assert.Equal("Advanture", run.LevelType);
    }

    [Fact]
    public void A_repeated_board_start_keeps_the_first_metadata_and_one_run()
    {
        var matchKey = Guid.NewGuid().ToString("N");
        Store.InsertEvents(new[] { Ev("board.start", matchKey, StartPayload()) });
        Store.InsertEvents(new[] { Ev("board.start", matchKey, new { levelName = "retry", levelType = "Other", boardLevel = 9 }) });

        var run = Assert.Single(Store.ListRuns(), r => r.MatchKey == matchKey);
        Assert.Equal("Advanture", run.LevelType);
        Assert.Equal(2, run.BoardLevel);
    }
}
