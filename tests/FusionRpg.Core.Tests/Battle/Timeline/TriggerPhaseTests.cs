using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// B8 / T2f — the post-apply trigger phase. Proven entirely with fake listeners: the real
/// immortal/soul-eater/coward mechanics belong to the trait/status layer, out of scope here.
/// </summary>
public class TriggerPhaseTests
{
    sealed class RecordingListener : ITriggerListener
    {
        readonly List<string> _log;
        readonly string _name;
        readonly bool _veto;

        public RecordingListener(List<string> log, string name, int priority, bool veto = false)
        {
            _log = log;
            _name = name;
            Priority = priority;
            _veto = veto;
        }

        public int Priority { get; }

        public TriggerDecision OnHpDelta(in HpDeltaEvent ev)
        {
            _log.Add(_name);
            return _veto ? TriggerDecision.Veto : TriggerDecision.Continue;
        }
    }

    /// <summary>The coward shape: fires exactly once per threshold crossing, tracked by the
    /// listener itself — TriggerPhase has no notion of "crossing", only "delta happened".</summary>
    sealed class ThresholdListener : ITriggerListener
    {
        readonly long _thresholdMilli;
        bool _crossed;
        public int CrossingCount;

        public ThresholdListener(long thresholdMilli) => _thresholdMilli = thresholdMilli;
        public int Priority => 0;

        public TriggerDecision OnHpDelta(in HpDeltaEvent ev)
        {
            var below = ev.HpAfter <= _thresholdMilli;
            if (below && !_crossed) CrossingCount++;
            _crossed = below;
            return TriggerDecision.Continue;
        }
    }

    static HpDeltaEvent Delta(string owner, long before, long after) =>
        new(owner, after - before, before, after, Tick: 100);

    [Fact]
    public void A_veto_listener_stops_the_pending_death()
    {
        var phase = new TriggerPhase();
        var log = new List<string>();
        phase.Register(new RecordingListener(log, "immortal", priority: 0, veto: true));

        var vetoed = phase.Fire(Delta("z1", 10, -5));

        Assert.True(vetoed);
        Assert.Equal(new[] { "immortal" }, log);
    }

    [Fact]
    public void An_on_kill_listener_observes_without_vetoing()
    {
        var phase = new TriggerPhase();
        var log = new List<string>();
        phase.Register(new RecordingListener(log, "soul-eater", priority: 0));

        var vetoed = phase.Fire(Delta("z1", 10, -5));

        Assert.False(vetoed);
        Assert.Equal(new[] { "soul-eater" }, log);
    }

    [Fact]
    public void Every_listener_runs_even_after_an_earlier_veto()
    {
        var phase = new TriggerPhase();
        var log = new List<string>();
        phase.Register(new RecordingListener(log, "immortal", priority: 0, veto: true));
        phase.Register(new RecordingListener(log, "soul-eater", priority: 1));

        var vetoed = phase.Fire(Delta("z1", 10, -5));

        Assert.True(vetoed);
        Assert.Equal(new[] { "immortal", "soul-eater" }, log); // soul-eater still got to observe
    }

    [Fact]
    public void Listeners_fire_in_priority_order()
    {
        var phase = new TriggerPhase();
        var log = new List<string>();
        // Registered out of priority order on purpose — the phase must sort, not preserve call order.
        phase.Register(new RecordingListener(log, "coward", priority: 2));
        phase.Register(new RecordingListener(log, "immortal", priority: 0));
        phase.Register(new RecordingListener(log, "soul-eater", priority: 1));

        phase.Fire(Delta("z1", 10, -5));

        Assert.Equal(new[] { "immortal", "soul-eater", "coward" }, log);
    }

    [Fact]
    public void Equal_priority_listeners_fire_in_registration_order_deterministically()
    {
        // The same instability List<T>.Sort exposes elsewhere in this module (ActionSlots.SortContenders's
        // own comment) — proven here with enough entries that an unstable sort would show it.
        var phase = new TriggerPhase();
        var log = new List<string>();
        var names = new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l" };
        foreach (var n in names) phase.Register(new RecordingListener(log, n, priority: 0));

        phase.Fire(Delta("z1", 10, -5));

        Assert.Equal(names, log);
    }

    [Fact]
    public void A_threshold_listener_fires_exactly_once_per_crossing()
    {
        var phase = new TriggerPhase();
        var threshold = new ThresholdListener(thresholdMilli: 300);
        phase.Register(threshold);

        phase.Fire(Delta("z1", 1000, 500));  // above threshold — no crossing yet
        Assert.Equal(0, threshold.CrossingCount);

        phase.Fire(Delta("z1", 500, 200));   // crosses below 300
        Assert.Equal(1, threshold.CrossingCount);

        phase.Fire(Delta("z1", 200, 100));   // still below — same crossing, not a new one
        Assert.Equal(1, threshold.CrossingCount);

        phase.Fire(Delta("z1", 100, 400));   // recovers above threshold
        phase.Fire(Delta("z1", 400, 50));    // crosses below again — a SECOND crossing
        Assert.Equal(2, threshold.CrossingCount);
    }

    [Fact]
    public void Registering_a_null_listener_throws()
    {
        var phase = new TriggerPhase();
        Assert.Throws<ArgumentNullException>(() => phase.Register(null!));
    }

    [Fact]
    public void No_listeners_never_vetoes()
    {
        var phase = new TriggerPhase();
        Assert.False(phase.Fire(Delta("z1", 10, -5)));
    }
}
