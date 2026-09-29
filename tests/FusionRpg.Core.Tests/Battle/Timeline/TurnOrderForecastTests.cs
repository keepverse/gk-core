using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// battle-timeline **T8 / B19** (spec-turn-order-forecast.md) — the turn-order forecast is a READ of
/// the queue, not a second model of it.
///
/// <para>The load-bearing test is <see cref="TheForecastEqualsTheRealDrain"/>: if a forecast ever
/// disagrees with what actually pops, the queue was not the single source of truth after all, which is
/// the exact property the map says this module exists to validate.</para>
/// </summary>
public class TurnOrderForecastTests
{
    static EventQueue Queue(params (long due, string owner)[] events)
    {
        var q = new EventQueue();
        foreach (var (due, owner) in events) q.Schedule(due, owner, kind: 1, tag: 0);
        return q;
    }

    /// <summary>The acceptance the map names.</summary>
    [Fact]
    public void TheForecastEqualsTheRealDrain()
    {
        var q = Queue((300, "c"), (100, "a"), (200, "b"), (100, "a2"), (500, "e"));

        var forecast = TurnOrderForecast.Project(q, 5).Select(e => e.OwnerKey).ToList();

        var drained = new List<ScheduledEvent>();
        q.PopDue(long.MaxValue, drained);

        Assert.Equal(drained.Select(e => e.OwnerKey), forecast);
    }

    /// <summary>The other half of the acceptance: projecting must not consume.</summary>
    [Fact]
    public void TheQueueIsObservablyUnchanged()
    {
        var q = Queue((300, "c"), (100, "a"), (200, "b"));
        var countBefore = q.Count;
        var headBefore = q.PeekDueTick();

        var first = TurnOrderForecast.Project(q, 3).Select(e => e.OwnerKey).ToList();
        var second = TurnOrderForecast.Project(q, 3).Select(e => e.OwnerKey).ToList();

        Assert.Equal(countBefore, q.Count);
        Assert.Equal(headBefore, q.PeekDueTick());
        Assert.Equal(first, second);   // idempotent, because it mutates nothing
    }

    /// <summary>
    /// ⭐ **The test a falsifier had to teach me to write.** Planting the obvious defect — sorting the
    /// LIVE heap instead of a copy — passed every other test in this file, because a fully sorted array
    /// is still a valid min-heap: `Count`, `PeekDueTick` and pop order all stay correct. What it
    /// silently corrupts is `_indexOf`, the seq-to-position map, so the damage only surfaces when a
    /// handle is used AFTERWARDS. Cancelling and rescheduling after a forecast is therefore the only
    /// assertion that actually catches a mutating projection.
    /// </summary>
    [Fact]
    public void HandlesStillWorkAfterAForecast()
    {
        // ⚠️ The insertion ORDER here is load-bearing, and it took two failed attempts to get right.
        // A min-heap's array is sorted for ascending inserts AND for descending ones (400,300,200,100
        // sifts all the way to [100,200,300,400]), so an in-place sort is a no-op in both and this test
        // passed for the wrong reason twice. An array is only genuinely unsorted when a shallower
        // sibling exceeds a deeper one — the classic [1,5,2] shape — so the keys below interleave to
        // force exactly that, and an in-place sort really does move slots out from under `_indexOf`.
        var q = new EventQueue();
        q.Schedule(100, "a0", 1, 0);
        q.Schedule(900, "a1", 1, 0);
        var doomed = q.Schedule(200, "doomed", 1, 0);
        q.Schedule(950, "a2", 1, 0);
        q.Schedule(800, "a3", 1, 0);
        var moved = q.Schedule(300, "moved", 1, 0);
        var a = q.Schedule(400, "a", 1, 0);
        q.Schedule(700, "d", 1, 0);

        // Forecast first — if this reorders the live heap, the handles below address the wrong slots.
        TurnOrderForecast.Project(q, 4);

        Assert.True(q.Cancel(doomed), "cancelling a live handle must still work after a forecast");
        Assert.True(q.Reschedule(moved, 50), "rescheduling a live handle must still work after a forecast");
        Assert.False(q.Cancel(doomed), "a cancelled handle must stay cancelled");

        var drained = new List<ScheduledEvent>();
        q.PopDue(long.MaxValue, drained);
        // "doomed" is gone, "moved" jumped to the front at tick 50, everything else keeps its order.
        Assert.Equal(new[] { "moved", "a0", "a", "d", "a3", "a1", "a2" }, drained.Select(e => e.OwnerKey));
        Assert.False(q.Cancel(a), "a fired handle is a no-op, not a corruption");
    }

