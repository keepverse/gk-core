using System;
using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Activity;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Saves;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// SE4.21 (G4) — Tier A awards name their owner. `ApplyRpgProgressionFromActivityUnlocked` takes the
/// save and refuses a fact whose run belongs to another save; a fact whose run resolves to no save
/// awards nothing; `CommanderLevelOf(SaveId, EmpireId)` reads the commander's own row.
/// </summary>
public class ProgressionEmpireAwardTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ProgressionEmpireAwardTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

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

    static PvzActivityAppendRequest KillFor(long runId) => new()
    {
        Kind = PvzActivityKinds.ZombieKilled,
        RunId = runId,
        DedupeKey = "kill-" + Guid.NewGuid().ToString("N"),
    };

    [Fact]
    public void A_fact_for_another_saves_run_is_refused_not_reattributed()
    {
        var saveA = _store.CreatePlayer("AwardMismatchA").Id;
        var saveB = _store.CreatePlayer("AwardMismatchB").Id;
        var runA = StartRun(saveA);

        var ex = Assert.Throws<InvalidOperationException>(
            () => _store.AppendPvzActivityFact(saveB, KillFor(runA)));
        Assert.Contains(saveA.ToString(), ex.Message);
        Assert.Contains(saveB.ToString(), ex.Message);

        // The refusal rolls the whole append back: neither save gains a row.
        Assert.Null(_store.GetRpgActor(saveA, RpgActorKinds.Player, 0));
        Assert.Null(_store.GetRpgActor(saveB, RpgActorKinds.Player, 0));
    }

    [Fact]
    public void A_fact_for_an_unknown_run_awards_nothing()
    {
        var save = _store.CreatePlayer("AwardNoRun").Id;

        var result = _store.AppendPvzActivityFact(save, KillFor(999999));

        Assert.Empty(result.Progression);
        Assert.Null(_store.GetRpgActor(save, RpgActorKinds.Player, 0));
    }

    [Fact]
    public void A_fact_for_the_runs_own_save_still_awards()
    {
        var save = _store.CreatePlayer("AwardOwnRun").Id;
        var run = StartRun(save);

        var result = _store.AppendPvzActivityFact(save, KillFor(run));

        Assert.NotEmpty(result.Progression);
        Assert.NotNull(_store.GetRpgActor(save, RpgActorKinds.Player, 0));
    }

    [Fact]
    public void Commander_level_reads_the_named_empires_player_row()
    {
        var save = _store.CreatePlayer("AwardCommander").Id;
        _store.SeedRpgProgressionDemo(save);

        var expected = _store.GetRpgActor(save, RpgActorKinds.Player, 0);
        Assert.NotNull(expected);
        Assert.Equal(expected!.Level, _store.CommanderLevelOf(new SaveId(save), EmpireId.Dave));
        // An empire with no row is a fresh commander, not an error.
        Assert.Equal(1, _store.CommanderLevelOf(new SaveId(save), EmpireId.Zomboss));
    }
}
