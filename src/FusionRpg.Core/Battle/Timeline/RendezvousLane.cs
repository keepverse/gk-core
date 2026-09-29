namespace FusionRpg.Core.Battle.Timeline;

/// <summary>Result of opening or joining a rendezvous reservation.</summary>
public enum RendezvousOutcome
{
    /// <summary>This participant joined; at least one other is still awaited.</summary>
    Waiting,
    /// <summary>The last participant joined: every slot was acquired atomically, one shared resolve
    /// was scheduled, and every participant's <see cref="ActorTurnMachine"/> is now <see cref="TurnState.Committed"/>.</summary>
    Committed,
    /// <summary>The dwell already timed out, or the id is unknown — the caller must fall back to a
    /// solo <see cref="ActionRunner.TryCommit"/> for whichever participants already joined.</summary>
    Expired,
    /// <summary>Every participant joined in time, but at least one slot could not be acquired.
    /// Nothing is held by anyone — rolled back — and the caller falls back to solo commits.</summary>
    NoSlot
}

/// <summary>
/// B7 / T2e — multi-actor coordinated actions (link-strikes; spec-turn-fsm.md "Multi-actor
/// coordinated actions"). Two or more actors commit together and produce <b>one</b> shared
/// <see cref="TimelineEventKind.LinkedResolve"/> event rather than N independent resolves.
///
/// <para><b>Deliberately FSM-neutral</b> (design call, battle-timeline-plan.md's Phase 1 addendum,
/// 2026-08-28): a joining actor's own <see cref="ActorTurnMachine"/> stays in <see cref="TurnState.Ready"/>
/// until the whole reservation completes — there is no <c>WaitingForPartner</c> state. The spec's
/// own Boundaries section requires asking first before adding a <see cref="TurnState"/> beyond
/// <see cref="TurnState.Downed"/>; this design avoids ever needing to, the same way B6's
/// <see cref="ReactionLane"/> stayed outside the FSM rather than inventing a reacting state.
/// "Produces one <c>Resolving</c>" is satisfied at the <b>scheduling</b> level — a single shared
/// <see cref="EventHandle"/>, keyed by the reservation id rather than an actor key — not by merging
/// per-actor state machines.</para>
///
/// <para><b>Scope, kept to a single shared hit for this first pass.</b> Multi-hit linked combos
/// (`ResolveOffsets` beyond index 0) are not exercised by the todo's own acceptance criteria and
/// would triple this already-nontrivial mechanism's surface for no consumer yet — see
/// <see cref="ActionRunner"/> for the multi-hit shape a later pass can fold in if content needs it.</para>
///
/// <para><b>Partial acquire never leaves a held slot.</b> <see cref="Complete"/> is the only path
/// that ever calls <see cref="ActionSlots.TryAcquire"/>, and only once every participant has
/// joined; if any participant's acquire fails, every prior success in the same call is rolled back
/// via <see cref="ActionSlots.Release"/> before returning <see cref="RendezvousOutcome.NoSlot"/>.</para>
/// </summary>
public sealed class RendezvousLane
{
    sealed class Reservation
    {
        public IReadOnlyList<string> ParticipantKeys = Array.Empty<string>();
        public readonly HashSet<string> Joined = new(StringComparer.Ordinal);
        public string SideId = "";
        public ActionEnvelope Envelope = ActionEnvelope.NoOp;
        public string? TargetKey;
        public EventHandle TimeoutHandle;
        public bool Resolved;
    }

    sealed class LinkedRun
    {
        public IReadOnlyList<string> ParticipantKeys = Array.Empty<string>();
        public ActionEnvelope Envelope = ActionEnvelope.NoOp;
    }

    readonly EventQueue _queue;
    readonly ActionSlots _slots;
    readonly CooldownLedger _cooldowns;
    readonly Func<string, ActorTurnMachine> _actorOf;
    readonly Dictionary<string, Reservation> _reservations = new(StringComparer.Ordinal);
    readonly Dictionary<string, LinkedRun> _runByReservation = new(StringComparer.Ordinal);

