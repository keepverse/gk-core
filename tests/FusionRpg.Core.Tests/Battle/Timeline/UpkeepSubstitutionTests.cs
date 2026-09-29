using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// T13 / B26 — the substitution proof: a recurring 100 ms upkeep event on the kernel must fire the
/// same number of times, in the same order, as the two accumulator grids it replaces.
///
/// <para><b>Why this lives in Core.</b> The real handlers are injector-side and CI never builds the
/// injector, so the thing under test here is the <i>scheduling</i> — pulse count, ordering, and drift
/// — which is exactly what B26 changes. The handlers themselves are unchanged code called from a new
/// place.</para>
///
/// <para>The recurrence mirrors <c>KernelDriveHost</c>'s: re-arm at <c>e.DueTick + period</c>, never
/// at "now". That distinction is the whole point of the drift test below.</para>
/// </summary>
public class UpkeepSubstitutionTests
{
    const long Period = 100;
    const int KindDot = 1;
    const int KindShield = 2;

    sealed class Harness
    {
        public readonly SimulationClock Clock = new();
        public readonly EventQueue Queue = new(64);
        public readonly List<(long Tick, int Kind)> Fired = new();
        public readonly TimelineDrive Drive;
        long _t;

        public Harness(long stopwatchStep = 0)
        {
            Drive = new TimelineDrive(Clock, Queue, Dispatch, () => _t += stopwatchStep);
            // Same order KernelDriveHost.BeginBoard uses: DoT scheduled first, so it sorts ahead of
            // shield upkeep at every shared tick via (DueTick, Seq).
            Queue.Schedule(Period, "match", KindDot, 0);
            Queue.Schedule(Period, "match", KindShield, 0);
        }

        void Dispatch(ScheduledEvent e)
        {
            Fired.Add((e.DueTick, e.Kind));
            Queue.Schedule(e.DueTick + Period, e.OwnerKey, e.Kind, e.Tag);
        }

        public int Count(int kind) => Fired.Count(f => f.Kind == kind);
    }

    /// <summary>
    /// One second of 60 fps frames must produce exactly ten pulses of each kind — the count the
    /// 100 ms grids produce. Frame time is 16 667 µs, which divides into 100 ms unevenly on purpose:
    /// it is the case a truncating clock gets wrong.
    /// </summary>
    [Fact]
    public void Sixty_frames_of_sixty_fps_produce_exactly_ten_upkeep_pulses_of_each_kind()
    {
        var h = new Harness();
        for (var i = 0; i < 60; i++) h.Drive.Tick(16_667, budgetTicks: long.MaxValue);

        Assert.Equal(1000, h.Clock.Now);            // 60 × 16.667 ms, carried
        Assert.Equal(10, h.Count(KindDot));
        Assert.Equal(10, h.Count(KindShield));
    }

    /// <summary>
    /// The frame order the grids had — `drain → TickDots → TickShields`, which
    /// `shield-system-spec.md` §2.6 requires so an expiring shield still absorbs its final frame's
    /// damage. On the kernel that ordering is `(DueTick, Seq)`, so it holds only because DoT is
    /// scheduled first. This test fails if that scheduling order is ever swapped.
    /// </summary>
    [Fact]
    public void At_every_shared_tick_the_dot_pulse_fires_before_shield_upkeep()
    {
        var h = new Harness();
        for (var i = 0; i < 60; i++) h.Drive.Tick(16_667, budgetTicks: long.MaxValue);

        Assert.NotEmpty(h.Fired);
        for (var i = 0; i < h.Fired.Count; i += 2)
        {
            Assert.Equal(h.Fired[i].Tick, h.Fired[i + 1].Tick);
            Assert.Equal(KindDot, h.Fired[i].Kind);
            Assert.Equal(KindShield, h.Fired[i + 1].Kind);
        }
    }

    /// <summary>
    /// <b>The defect B26 exists to close.</b> A 2 s hitch owes 20 upkeep steps of each kind. The
    /// legacy shield grid runs all of them inside one Unity frame (an unbounded <c>while</c> on the
    /// main thread); the kernel must spread them across frames under the budget and still deliver
    /// every one.
    /// </summary>
    [Fact]
    public void A_two_second_hitch_delivers_every_pulse_but_never_in_one_frame()
    {
        var h = new Harness(stopwatchStep: 10);     // every timestamp read is 10 ticks later
        h.Drive.Tick(2_000_000, budgetTicks: 1);    // 2 s in one frame, budget blown immediately

        var firstFrame = h.Fired.Count;
        Assert.True(firstFrame < 40, $"the whole backlog ran in one frame ({firstFrame} pulses) — unbounded");

        var frames = 1;
        while (h.Drive.Backlogged && ++frames <= 500) h.Drive.Tick(0, budgetTicks: 1);

        Assert.Equal(20, h.Count(KindDot));
        Assert.Equal(20, h.Count(KindShield));
    }

