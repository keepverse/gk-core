namespace FusionRpg.Core.Battle.Timeline;

/// <summary>
/// B9 / T3a — the live half of readiness (spec-readiness-model.md; the pure math lives in
/// <see cref="TurnReadiness"/>, which this class is the only thing that calls). Owns what a pure
/// function structurally cannot: <c>accruedWork</c> as state that survives a mid-flight
/// <c>turn.speed</c>/<c>turn.haste</c> mutation, and driving <see cref="TimelineEventKind.Readiness"/>
/// into <see cref="TurnState.Charging"/> → <see cref="TurnState.Ready"/> — already a legal
/// transition; nothing drove it before this class existed.
///
/// <para><b>Work is stored, not time.</b> A naive "reschedule at <c>now + TicksFor(remaining time,
/// newRate)</c>" would be wrong the moment a rate changes mid-flight, because "remaining time" at
/// the OLD rate is not the same quantity at the NEW one. Every rebase first converts elapsed
/// wall-of-the-clock ticks into work already done at the rate that was active for that span
/// (<c>elapsed × oldRate / SpeedScale</c>), subtracts it from the stored remainder, and only then
/// computes the new arrival from the fresh rate — reproducing the spec's own locked example
/// exactly: speed 100, haste 1000→500 half-way through a 1000-tick wait arrives at <c>t+750</c>,
/// not <c>t+1000</c>.</para>
///
/// <para><b>Mechanism only.</b> This class does not decide what an actor's rate <i>is</i> — the
/// caller resolves <c>turn.speed</c>/<c>turn.haste</c> and calls <see cref="TurnReadiness.EffectiveRate"/>
/// itself, the same boundary <see cref="ActionRunner"/> keeps around what an action <i>does</i>.</para>
/// </summary>
public sealed class ReadinessDriver
{
    sealed class Track
    {
        public long RemainingWork;
        public long Rate;
        public long RebasedAtTick;
        public EventHandle Handle;
        public bool Active;
    }

    readonly EventQueue _queue;
    readonly Dictionary<string, Track> _tracks = new(StringComparer.Ordinal);

    public ReadinessDriver(EventQueue queue) => _queue = queue ?? throw new ArgumentNullException(nameof(queue));

    /// <summary>
    /// Starts (or restarts) an actor's Charging phase: <paramref name="work"/> at
    /// <paramref name="rate"/>, scheduled as a <see cref="TimelineEventKind.Readiness"/> event.
    /// <paramref name="rate"/> must already be clamped by the caller — <see cref="TurnReadiness"/>'s
    /// own stated precondition, enforced by the throw inside it, not re-validated here.
    /// </summary>
    public void BeginCharging(string actorKey, long work, long rate, long nowTick)
    {
        if (string.IsNullOrWhiteSpace(actorKey)) throw new ArgumentException("actorKey is required", nameof(actorKey));
        var track = TrackFor(actorKey);
        if (track.Active) _queue.Cancel(track.Handle); // restarting mid-charge must not leak the old event

        track.RemainingWork = work;
        track.Rate = rate;
        track.RebasedAtTick = nowTick;
        track.Active = true;
        track.Handle = _queue.Schedule(
            TurnReadiness.NextReadyTick(nowTick, work, rate), actorKey, (int)TimelineEventKind.Readiness, 0);
    }

    /// <summary>
    /// Rebases a mid-flight actor's pending readiness event onto <paramref name="newRate"/> — the
    /// live response to a <c>turn.speed</c>/<c>turn.haste</c> mutation. A no-op for an actor not
    /// currently charging: mutating the stat while <c>Ready</c>/<c>Committed</c>/etc. changes
    /// nothing about a turn that has already arrived or is already committed, only the next one
    /// (which reads the current rate again at its own <see cref="BeginCharging"/>).
    /// </summary>
    public void OnRateChanged(string actorKey, long newRate, long nowTick)
    {
        if (!_tracks.TryGetValue(actorKey, out var track) || !track.Active) return;

        var elapsed = nowTick - track.RebasedAtTick;
        var workDone = elapsed * track.Rate / TurnReadiness.SpeedScale;
        track.RemainingWork = Math.Max(0, track.RemainingWork - workDone);
        track.Rate = newRate;
        track.RebasedAtTick = nowTick;

        _queue.Reschedule(track.Handle, TurnReadiness.NextReadyTick(nowTick, track.RemainingWork, newRate));
    }

    /// <summary>Fires when the actor's Readiness event drains: the turn arrived.</summary>
    public void OnReadinessDue(ActorTurnMachine actor)
    {
        if (actor == null) throw new ArgumentNullException(nameof(actor));
        if (_tracks.TryGetValue(actor.ActorKey, out var track)) track.Active = false;
        if (actor.State == TurnState.Charging) actor.TransitionTo(TurnState.Ready);
    }

    Track TrackFor(string actorKey)
    {
        if (_tracks.TryGetValue(actorKey, out var t)) return t;
        t = new Track();
        _tracks[actorKey] = t;
        return t;
    }
}
