namespace FusionRpg.Core.Battle.Timeline;

/// <summary>Thrown when a transition outside <see cref="TurnTransitions"/> is attempted.</summary>
public sealed class IllegalTurnTransitionException : InvalidOperationException
{
    public IllegalTurnTransitionException(string actorKey, TurnState from, TurnState to)
        : base($"Actor '{actorKey}': illegal turn transition {from} -> {to}.")
    {
        ActorKey = actorKey;
        From = from;
        To = to;
    }

    public string ActorKey { get; }
    public TurnState From { get; }
    public TurnState To { get; }
}

/// <summary>
/// One actor's position in the turn cycle. Loud over silent: an illegal transition throws with
/// both states named, matching the engine's existing "loud validation over silent corruption"
/// stance — this is the module where a bad transition would be least visible.
/// </summary>
public sealed class ActorTurnMachine
{
    public ActorTurnMachine(string actorKey)
    {
        if (string.IsNullOrWhiteSpace(actorKey)) throw new ArgumentException("actorKey is required", nameof(actorKey));
        ActorKey = actorKey;
    }

    public string ActorKey { get; }
    public TurnState State { get; private set; } = TurnState.Charging;

    /// <summary>
    /// Still on the battlefield: targetable, and revivable if downed. False once the actor is
    /// dead or has withdrawn.
    /// </summary>
    public bool IsPresent => !TurnTransitions.IsTerminal(State);

    /// <summary>
    /// Able to take a turn. Distinct from <see cref="IsPresent"/> in exactly one state: a downed
    /// actor is present — it can be healed, revived, or hit — but it cannot act. That distinction
    /// is what the seat check needs, and collapsing the two into one predicate (as an earlier
    /// draft did, with two identically-bodied properties) loses it.
    /// </summary>
    public bool CanAct => State is TurnState.Charging or TurnState.Ready
        or TurnState.Committed or TurnState.Resolving or TurnState.Recovering;

    /// <summary>
    /// Between commit and the end of resolution — the span during which a slot-consuming action
    /// occupies its slot.
    ///
    /// It is deliberately <i>not</i> called <c>HoldsSlot</c>, which is what it was until the
    /// envelope gained <see cref="ActionEnvelope.SlotConsuming"/>. Movement and periodic pulses are
    /// mid-action while holding no slot at all, so a state-derived slot claim would have been a
    /// second, wrong answer to a question <see cref="ActionSlots.Holds"/> already answers exactly.
    /// </summary>
    public bool IsMidAction => State is TurnState.Committed or TurnState.Resolving;

    public void TransitionTo(TurnState next)
    {
        if (!TurnTransitions.IsLegal(State, next))
            throw new IllegalTurnTransitionException(ActorKey, State, next);
        State = next;
    }

    /// <summary>Transitions only if legal; returns whether it moved. For paths that race.</summary>
    public bool TryTransitionTo(TurnState next)
    {
        if (!TurnTransitions.IsLegal(State, next)) return false;
        State = next;
        return true;
    }
}