    [Fact]
    public void TiesFollowInsertionOrder_theQueuesOwnTotalOrder()
    {
        var q = new EventQueue();
        q.Schedule(100, "first", 1, 0);
        q.Schedule(100, "second", 1, 0);
        q.Schedule(100, "third", 1, 0);

        Assert.Equal(new[] { "first", "second", "third" },
            TurnOrderForecast.Project(q, 3).Select(e => e.OwnerKey));
    }

    [Fact]
    public void ACancelledEventNeverAppears()
    {
        var q = new EventQueue();
        q.Schedule(100, "a", 1, 0);
        var doomed = q.Schedule(150, "cancelled", 1, 0);
        q.Schedule(200, "b", 1, 0);
        Assert.True(q.Cancel(doomed));

        Assert.Equal(new[] { "a", "b" }, TurnOrderForecast.Project(q, 10).Select(e => e.OwnerKey));
    }

    [Fact]
    public void ARescheduledEventForecastsAtItsNewPosition()
    {
        var q = new EventQueue();
        var moved = q.Schedule(100, "moved", 1, 0);
        q.Schedule(200, "b", 1, 0);
        Assert.True(q.Reschedule(moved, 300));

        Assert.Equal(new[] { "b", "moved" }, TurnOrderForecast.Project(q, 10).Select(e => e.OwnerKey));
    }

    // ---- k is a bound, not a promise ----

    [Fact]
    public void FewerEventsThanAskedForReturnsWhatExists()
    {
        var q = Queue((100, "a"), (200, "b"));
        Assert.Equal(2, TurnOrderForecast.Project(q, 10).Count);
    }

    [Fact]
    public void ZeroIsEmptyAndAnEmptyQueueIsEmpty()
    {
        Assert.Empty(TurnOrderForecast.Project(Queue((100, "a")), 0));
        Assert.Empty(TurnOrderForecast.Project(new EventQueue(), 5));
    }

    [Fact]
    public void ANegativeLengthIsRefused_notTreatedAsZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TurnOrderForecast.Project(Queue((100, "a")), -1));
    }

    [Fact]
    public void TheBufferOverloadAppendsAndReportsHowManyItWrote()
    {
        var q = Queue((100, "a"), (200, "b"), (300, "c"));
        var into = new List<ScheduledEvent> { new(0, 0, "pre-existing", 0, 0) };

        var written = TurnOrderForecast.Project(q, 2, into);

        Assert.Equal(2, written);
        Assert.Equal(3, into.Count);                       // appended, not cleared
        Assert.Equal("pre-existing", into[0].OwnerKey);
    }

    // ---- per-profile exactness ----

    [Fact]
    public void NextEventProfilesAreExactAndFixedIncrementIsSoftBounded()
    {
        Assert.Equal(ForecastExactness.Exact,
            TurnOrderForecast.ExactnessFor(BattleModeProfileCatalog.ClassicRound));
        Assert.Equal(ForecastExactness.Exact,
            TurnOrderForecast.ExactnessFor(BattleModeProfileCatalog.GalaxySync));
        Assert.Equal(ForecastExactness.SoftBounded,
            TurnOrderForecast.ExactnessFor(BattleModeProfileCatalog.HybridAtb));
    }

    /// <summary>
    /// ⛔ **Exactness is DECLARED per row, not derived from the advance policy — and that is the
    /// architecture's choice, not a shortcut.** The obvious implementation
    /// (`AdvancePolicy == NextEvent ? Exact : SoftBounded`) is rejected by
    /// `ModeProfileArchitectureTests` in *every* file: its profile-id exemption covers id literals
    /// only, never a branch on `AdvancePolicyKind`. The map's acceptance is structural — "adding a
    /// mode adds a row, never a branch in the kernel" — and a computed property is a branch wearing a
    /// row's clothes.
    ///
    /// <para>The consequence, asserted here so nobody mistakes it for a bug: changing a profile's
    /// advance policy with `with` does NOT change its exactness. A fourth mode must state its own.</para>
    /// </summary>
    [Fact]
    public void ExactnessIsADeclaredRowField_notDerivedFromTheAdvancePolicy()
    {
        var renamed = BattleModeProfileCatalog.ClassicRound with { ProfileId = "some-future-mode" };
        Assert.Equal(ForecastExactness.Exact, TurnOrderForecast.ExactnessFor(renamed));

        // Advance policy changed, exactness deliberately unchanged — it is data on the row.
        var stepped = BattleModeProfileCatalog.ClassicRound with
        {
            ProfileId = "some-future-mode",
            AdvancePolicy = AdvancePolicyKind.FixedIncrement
        };
        Assert.Equal(ForecastExactness.Exact, TurnOrderForecast.ExactnessFor(stepped));

        // A row that means it says so.
        var declared = stepped with { ForecastExactness = ForecastExactness.SoftBounded };
        Assert.Equal(ForecastExactness.SoftBounded, TurnOrderForecast.ExactnessFor(declared));
    }
}