    /// <param name="actorOf">Resolves a participant key to its machine. Taken once, like
    /// <see cref="ActionRunner"/>'s <c>isActive</c> delegate, so the hot path carries no per-call
    /// closure.</param>
    public RendezvousLane(EventQueue queue, ActionSlots slots, CooldownLedger cooldowns, Func<string, ActorTurnMachine> actorOf)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _slots = slots ?? throw new ArgumentNullException(nameof(slots));
        _cooldowns = cooldowns ?? throw new ArgumentNullException(nameof(cooldowns));
        _actorOf = actorOf ?? throw new ArgumentNullException(nameof(actorOf));
    }

    /// <summary>
    /// Opens a reservation for exactly <paramref name="participantKeys"/> (2 or more) and starts
    /// the bounded dwell. The caller IS the first participant — there is no separate "declare then
    /// join" step for it, so this both opens and joins in one call.
    /// </summary>
    public RendezvousOutcome Open(
        string reservationId, IReadOnlyList<string> participantKeys, string firstActorKey,
        string sideId, ActionEnvelope envelope, string? targetKey, long nowTick, long timeoutTicks)
    {
        if (string.IsNullOrWhiteSpace(reservationId)) throw new ArgumentException("reservationId is required", nameof(reservationId));
        if (participantKeys == null || participantKeys.Count < 2)
            throw new ArgumentException("A rendezvous needs at least two participants.", nameof(participantKeys));
        if (timeoutTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutTicks), "An unbounded rendezvous at W=1 is a guaranteed hang — the timeout is mandatory.");
        if (_reservations.ContainsKey(reservationId))
            throw new InvalidOperationException($"Rendezvous '{reservationId}' is already open.");

        var reservation = new Reservation
        {
            ParticipantKeys = participantKeys,
            SideId = sideId,
            Envelope = envelope,
            TargetKey = targetKey
        };
        reservation.TimeoutHandle = _queue.Schedule(
            nowTick + timeoutTicks, reservationId, (int)TimelineEventKind.RendezvousTimeout, 0);
        _reservations[reservationId] = reservation;

        return TryJoin(reservationId, firstActorKey, nowTick);
    }

    /// <summary>Joins an already-open reservation. The last participant to join completes it.</summary>
    public RendezvousOutcome TryJoin(string reservationId, string actorKey, long nowTick)
    {
        if (!_reservations.TryGetValue(reservationId, out var reservation) || reservation.Resolved)
            return RendezvousOutcome.Expired;
        if (!Contains(reservation.ParticipantKeys, actorKey))
            throw new ArgumentException($"'{actorKey}' is not a participant of rendezvous '{reservationId}'.", nameof(actorKey));

        reservation.Joined.Add(actorKey);
        if (reservation.Joined.Count < reservation.ParticipantKeys.Count) return RendezvousOutcome.Waiting;

        return Complete(reservationId, reservation, nowTick);
    }

    RendezvousOutcome Complete(string reservationId, Reservation reservation, long nowTick)
    {
        reservation.Resolved = true;
        _queue.Cancel(reservation.TimeoutHandle);

        if (reservation.Envelope.SlotConsuming)
        {
            var acquired = new List<string>(reservation.ParticipantKeys.Count);
            foreach (var key in reservation.ParticipantKeys)
            {
                if (_slots.TryAcquire(key, reservation.SideId)) { acquired.Add(key); continue; }
                foreach (var held in acquired) _slots.Release(held);
                return RendezvousOutcome.NoSlot;
            }
        }

        _runByReservation[reservationId] = new LinkedRun
        {
            ParticipantKeys = reservation.ParticipantKeys,
            Envelope = reservation.Envelope
        };
        // ONE shared handle for every participant — the mechanism that makes "produces one
        // Resolving" true. OwnerKey is the reservation id, never an actor key.
        _queue.Schedule(nowTick + reservation.Envelope.WindupTicks, reservationId, (int)TimelineEventKind.LinkedResolve, 0);

        foreach (var key in reservation.ParticipantKeys)
        {
            var actor = _actorOf(key);
            if (actor.State == TurnState.Charging) actor.TransitionTo(TurnState.Ready);
            actor.TransitionTo(TurnState.Committed);
            if (reservation.Envelope.StartsAt == CooldownStart.Commit)
                _cooldowns.Start(key, reservation.Envelope, nowTick);
        }

        return RendezvousOutcome.Committed;
    }

    /// <summary>
    /// Fires when the shared <see cref="TimelineEventKind.LinkedResolve"/> event drains. Transitions
    /// every participant to <see cref="TurnState.Resolving"/> off this ONE firing, then straight to
    /// <see cref="TurnState.Recovering"/> with its own scheduled recovery — recovery and cooldown
    /// apply <b>per participant</b> even though the resolve trigger was shared (spec's explicit
    /// "economy is charged once per participant" rule).
    /// </summary>
    public void OnLinkedResolveDue(string reservationId, long atTick)
    {
        if (!_runByReservation.Remove(reservationId, out var run))
            throw new InvalidOperationException($"Linked resolve fired for unknown reservation '{reservationId}'.");

        foreach (var key in run.ParticipantKeys)
        {
            var actor = _actorOf(key);
            if (actor.State == TurnState.Committed) actor.TransitionTo(TurnState.Resolving);
            if (run.Envelope.SlotConsuming) _slots.Release(key);
            if (run.Envelope.StartsAt == CooldownStart.Resolve) _cooldowns.Start(key, run.Envelope, atTick);
            if (actor.State == TurnState.Resolving) actor.TransitionTo(TurnState.Recovering);
            _queue.Schedule(atTick + run.Envelope.RecoveryTicks, key, (int)TimelineEventKind.Recovery, 0);
        }
    }

    /// <summary>
    /// Fires when a reservation's dwell timeout drains before every participant joined. Nothing was
    /// ever acquired for a reservation that never reached <see cref="Complete"/> — that is the only
    /// path that touches <see cref="ActionSlots"/> — so there is nothing concrete to release here.
    /// Returns whichever participants HAD already joined, so the caller can fall back each of them
    /// to a solo <see cref="ActionRunner.TryCommit"/> rather than leaving them silently stuck.
    /// </summary>
    public IReadOnlyList<string> OnTimeoutDue(string reservationId)
    {
        if (!_reservations.Remove(reservationId, out var reservation) || reservation.Resolved)
            return Array.Empty<string>();

        var joined = new string[reservation.Joined.Count];
        reservation.Joined.CopyTo(joined);
        return joined;
    }

    static bool Contains(IReadOnlyList<string> keys, string key)
    {
        for (var i = 0; i < keys.Count; i++)
            if (string.Equals(keys[i], key, StringComparison.Ordinal)) return true;
        return false;
    }
}
