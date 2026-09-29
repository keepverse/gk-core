using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// B9 / T3a — the live half of readiness. <see cref="TurnReadinessTests"/> proves the pure math;
/// this proves the kernel-FSM wiring: stored work, mid-flight rebase via <c>EventQueue.Reschedule</c>,
/// and driving <c>Charging → Ready</c>.
/// </summary>
public class ReadinessDriverTests
{
    [Fact]
    public void MidFlightHasteRebaseArrivesAtTPlusSevenFiftyNotTPlusOneThousand()
    {
        // The audit's I1 lock, reproduced end to end through the live driver rather than the pure
        // function alone: speed 100, haste 1000 (rate 100) for a 1000-work wait, half-elapsed, then
        // haste drops to 500 (rate 200, "twice as fast").
        var queue = new EventQueue(8);
        var driver = new ReadinessDriver(queue);

        driver.BeginCharging("z1", work: 1000, rate: 100, nowTick: 0);
        Assert.Equal(1000, queue.PeekDueTick());

        var newRate = TurnReadiness.EffectiveRate(speed: 100, haste: 500);
        Assert.Equal(200, newRate);
        driver.OnRateChanged("z1", newRate, nowTick: 500);

        Assert.Equal(750, queue.PeekDueTick());
        Assert.Equal(1, queue.Count); // rebase reschedules in place — it does not add a second event
    }

    [Fact]
    public void Suspension_stores_work_so_resuming_with_haste_is_faster_than_without()
    {
        // Two rebases in sequence prove work is STORED across each rebase point, not re-derived
        // from the original start tick — which is what "resuming with haste is faster" requires:
        // the second rebase's arithmetic must only ever see the work left over from the first.
        var queue = new EventQueue(8);
        var driver = new ReadinessDriver(queue);

        driver.BeginCharging("z1", work: 1000, rate: 100, nowTick: 0);   // -> due at 1000
        driver.OnRateChanged("z1", newRate: 200, nowTick: 500);          // -> due at 750 (500 work left)
        driver.OnRateChanged("z1", newRate: 100, nowTick: 600);          // slows back down at t=600

        // Work done between 500 and 600 at rate 200: 100 ticks * 200/100 = 200. Remaining: 500-200=300.
        // TicksFor(300, 100) = 300 -> due at 600+300 = 900. Still faster than the un-hasted 1000.
        Assert.Equal(900, queue.PeekDueTick());
        Assert.True(900 < 1000, "resuming after a haste window must still land before the un-hasted arrival");
    }

    [Fact]
    public void The_readiness_event_drives_charging_to_ready()
    {
        var queue = new EventQueue(8);
        var driver = new ReadinessDriver(queue);
        var actor = new ActorTurnMachine("z1");
        Assert.Equal(TurnState.Charging, actor.State);

        driver.BeginCharging("z1", work: 100, rate: 100, nowTick: 0); // -> due at 100

        var buffer = new List<ScheduledEvent>();
        Assert.Equal(1, queue.PopDue(now: 100, buffer));
        Assert.Equal((int)TimelineEventKind.Readiness, buffer[0].Kind);

        driver.OnReadinessDue(actor);
        Assert.Equal(TurnState.Ready, actor.State);
    }

    [Fact]
    public void A_rate_change_for_an_actor_not_currently_charging_is_a_no_op()
    {
        var queue = new EventQueue(8);
        var driver = new ReadinessDriver(queue);

        // No BeginCharging call at all -- driver has never heard of this actor.
        driver.OnRateChanged("ghost", newRate: 999, nowTick: 10);

        Assert.Equal(0, queue.Count); // nothing scheduled, nothing to reschedule, no throw
    }

    [Fact]
    public void A_rate_change_after_the_readiness_event_already_fired_is_a_no_op()
    {
        var queue = new EventQueue(8);
        var driver = new ReadinessDriver(queue);
        var actor = new ActorTurnMachine("z1");

        driver.BeginCharging("z1", work: 100, rate: 100, nowTick: 0);
        var buffer = new List<ScheduledEvent>();
        queue.PopDue(100, buffer);
        driver.OnReadinessDue(actor); // track is now inactive

        driver.OnRateChanged("z1", newRate: 500, nowTick: 100); // must not resurrect a fired track

        Assert.Equal(0, queue.Count);
        Assert.Equal(TurnState.Ready, actor.State); // unaffected by the stray rate change
    }

    [Fact]
    public void Restarting_a_charge_that_is_already_in_flight_does_not_leak_the_old_event()
    {
        var queue = new EventQueue(8);
        var driver = new ReadinessDriver(queue);

        driver.BeginCharging("z1", work: 1000, rate: 100, nowTick: 0);   // due at 1000
        driver.BeginCharging("z1", work: 50, rate: 100, nowTick: 10);    // restarted — due at 60

        Assert.Equal(1, queue.Count); // the first handle was cancelled, not left dangling
        Assert.Equal(60, queue.PeekDueTick());
    }
}
