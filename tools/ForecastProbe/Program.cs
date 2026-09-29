// battle-tempo FR4, executed standalone (Core.Tests blocked, see PoiseProbe's header). Mirrors
// gk-core/tests/FusionRpg.Core.Tests/Battle/Timeline/TurnOrderForecastProjectionTests.cs case-for-case.

using FusionRpg.Core.Battle.Timeline;

var failures = 0;
void Check(string name, bool condition)
{
    if (condition) { Console.WriteLine($"PASS  {name}"); return; }
    Console.WriteLine($"FAIL  {name}");
    failures++;
}

EventQueue SeededQueue()
{
    var q = new EventQueue();
    q.Schedule(100, "a", 0, 0);
    q.Schedule(50, "b", 0, 0);
    q.Schedule(75, "c", 0, 0);
    q.Schedule(50, "d", 0, 0);
    q.Schedule(200, "e", 0, 0);
    return q;
}

{
    var q = SeededQueue();
    var countBefore = q.Count;
    var peekBefore = q.PeekDueTick();
    var projected = TurnOrderForecast.Project(q, max: 3);
    Check("ProjectingLeavesCountAndPeekDueTickUnchanged", projected.Count == 3 && q.Count == countBefore && q.PeekDueTick() == peekBefore);
}

{
    var q = SeededQueue();
    var first = TurnOrderForecast.Project(q, max: 5);
    var second = TurnOrderForecast.Project(q, max: 5);
    var third = TurnOrderForecast.Project(q, max: 5);
    Check("ProjectingRepeatedlyIsIdempotentAndSideEffectFree", first.SequenceEqual(second) && second.SequenceEqual(third) && q.Count == 5);
}

{
    var q = SeededQueue();
    var forecast = TurnOrderForecast.Project(q, max: 5);
    var drained = new List<ScheduledEvent>();
    q.PopDue(long.MaxValue, drained);
    Check("ASubsequentRealDrainYieldsExactlyWhatTheForecastSaid", forecast.SequenceEqual(drained));
}

{
    var q = SeededQueue();
    var projected = TurnOrderForecast.Project(q, max: 2);
    Check("ProjectingFewerThanScheduledReturnsTheBoundNotAnError", projected.Count == 2 && q.Count == 5);
}

{
    var q = SeededQueue();
    var projected = TurnOrderForecast.Project(q, max: 100);
    Check("ProjectingMoreThanScheduledReturnsWhatExists", projected.Count == 5);
}

{
    var q = SeededQueue();
    var p = TurnOrderForecast.Project(q, max: 5);
    Check("OrderMatchesTheRealPopOrderExactlyAcrossATie",
        p[0].OwnerKey == "b" && p[1].OwnerKey == "d" && p[2].OwnerKey == "c" && p[3].OwnerKey == "a" && p[4].OwnerKey == "e");
}

{
    var q = SeededQueue();
    try { TurnOrderForecast.Project(q, max: -1); Check("NegativeMaxIsRefused", false); }
    catch (ArgumentOutOfRangeException) { Check("NegativeMaxIsRefused", true); }
}

{
    try { TurnOrderForecast.Project(null!, max: 1); Check("NullQueueIsRefused", false); }
    catch (ArgumentNullException) { Check("NullQueueIsRefused", true); }
}

{
    var q = new EventQueue();
    var projected = TurnOrderForecast.Project(q, max: 5);
    Check("AnEmptyQueueProjectsNothing", projected.Count == 0);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL PROBES PASSED" : $"{failures} PROBE(S) FAILED");
Environment.Exit(failures == 0 ? 0 : 1);
