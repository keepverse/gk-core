// battle-tempo reaction-lane RL4, executed standalone (Core.Tests blocked). Mirrors
// gk-core/tests/FusionRpg.Core.Tests/Battle/Timeline/ReactionLaneOutcomesTests.cs case-for-case.

using FusionRpg.Core.Battle.Timeline;

var failures = 0;
void Check(string name, bool condition)
{
    if (condition) { Console.WriteLine($"PASS  {name}"); return; }
    Console.WriteLine($"FAIL  {name}");
    failures++;
}

{
    var lane = new ReactionLane(0);
    Check("WReactZeroAlwaysRefusesWithNoLane", lane.TryEnter("a", "left") == ReactionOutcome.NoLane);
}
{
    var lane = new ReactionLane(1);
    var o = lane.TryEnter("a", "left");
    Check("EnteringWithinCapacityAndDepthSucceeds", o == ReactionOutcome.Entered && lane.Depth == 1);
}
{
    var lane = new ReactionLane(1);
    var first = lane.TryEnter("a", "left");
    var second = lane.TryEnter("b", "left");
    Check("ASecondEntryWhileTheOneSlotIsHeldRefusesWithNoSlot", first == ReactionOutcome.Entered && second == ReactionOutcome.NoSlot);
}
{
    var lane = new ReactionLane(ReactionLane.DepthLimit + 5);
    var allEntered = true;
    for (var i = 0; i < ReactionLane.DepthLimit; i++)
        if (lane.TryEnter($"actor{i}", "left") != ReactionOutcome.Entered) allEntered = false;
    var overLimit = lane.TryEnter("oneTooMany", "left");
    Check("ExceedingDepthLimitRefusesWithDepthExceeded", allEntered && overLimit == ReactionOutcome.DepthExceeded && lane.Depth == ReactionLane.DepthLimit);
}
{
    var lane = new ReactionLane(1);
    lane.TryEnter("a", "left");
    lane.Exit("a");
    var reentered = lane.TryEnter("b", "left");
    Check("ExitReleasesTheSlotAndPopsOneNestingLevel", lane.Depth == 1 && reentered == ReactionOutcome.Entered);
}
{
    var lane = new ReactionLane(1);
    lane.TryEnter("a", "left");
    lane.Exit("a");
    lane.Exit("a");
    Check("ExitIsIdempotentPerAcquire", lane.Depth == 0);
}
{
    (List<ReactionOutcome> Outcomes, int FinalDepth) Run()
    {
        var lane = new ReactionLane(2);
        var outcomes = new List<ReactionOutcome>
        {
            lane.TryEnter("a", "left"),
            lane.TryEnter("b", "left"),
            lane.TryEnter("c", "left"),
        };
        lane.Exit("a");
        outcomes.Add(lane.TryEnter("c", "left"));
        return (outcomes, lane.Depth);
    }
    var first = Run();
    var second = Run();
    Check("TheIdenticalCallSequenceProducesIdenticalOutcomesAndFinalDepth",
        first.Outcomes.SequenceEqual(second.Outcomes) && first.FinalDepth == second.FinalDepth);
}
{
    // Three DISTINCT actor keys -- ActionSlots refuses a SECOND concurrent acquire by the SAME actor
    // (found via this probe: a naive "defender enters twice, for block then riposte" scenario got
    // NoSlot on the second entry, which is ActionSlots' own one-slot-per-actor contract working
    // correctly, not a ReactionLane defect). This test is about NESTING DEPTH, not about which actor
    // holds which level, so three keys isolate exactly that property.
    var lane = new ReactionLane(4);
    var hit = lane.TryEnter("attacker", "left");
    var block = lane.TryEnter("defender", "right");
    var riposte = lane.TryEnter("bystander", "right");
    var atLimit = lane.Depth == ReactionLane.DepthLimit;
    var fourth = lane.TryEnter("someone", "left");
    Check("TheNamedWorstCaseChainLandsExactlyAtTheLimitAndAFourthLevelIsRefused",
        hit == ReactionOutcome.Entered && block == ReactionOutcome.Entered && riposte == ReactionOutcome.Entered &&
        atLimit && fourth == ReactionOutcome.DepthExceeded);
}
{
    try { _ = new ReactionLane(-1); Check("NegativeWReactThrows", false); }
    catch (ArgumentOutOfRangeException) { Check("NegativeWReactThrows", true); }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL PROBES PASSED" : $"{failures} PROBE(S) FAILED");
Environment.Exit(failures == 0 ? 0 : 1);