    /// <summary>
    /// <b>Re-arming off `DueTick`, not "now".</b> Every pulse must land on an exact multiple of the
    /// period even when the drain deferred it by several frames — otherwise a stuttering machine
    /// permanently slows DoT cadence instead of merely delaying it, which is the same drift the
    /// carry-corrected clock exists to prevent.
    ///
    /// <para><b>This test alone does not catch that mistake, and the first draft of this comment
    /// claimed it did.</b> Falsified: re-arming at <c>Clock.Now + Period</c> leaves this assertion
    /// green, because in this scenario the clock has already reached 2 000 — itself a multiple of the
    /// period — so the drifted schedule still lands on multiples. What actually reddens on that
    /// mistake is the pair of <i>count</i> tests, which is where the drift becomes visible. Kept as a
    /// narrower guard against a schedule landing off-grid, with its real reach stated rather than
    /// assumed.</para>
    /// </summary>
    [Fact]
    public void Deferred_pulses_still_land_on_exact_period_multiples()
    {
        var h = new Harness(stopwatchStep: 10);
        h.Drive.Tick(2_000_000, budgetTicks: 1);
        var frames = 0;
        while (h.Drive.Backlogged && ++frames <= 500) h.Drive.Tick(0, budgetTicks: 1);

        Assert.NotEmpty(h.Fired);
        foreach (var (tick, _) in h.Fired)
            Assert.True(tick % Period == 0, $"pulse at tick {tick} is not a multiple of {Period} — cadence drifted");
    }

    /// <summary>
    /// ⛔ <b>The scaled clock, and its deliberate 10× consequence.</b> `decisions.md` (*Battle engine
    /// open questions (2026-09-04)*, item 4) makes the injector kernel clock follow
    /// <c>Time.timeScale</c>: it stops on pause and <b>accelerates on fast-forward</b>, up to the 10×
    /// <c>CheatActions.cs:28</c> allows.
    ///
    /// <para>This test exists to stop a future reader "fixing" that. <c>event-pipeline-v2-ssot.md</c>
    /// records that unscaled was originally chosen precisely so game speed could not multiply DoTs, so
    /// a 10× DoT rate looks exactly like a bug to anyone who reads that document and not this one. The
    /// owner was shown the consequence and confirmed it. <b>If this test fails, the decision was
    /// reverted — go and change the decision, do not change the number.</b></para>
    ///
    /// <para>The scaling itself happens in <c>InjectorLoop</c> (<c>unscaledDeltaTime × Time.timeScale</c>),
    /// which CI never builds; what is provable here is the property that matters — the same wall-clock
    /// frames buy proportionally more simulated upkeep — so the multiply is modelled at the call site
    /// exactly as the loop performs it.</para>
    /// </summary>
    [Theory]
    [InlineData(0.0f, 0)]      // paused: the clock does not advance at all
    [InlineData(0.5f, 5)]      // slow motion: half the pulses over the same real second
    [InlineData(1.0f, 10)]     // normal: the ten pulses the 100 ms grids produced
    [InlineData(10.0f, 100)]   // CheatActions' maximum — chosen, not overlooked
    public void The_kernel_clock_follows_time_scale(float timeScale, int expectedPulsesPerKind)
    {
        var h = new Harness();
        for (var i = 0; i < 60; i++)
        {
            // Exactly InjectorLoop's arithmetic: scale, then round to whole microseconds.
            var micros = (long)Math.Round(16_667.0 * timeScale);
            h.Drive.Tick(micros, budgetTicks: long.MaxValue);
        }

        Assert.Equal(expectedPulsesPerKind, h.Count(KindDot));
        Assert.Equal(expectedPulsesPerKind, h.Count(KindShield));
    }

    /// <summary>
    /// A pause must <b>hold</b> simulated time, not merely skip a frame: the clock reads the same
    /// before and after, and the pulse that was one frame away is still one frame away when play
    /// resumes. Without this, "stops on pause" could be satisfied by a clock that quietly jumped.
    /// </summary>
    [Fact]
    public void Pausing_holds_the_clock_and_resumes_where_it_stopped()
    {
        var h = new Harness();
        for (var i = 0; i < 5; i++) h.Drive.Tick(16_667, budgetTicks: long.MaxValue);
        var atPause = h.Clock.Now;
        var firedAtPause = h.Fired.Count;

        for (var i = 0; i < 600; i++) h.Drive.Tick(0, budgetTicks: long.MaxValue);   // ten paused seconds

        Assert.Equal(atPause, h.Clock.Now);
        Assert.Equal(firedAtPause, h.Fired.Count);

        for (var i = 0; i < 55; i++) h.Drive.Tick(16_667, budgetTicks: long.MaxValue);
        Assert.Equal(1000, h.Clock.Now);            // the same 60 playing frames as the 1x case
        Assert.Equal(10, h.Count(KindDot));
    }

    /// <summary>
    /// The carry, at the granularity that matters: 100 ms of upkeep must arrive after 100 ms of real
    /// time however the frames are chopped up. Irregular frames that never align to the period are
    /// the case an accumulator that zeroes its overshoot gets wrong — the real defect found in
    /// <c>TickDots</c> (`_dotAccum = 0`) while its shield sibling subtracted.
    /// </summary>
    [Fact]
    public void Irregular_frames_do_not_lose_or_gain_pulses()
    {
        var h = new Harness();
        long offered = 0;
        for (var i = 0; i < 1_000; i++)
        {
            var micros = 7_000 + i % 23;            // never a divisor of 100 ms
            offered += micros;
            h.Drive.Tick(micros, budgetTicks: long.MaxValue);
        }

        var expected = (int)(offered / 1000 / Period);
        Assert.Equal(expected, h.Count(KindDot));
        Assert.Equal(expected, h.Count(KindShield));
    }
}
