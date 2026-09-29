using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests.EffectClock;

/// <summary>
/// `solid-remediation` T4.14/T4.15 (D15) — the lawn's clock as a SOURCE that feeds an advanced
/// scheduler, and the determinism that only becomes assertable once it does.
///
/// <para><b>Why this is the sharpest statement of the defect</b> (T4.15's own wording): a deterministic
/// engine whose schedule depends on wall time is a contradiction. The test below replays the same
/// delta sequence twice and demands identical times — which a wall-clock schedule cannot satisfy even
/// in principle, because the second replay happens later. That is the contract D15 makes assertable,
/// and it is why the fix is a role change rather than a deletion: the wall clock still supplies the
/// seed, it just stops answering every question.</para>
///
/// <para>Durations stay integer milliseconds throughout — T4.15's note names the `green-baseline`
/// incident where `duration = ms / 1000.0` produced values the validator refuses, so nothing here
/// reintroduces fractional seconds as a stored quantity.</para>
/// </summary>
[Trait("VerificationId", "core.advanced-effect-clock")]
public class AdvancedEffectClockTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    static readonly double[] FrameDeltas =
    {
        // A realistic, uneven frame sequence: a steady run, one long frame (an alt-tab or a GC pause),
        // then steady again. The unevenness is the point — a uniform sequence would agree under almost
        // any scheduler.
        0.016, 0.016, 0.017, 0.016, 0.250, 0.016, 0.016, 0.033, 0.016, 0.016,
    };

    static DateTimeOffset ReplayOnce()
    {
        var clock = new AdvancedEffectClock(Start);
        foreach (var d in FrameDeltas) clock.AdvanceSeconds(d);
        return clock.UtcNow;
    }

    [Fact]
    public void The_same_scenario_replayed_twice_produces_identical_times()
    {
        // T4.15's acceptance, stated exactly. Two replays of one delta sequence, and the clock must
        // land on the same instant — not "close", the same. A wall-clock schedule fails this by
        // construction, which is the whole argument for the role split.
        Assert.Equal(ReplayOnce(), ReplayOnce());
    }

    [Fact]
    public void Replay_is_independent_of_when_the_replay_happens()
    {
        // The other half of the same claim: the result must not depend on real elapsed time between
        // the two runs. Seeding from a FIXED start is what buys this; seeding from the wall clock (as
        // the live host legitimately does) is still fine, because the live host is not replayed.
        var first = ReplayOnce();
        Thread.Sleep(15);
        Assert.Equal(first, ReplayOnce());
    }

    [Fact]
    public void One_number_answers_both_questions_so_a_pulse_and_an_expiry_cannot_disagree()
    {
        // D15 in one assertion. A status applied for 250 ms and a pulse grid of 100 ms are read off
        // the SAME advanced value, so after 300 ms of frames the status is expired and exactly three
        // pulse boundaries have passed — both derived from one clock rather than two.
        var clock = new AdvancedEffectClock(Start);
        var expiresAt = clock.UtcNow.AddMilliseconds(250);

        var pulses = 0;
        var nextPulse = clock.UtcNow.AddMilliseconds(100);
        for (var i = 0; i < 30; i++) // 30 x 10 ms = 300 ms
        {
            clock.AdvanceSeconds(0.010);
            while (clock.UtcNow >= nextPulse)
            {
                pulses++;
                nextPulse = nextPulse.AddMilliseconds(100);
            }
        }

        Assert.Equal(3, pulses);
        Assert.True(clock.UtcNow >= expiresAt);
    }

    [Fact]
    public void Time_never_runs_backwards_even_when_a_host_hands_over_a_negative_delta()
    {
        // A live host can produce one across a pause or a system clock adjustment. A schedule that ran
        // backwards would RESURRECT expired statuses — far worse than a tick that stands still, which
        // is why this is ignored rather than rejected.
        var clock = new AdvancedEffectClock(Start);
        clock.AdvanceSeconds(1.0);
        var afterOneSecond = clock.UtcNow;

        clock.AdvanceSeconds(-5.0);
        clock.Advance(TimeSpan.FromSeconds(-5));

        Assert.Equal(afterOneSecond, clock.UtcNow);
    }

    [Fact]
    public void A_degenerate_delta_is_ignored_rather_than_poisoning_the_schedule()
    {
        // NaN or infinity reaching a DateTimeOffset.Add throws, and a frame-delta source is exactly
        // where one appears (a division by a zero frame time). Ignoring keeps the schedule usable; the
        // alternative takes down a live match for one bad frame.
        var clock = new AdvancedEffectClock(Start);
        clock.AdvanceSeconds(double.NaN);
        clock.AdvanceSeconds(double.PositiveInfinity);
        clock.AdvanceSeconds(0.0);

        Assert.Equal(Start, clock.UtcNow);
    }

    [Fact]
    public void The_seed_is_the_hosts_to_supply_and_this_type_has_no_opinion_about_it()
    {
        // The role split, asserted from the Core side. The wall clock remains the SOURCE — but the READ
        // happens at the host's composition root, not here: WorldDeterminismGuardTests refused a
        // `StartingNow()` convenience on this type, correctly, because Core sits inside the
        // world-simulation purity scan and a wall-clock read there is one no matter how narrow.
        var fixedStart = DateTimeOffset.Parse("2031-07-04T12:00:00Z");
        Assert.Equal(fixedStart, new AdvancedEffectClock(fixedStart).UtcNow);

        var live = new AdvancedEffectClock(DateTimeOffset.UtcNow);
        Assert.True(live.UtcNow <= DateTimeOffset.UtcNow);
    }
}
