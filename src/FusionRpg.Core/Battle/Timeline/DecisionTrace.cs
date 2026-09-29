using System.Text.Json;

namespace FusionRpg.Core.Battle.Timeline;

/// <summary>Where a recorded decision came from. `Timeout` is a real decision, not an absence.</summary>
public enum DecisionSource
{
    /// <summary>A human chose it inside the input window.</summary>
    Player,

    /// <summary>
    /// The input window elapsed and the default action was taken.
    ///
    /// <para>⛔ Recorded as a DECISION AT A TICK, never re-measured on replay. This is the sharpest
    /// determinism trap in the program: a replay that re-timed the window would branch differently on
    /// a slower machine, and `SimulationClock` cannot read a wall clock at all. The session layer owns
    /// the countdown; the trace owns what it decided.</para>
    /// </summary>
    Timeout
}

/// <summary>One decision, stamped at the tick it was made.</summary>
public readonly record struct TracedDecision(
    long Tick, string ActorKey, string ActionId, string? TargetKey, DecisionSource Source);

/// <summary>
/// **T10 — the decision trace.** With real input, `(setup, seed)` stops being a complete description
/// of a battle; determinism becomes `(setup, seed, trace)`.
///
/// <para><b>Appended per decision, never written at the end.</b> A trace produced only on completion
/// is worthless for the failure it exists to cover — a disconnect mid-battle would leave a row that
/// still *looks* auto-resolvable, and the boot sweep would re-resolve it with AI decisions,
/// silently overwriting a player's real result. That is the hole T6 must not ship without.</para>
///
/// <para>Ordered by <c>(Tick, Seq)</c>, the same total order the event queue uses — never by arrival
/// time, which is a wall-clock property and would not replay.</para>
/// </summary>
public sealed class DecisionTrace
{
    readonly List<TracedDecision> _decisions = new();
    int _replayCursor;

    public IReadOnlyList<TracedDecision> Decisions => _decisions;

    public int Count => _decisions.Count;

    /// <summary>True once every recorded decision has been consumed by a replay.</summary>
    public bool ReplayExhausted => _replayCursor >= _decisions.Count;

    public void Record(long tick, string actorKey, string actionId, string? targetKey, DecisionSource source)
    {
        if (string.IsNullOrEmpty(actorKey)) throw new ArgumentException("A decision needs an actor.", nameof(actorKey));
        if (string.IsNullOrEmpty(actionId)) throw new ArgumentException("A decision needs an action.", nameof(actionId));
        _decisions.Add(new TracedDecision(tick, actorKey, actionId, targetKey, source));
    }

    /// <summary>
    /// The next decision this actor made, for replay. Returns null when the trace has nothing more to
    /// say about it.
    ///
    /// <para>Matched on actor key rather than consumed strictly in order, because the kernel may seat
    /// actors in a different pass order than they were recorded in while still producing the same
    /// battle — the total order that matters is `(Tick, Seq)`, which the recorded ticks preserve.</para>
    /// </summary>
    public TracedDecision? NextFor(string actorKey)
    {
        for (var i = _replayCursor; i < _decisions.Count; i++)
        {
            if (!string.Equals(_decisions[i].ActorKey, actorKey, StringComparison.Ordinal)) continue;
            var found = _decisions[i];
            if (i == _replayCursor) _replayCursor++;
            else _decisions.RemoveAt(i);
            return found;
        }

        return null;
    }

    public string ToJson() => JsonSerializer.Serialize(_decisions);

    /// <summary>
    /// Rehydrates a trace. **A null or empty document is not an empty trace** — it is the absence of
    /// one, and the caller must treat an interactive match with no trace as unreplayable rather than
    /// as "a battle in which nobody decided anything".
    /// </summary>
    public static DecisionTrace? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        var decisions = JsonSerializer.Deserialize<List<TracedDecision>>(json);
        if (decisions is null) return null;

        var trace = new DecisionTrace();
        trace._decisions.AddRange(decisions);
        return trace;
    }
}
