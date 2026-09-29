using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Creatures;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// `battle-tempo` `forecast-rail` FR2/FR3 (spec-forecast-rail.md §2.0/§2.4): projects
/// `BattleTrace.Turns`' raw debug log into a player-facing acting order — names, never `actorKey`.
/// </summary>
public class TurnOrderRecordTests
{
    static BattleSetup Setup() => new()
    {
        WaveId = "probe",
        Squad = new[] { new BattleActorSetup { Key = "squad:0", SpeciesId = "golden-species" } },
        Wave = new[] { new BattleActorSetup { Key = "wave:0", SpeciesId = "golden-species" } },
    };

    [Fact]
    public void OnlyReadyToCommittedTransitionsBecomeTurnOrderEntries()
    {
        var trace = new BattleTrace();
        trace.Turn(1, "squad:0", TurnState.Charging, TurnState.Ready);
        trace.Turn(1, "squad:0", TurnState.Ready, TurnState.Committed); // the ONE that counts
        trace.Turn(1, "squad:0", TurnState.Committed, TurnState.Resolving);
        trace.Turn(1, "squad:0", TurnState.Resolving, TurnState.Recovering);

        var entries = TurnOrderRecord.FromTrace(trace, Setup());

        Assert.Single(entries);
        Assert.Equal(1, entries[0].Round);
    }

    [Fact]
    public void EntriesPreserveTheOrderTheyOccurredIn()
    {
        var trace = new BattleTrace();
        trace.Turn(1, "wave:0", TurnState.Ready, TurnState.Committed);
        trace.Turn(1, "squad:0", TurnState.Ready, TurnState.Committed);
        trace.Turn(2, "squad:0", TurnState.Ready, TurnState.Committed);

        var entries = TurnOrderRecord.FromTrace(trace, Setup());

        Assert.Equal(3, entries.Count);
        Assert.Equal(1, entries[0].Round);
        Assert.Equal(1, entries[1].Round);
        Assert.Equal(2, entries[2].Round);
    }

    /// <summary>§2.4: no engine vocabulary reaches the surface — a known species resolves to its
    /// real display name, never the raw `actorKey`.</summary>
    [Fact]
    public void AKnownSpeciesResolvesToItsRealDisplayNameNeverTheActorKey()
    {
        CreatureSpeciesCatalog.ConfigureFromCompiledDefault();
        var realSpeciesId = CreatureSpeciesCatalog.All[0].SpeciesId;
        var realName = CreatureSpeciesCatalog.All[0].Name;

        var setup = new BattleSetup
        {
            WaveId = "probe",
            Squad = new[] { new BattleActorSetup { Key = "squad:0", SpeciesId = realSpeciesId } },
            Wave = Array.Empty<BattleActorSetup>(),
        };
        var trace = new BattleTrace();
        trace.Turn(1, "squad:0", TurnState.Ready, TurnState.Committed);

        var entries = TurnOrderRecord.FromTrace(trace, setup);

        Assert.Single(entries);
        Assert.Equal(realName, entries[0].DisplayName);
        Assert.NotEqual("squad:0", entries[0].DisplayName); // never the actorKey
    }

    [Fact]
    public void AnUnknownSpeciesFallsBackToTheRawIdRatherThanThrowing()
    {
        var setup = Setup(); // "golden-species" is not a real catalog id
        var trace = new BattleTrace();
        trace.Turn(1, "squad:0", TurnState.Ready, TurnState.Committed);

        var entries = TurnOrderRecord.FromTrace(trace, setup);

        Assert.Single(entries);
        Assert.Equal("golden-species", entries[0].DisplayName); // the fallback, not an actorKey either
    }

    [Fact]
    public void AnEmptyTraceProjectsNothing()
    {
        var entries = TurnOrderRecord.FromTrace(new BattleTrace(), Setup());
        Assert.Empty(entries);
    }

    [Fact]
    public void NullTraceOrSetupThrows()
    {
        Assert.Throws<ArgumentNullException>(() => TurnOrderRecord.FromTrace(null!, Setup()));
        Assert.Throws<ArgumentNullException>(() => TurnOrderRecord.FromTrace(new BattleTrace(), null!));
    }
}
