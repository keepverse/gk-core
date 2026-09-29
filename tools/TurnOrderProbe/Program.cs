// battle-tempo forecast-rail FR2/FR3, executed standalone (Core.Tests blocked). Mirrors
// gk-core/tests/FusionRpg.Core.Tests/Battle/Timeline/TurnOrderRecordTests.cs case-for-case.

using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Creatures;

CreatureSpeciesCatalog.ConfigureFromCompiledDefault();

var failures = 0;
void Check(string name, bool condition)
{
    if (condition) { Console.WriteLine($"PASS  {name}"); return; }
    Console.WriteLine($"FAIL  {name}");
    failures++;
}

BattleSetup Setup() => new()
{
    WaveId = "probe",
    Squad = new[] { new BattleActorSetup { Key = "squad:0", SpeciesId = "golden-species" } },
    Wave = new[] { new BattleActorSetup { Key = "wave:0", SpeciesId = "golden-species" } },
};

{
    var trace = new BattleTrace();
    trace.Turn(1, "squad:0", TurnState.Charging, TurnState.Ready);
    trace.Turn(1, "squad:0", TurnState.Ready, TurnState.Committed);
    trace.Turn(1, "squad:0", TurnState.Committed, TurnState.Resolving);
    trace.Turn(1, "squad:0", TurnState.Resolving, TurnState.Recovering);
    var entries = TurnOrderRecord.FromTrace(trace, Setup());
    Check("OnlyReadyToCommittedTransitionsBecomeTurnOrderEntries", entries.Count == 1 && entries[0].Round == 1);
}
{
    var trace = new BattleTrace();
    trace.Turn(1, "wave:0", TurnState.Ready, TurnState.Committed);
    trace.Turn(1, "squad:0", TurnState.Ready, TurnState.Committed);
    trace.Turn(2, "squad:0", TurnState.Ready, TurnState.Committed);
    var entries = TurnOrderRecord.FromTrace(trace, Setup());
    Check("EntriesPreserveTheOrderTheyOccurredIn",
        entries.Count == 3 && entries[0].Round == 1 && entries[1].Round == 1 && entries[2].Round == 2);
}
{
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
    Console.WriteLine($"  DEBUG: realSpeciesId={realSpeciesId} realName={realName} resolved={entries[0].DisplayName}");
    Check("AKnownSpeciesResolvesToItsRealDisplayNameNeverTheActorKey",
        entries.Count == 1 && entries[0].DisplayName == realName && entries[0].DisplayName != "squad:0");
}
{
    var setup = Setup();
    var trace = new BattleTrace();
    trace.Turn(1, "squad:0", TurnState.Ready, TurnState.Committed);
    var entries = TurnOrderRecord.FromTrace(trace, setup);
    Check("AnUnknownSpeciesFallsBackToTheRawIdRatherThanThrowing", entries.Count == 1 && entries[0].DisplayName == "golden-species");
}
{
    var entries = TurnOrderRecord.FromTrace(new BattleTrace(), Setup());
    Check("AnEmptyTraceProjectsNothing", entries.Count == 0);
}
{
    var threwForTrace = false;
    try { TurnOrderRecord.FromTrace(null!, Setup()); } catch (ArgumentNullException) { threwForTrace = true; }
    var threwForSetup = false;
    try { TurnOrderRecord.FromTrace(new BattleTrace(), null!); } catch (ArgumentNullException) { threwForSetup = true; }
    Check("NullTraceOrSetupThrows", threwForTrace && threwForSetup);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL PROBES PASSED" : $"{failures} PROBE(S) FAILED");
Environment.Exit(failures == 0 ? 0 : 1);
