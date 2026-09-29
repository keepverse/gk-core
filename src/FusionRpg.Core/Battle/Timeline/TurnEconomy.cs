namespace FusionRpg.Core.Battle.Timeline;

/// <summary>Whether an economy's budget belongs to one actor or a whole side.</summary>
public enum TurnEconomyScope
{
    PerActor,
    PerSide
}

/// <summary>
/// What an action's resolution did, as far as the economy cares. Deliberately narrow — the kernel
/// has no notion of "weakness" or "miss" itself; the caller (combat resolution, outside this
/// module) classifies its own outcome into one of these three before calling
/// <see cref="ITurnEconomy.OnActionResolved"/>.
/// </summary>
public enum ActionResolutionOutcome
{
    Normal,
    /// <summary>Hit an elemental/type weakness — SMT press-turn refunds an icon for this.</summary>
    HitWeakness,
    /// <summary>The action missed outright — press-turn costs an extra icon for this.</summary>
    Missed
}

/// <summary>
/// B10 / T3b — the turn economy as a pluggable strategy (spec-turn-fsm.md; the gameplay choice of
/// which mode's turn budget applies must not become an architectural one). <c>scheduleKey</c> is an
/// actor key for a <see cref="TurnEconomyScope.PerActor"/> economy, or a side key (e.g.
/// <c>"side:left"</c>) for a <see cref="TurnEconomyScope.PerSide"/> one — the same trick
/// <see cref="CooldownSlot"/> and B10's own design note already use: a distinct string namespace,
/// not a new <c>ISchedulable</c> union type the spec sketches but nothing here actually needs.
///
/// <b>Purity boundary:</b> readiness never reads a budget (spec's own stated rule — "if a budget
/// ever has to be consulted to compute an arrival time, the abstraction has failed"). Budget is
/// consumed at slot acquisition, which is why <see cref="ITurnEconomy"/> has no dependency on
/// <see cref="ReadinessDriver"/> or <see cref="TurnReadiness"/> at all, in either direction.
/// </summary>
public interface ITurnEconomy
{
    TurnEconomyScope Scope { get; }

    /// <summary>Attempts to spend <paramref name="cost"/> units of budget. False when insufficient
    /// — the caller stays wherever it was and contends again, the same shape
    /// <see cref="ActionSlots.TryAcquire"/> already uses for slots.</summary>
    bool TryAcquire(string scheduleKey, long cost, long nowTick);

    /// <summary>Adjusts budget for what an already-committed action's resolution did. A no-op
    /// economy (<see cref="OneActionPerTurnEconomy"/>, <see cref="ActionPointsEconomy"/>) ignores
    /// this entirely — refund/penalty behavior is a press-turn-specific mechanic, not a universal
    /// one, which is exactly why this is a method every economy implements rather than a rule the
    /// kernel enforces centrally.</summary>
    void OnActionResolved(string scheduleKey, ActionResolutionOutcome outcome);

    /// <summary>Refills the budget for a new turn/round boundary. Meaning is economy-specific: back
    /// to 1 for <see cref="OneActionPerTurnEconomy"/>, back to its max for
    /// <see cref="ActionPointsEconomy"/>, back to the starting icon count for
    /// <see cref="PressTurnEconomy"/> — the caller decides when a boundary happens.</summary>
    void ResetForNewTurn(string scheduleKey, long nowTick);
}

/// <summary>Exactly one action, spent once, per <c>ResetForNewTurn</c>. The simplest economy —
/// every classic-round battle in this game today.</summary>
public sealed class OneActionPerTurnEconomy : ITurnEconomy
{
    readonly HashSet<string> _spent = new(StringComparer.Ordinal);

    public TurnEconomyScope Scope => TurnEconomyScope.PerActor;

    /// <summary><c>cost</c> is accepted for interface uniformity and ignored — this economy has
    /// exactly one indivisible action, never a partial one.</summary>
    public bool TryAcquire(string scheduleKey, long cost, long nowTick) => _spent.Add(scheduleKey);

    public void OnActionResolved(string scheduleKey, ActionResolutionOutcome outcome) { }

    public void ResetForNewTurn(string scheduleKey, long nowTick) => _spent.Remove(scheduleKey);
}

/// <summary>A per-actor pool of points, spent per action and refilled to its max at each reset.</summary>
public sealed class ActionPointsEconomy : ITurnEconomy
{
    readonly Dictionary<string, long> _points = new(StringComparer.Ordinal);
    readonly long _maxPoints;

    public ActionPointsEconomy(long maxPoints)
    {
        if (maxPoints <= 0) throw new ArgumentOutOfRangeException(nameof(maxPoints), "A zero-point economy could never act.");
        _maxPoints = maxPoints;
    }

    public TurnEconomyScope Scope => TurnEconomyScope.PerActor;

    public bool TryAcquire(string scheduleKey, long cost, long nowTick)
    {
        if (cost < 0) throw new ArgumentOutOfRangeException(nameof(cost));
        var current = _points.TryGetValue(scheduleKey, out var p) ? p : _maxPoints;
        if (current < cost) { _points[scheduleKey] = current; return false; }
        _points[scheduleKey] = current - cost;
        return true;
    }

    public void OnActionResolved(string scheduleKey, ActionResolutionOutcome outcome) { }

    public void ResetForNewTurn(string scheduleKey, long nowTick) => _points[scheduleKey] = _maxPoints;
}

/// <summary>
/// SMT-style press-turn: a shared, side-scoped pool of icons. A normal hit spends one icon
/// (already paid by <see cref="TryAcquire"/>); hitting a weakness refunds one, and missing costs an
/// extra one on top of what was already spent — <b>this is the implementation the interface exists
/// to prove</b>, per the todo's own acceptance bar, since a per-actor-shaped interface would have
/// broken trying to express a side-shared budget mutated by resolution outcome.
/// </summary>
public sealed class PressTurnEconomy : ITurnEconomy
{
    readonly Dictionary<string, long> _icons = new(StringComparer.Ordinal);
    readonly long _startingIcons;

    public PressTurnEconomy(long startingIcons)
    {
        if (startingIcons <= 0) throw new ArgumentOutOfRangeException(nameof(startingIcons), "A side with zero icons could never act — this is a hang, not a balance choice.");
        _startingIcons = startingIcons;
    }

    public TurnEconomyScope Scope => TurnEconomyScope.PerSide;

    public bool TryAcquire(string scheduleKey, long cost, long nowTick)
    {
        if (cost < 0) throw new ArgumentOutOfRangeException(nameof(cost));
        var current = _icons.TryGetValue(scheduleKey, out var p) ? p : _startingIcons;
        if (current < cost) { _icons[scheduleKey] = current; return false; }
        _icons[scheduleKey] = current - cost;
        return true;
    }

    public void OnActionResolved(string scheduleKey, ActionResolutionOutcome outcome)
    {
        var current = _icons.TryGetValue(scheduleKey, out var p) ? p : _startingIcons;
        _icons[scheduleKey] = outcome switch
        {
            ActionResolutionOutcome.HitWeakness => current + 1,
            ActionResolutionOutcome.Missed => Math.Max(0, current - 1),
            _ => current
        };
    }

    public void ResetForNewTurn(string scheduleKey, long nowTick) => _icons[scheduleKey] = _startingIcons;
}
