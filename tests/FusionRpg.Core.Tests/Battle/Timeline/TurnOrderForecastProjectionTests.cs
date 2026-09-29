using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// `battle-tempo` `forecast-rail` FR4 (spec-forecast-rail.md §2.1: "the projection never mutates" —
/// asserted here, not assumed). `TurnOrderForecast.Project` must leave the queue byte-identical:
/// same `Count`, same `PeekDueTick`, and a subsequent real `PopDue` must yield exactly what the
/// forecast said it would.
/// </summary>
public class TurnOrderForecastProjectionTests
{
    static EventQueue SeededQueue()
    {
        var q = new EventQueue();
        q.Schedule(100, "a", 0, 0);
        q.Schedule(50, "b", 0, 0);
        q.Schedule(75, "c", 0, 0);
        q.Schedule(50, "d", 0, 0); // tie on DueTick with "b" -- Seq breaks it
        q.Schedule(200, "e", 0, 0);
        return q;
    }

    [Fact]
    public void ProjectingLeavesCountAndPeekDueTickUnchanged()
    {
        var q = SeededQueue();
        var countBefore = q.Count;
        var peekBefore = q.PeekDueTick();

        var projected = TurnOrderForecast.Project(q, max: 3);

        Assert.Equal(3, projected.Count);
        Assert.Equal(countBefore, q.Count);
        Assert.Equal(peekBefore, q.PeekDueTick());
    }

    [Fact]
    public void ProjectingRepeatedlyIsIdempotentAndSideEffectFree()
    {
        var q = SeededQueue();

        var first = TurnOrderForecast.Project(q, max: 5);
        var second = TurnOrderForecast.Project(q, max: 5);
        var third = TurnOrderForecast.Project(q, max: 5);

        Assert.Equal(first, second);
        Assert.Equal(second, third);
        Assert.Equal(5, q.Count); // never drained across repeated projections
    }

    /// <summary>The acceptance line's own wording, made literal: "a subsequent real drain yields
    /// exactly what the forecast said."</summary>
    [Fact]
    public void ASubsequentRealDrainYieldsExactlyWhatTheForecastSaid()
    {
        var q = SeededQueue();
        var forecast = TurnOrderForecast.Project(q, max: 5);

        var drained = new List<ScheduledEvent>();
        q.PopDue(now: long.MaxValue, drained); // drain everything due by "the end of time"

        Assert.Equal(forecast, drained);
    }

    [Fact]
    public void ProjectingFewerThanScheduledReturnsTheBoundNotAnError()
    {
        var q = SeededQueue();
        var projected = TurnOrderForecast.Project(q, max: 2);
        Assert.Equal(2, projected.Count);
        Assert.Equal(5, q.Count); // still untouched
    }

    [Fact]
    public void ProjectingMoreThanScheduledReturnsWhatExists()
    {
        var q = SeededQueue();
        var projected = TurnOrderForecast.Project(q, max: 100);
        Assert.Equal(5, projected.Count);
    }

    [Fact]
    public void OrderMatchesTheRealPopOrderExactlyAcrossATie()
    {
        var q = SeededQueue();
        var projected = TurnOrderForecast.Project(q, max: 5);

        // b and d tie on DueTick=50 -- Seq (schedule order) must break the tie, matching PopDue.
        Assert.Equal("b", projected[0].OwnerKey);
        Assert.Equal("d", projected[1].OwnerKey);
        Assert.Equal("c", projected[2].OwnerKey);
        Assert.Equal("a", projected[3].OwnerKey);
        Assert.Equal("e", projected[4].OwnerKey);
    }

    [Fact]
    public void NegativeMaxIsRefused()
    {
        var q = SeededQueue();
        Assert.Throws<ArgumentOutOfRangeException>(() => TurnOrderForecast.Project(q, max: -1));
    }

    [Fact]
    public void NullQueueIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => TurnOrderForecast.Project(null!, max: 1));
    }

    [Fact]
    public void AnEmptyQueueProjectsNothing()
    {
        var q = new EventQueue();
        var projected = TurnOrderForecast.Project(q, max: 5);
        Assert.Empty(projected);
    }
}
