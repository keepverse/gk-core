namespace FusionRpg.Core.Battle.Timeline;

/// <summary>
/// What a scheduled event means. The queue stores <c>Kind</c> as an opaque int so it can be tested
/// with no game attached; this is where the FSM assigns it meaning.
/// </summary>
public enum TimelineEventKind
{
    /// <summary>An actor finished accruing readiness and is eligible to act (T3).</summary>
    Readiness = 0,
    /// <summary>One hit of a committed action applies. <c>Tag</c> is the index into ResolveOffsets.</summary>
    Resolve = 1,
    /// <summary>Post-action lockout elapsed.</summary>
    Recovery = 2,
    /// <summary>An actor with no legal intent re-contends after the profile's pass quantum.</summary>
    Pass = 3,
    /// <summary>B7 — the single shared hit of a rendezvous (link-strike). <c>OwnerKey</c> is the
    /// reservation id, not an actor key — <see cref="RendezvousLane.OnLinkedResolveDue"/> resolves
    /// it to every linked participant.</summary>
    LinkedResolve = 4,
    /// <summary>B7 — a rendezvous's bounded dwell elapsed before every participant joined.
    /// <c>OwnerKey</c> is the reservation id.</summary>
    RendezvousTimeout = 5
}

/// <summary>Why a commit did not happen. Refusals are values, not exceptions — every one is expected.</summary>
public enum CommitRefusal
{
    None,
    /// <summary>The intent source had nothing legal. The caller passes and reschedules.</summary>
    NoIntent,
    /// <summary>The actor is not in <see cref="TurnState.Ready"/>.</summary>
    NotReady,
    /// <summary>The concurrency width is exhausted. The actor stays Ready and contends again.</summary>
    NoSlot,
    OnCooldown
}

/// <summary>What one hit did.</summary>
public enum ActionOutcome
{
    Resolved,
    /// <summary>Early-bound onto a target that is no longer active — see <see cref="Commitment"/>.</summary>
    Fizzled
}

/// <summary>What is trying to break a committed action.</summary>
public enum InterruptCause
{
    CrowdControl,
    Damage,
    /// <summary>A `perTick` cost (spec-action-costs.md §3) could not be paid — a mechanical fact
    /// about the actor's own resources, never an inventory/content concept reaching this enum.</summary>
    ResourceExhausted
}

/// <summary>
/// Outcome of an interrupt attempt. <see cref="RefundMilli"/> is <b>reported, not applied</b> —
/// readiness belongs to T3, and returning the number keeps that seam visible instead of leaving an
/// envelope field silently unread.
/// </summary>
public readonly record struct InterruptResult(bool Broken, int RefundMilli)
{
    public static readonly InterruptResult Refused = new(false, 0);
}

/// <summary>
/// Drives one action through its envelope: commit → wind-up → resolve(s) → recovery, plus the two
/// ways it can end early (fizzle and interrupt).
///
/// <para><b>Why the resolve is a published handle.</b> An implicit wind-up timer cannot be
/// cancelled, so an interrupted swing still lands, and a combo whose target dies on hit one still
/// swings twice more at a corpse. Scheduling each hit as a real queue entry and keeping its handle
/// makes "stop this action" a cancel rather than a flag checked later — and it is sequenced before
/// the T5 gate precisely because it changes <i>when</i> a resolve is scheduled.</para>
///
/// <para>What this class deliberately does not know: what an action <i>does</i>. Damage, targeting
/// shapes, and effects belong to the combat action program. The runner owns timing, slots, and
/// cooldowns, and hands the caller an outcome to act on.</para>
///
/// <para>Allocation: one <see cref="ActionRun"/> per actor, created on that actor's first commit
/// and reused forever after, with its handle buffer grown to the widest combo it has seen. Steady
/// state is zero — asserted in the kernel allocation suite.</para>
/// </summary>
public sealed class ActionRunner
{
    /// <summary>Per-actor scratch. Mutable and reused — a record per commit is a heap object per turn.</summary>
    sealed class ActionRun
    {
        public ActionEnvelope Envelope = ActionEnvelope.NoOp;
        public string? TargetKey;
        public string SideId = "";
        public EventHandle[] Resolves = Array.Empty<EventHandle>();
        public int ResolveCount;
        public int ResolvesFired;
        public EventHandle Recovery;
        public bool HasRecovery;
        public bool TookSlot;
        public bool Active;

        /// <summary>`battle-tempo` `commitment-binding` (D6): the EFFECTIVE commitment for this run —
        /// <see cref="Envelope"/>'s own value if it declared one, else the profile default resolved
        /// once at commit time. Read here rather than re-resolved per hit, matching every other
        /// per-run field's own "resolved once at commit" shape.</summary>
        public Commitment Commitment;
    }

    readonly EventQueue _queue;
    readonly ActionSlots _slots;
    readonly CooldownLedger _cooldowns;
    readonly Func<string, bool> _isActive;
    readonly Commitment _defaultCommitment;
    readonly Func<string, string?, string?>? _reselectTarget;
    readonly Dictionary<string, ActionRun> _runs;

    /// <param name="isActive">
    /// Whether a target is still a legal recipient. Supplied by the caller because the runner has no
    /// view of the board — and taken once at construction rather than per call, so the hot path
    /// carries no per-commit delegate.
    /// </param>
    /// <param name="defaultCommitment">
    /// `battle-tempo` `commitment-binding` (D6): the active profile's own
    /// `BattleModeProfile.DefaultCommitment`, read once at construction — one `ActionRunner` serves
    /// one battle, so one profile. An envelope's own <see cref="ActionEnvelope.Commitment"/>, when
    /// non-null, always wins (envelope first, profile default second). Defaults to
    /// <see cref="Commitment.LateBound"/> — the value <see cref="ActionEnvelope.Commitment"/> itself
    /// defaulted to before it became nullable — so every existing caller that does not pass this is
    /// byte-identical.
    /// </param>
    /// <param name="reselectTarget">
    /// `battle-tempo` `commitment-binding` (D6/D11): re-selects a target using the ORIGINAL
    /// `ActionTargetSpec` the intent used — the runner owns no targeting logic of its own by design
    /// (see this class's own header: "what an action does... targeting shapes... belong to the
    /// combat action program"), so re-selection is a caller-supplied delegate, the same shape
    /// <paramref name="isActive"/> already uses for board access. Takes the acting actor's key and
    /// the now-terminal target's key; returns a new target key, or null when none is legal (the
    /// action then fizzles, same outward shape as <see cref="Commitment.EarlyBound"/>). Null (the
    /// default) means no re-selection support — <see cref="Commitment.LateBound"/> and
    /// <see cref="Commitment.EarlyBoundWithFallback"/> then fizzle too rather than throwing, so an
    /// existing caller that never passes this sees no behavioural change: nothing died mid-action
    /// under zero wind-up either way.
    /// </param>
    public ActionRunner(
        EventQueue queue,
        ActionSlots slots,
        CooldownLedger cooldowns,
        Func<string, bool> isActive,
        Commitment defaultCommitment = Commitment.LateBound,
        Func<string, string?, string?>? reselectTarget = null,
        int expectedActors = 16)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _slots = slots ?? throw new ArgumentNullException(nameof(slots));
        _cooldowns = cooldowns ?? throw new ArgumentNullException(nameof(cooldowns));
        _isActive = isActive ?? throw new ArgumentNullException(nameof(isActive));
        _defaultCommitment = defaultCommitment;
        _reselectTarget = reselectTarget;
        _runs = new Dictionary<string, ActionRun>(expectedActors, StringComparer.Ordinal);
    }

    /// <summary>True while the actor has a committed action that has not finished resolving.</summary>
    public bool IsMidAction(string actorKey) => _runs.TryGetValue(actorKey, out var run) && run.Active;

    /// <summary>Hits scheduled but not yet fired. Zero when the actor is not mid-action.</summary>
    public int PendingResolves(string actorKey) =>
        _runs.TryGetValue(actorKey, out var run) && run.Active ? run.ResolveCount - run.ResolvesFired : 0;

    /// <summary>
    /// `battle-tempo` `timeline-dispatch` (D14): the target this actor's in-flight action currently
    /// resolves against — reflects any re-selection <see cref="OnResolveDue"/> performed via
    /// <see cref="TryReselect"/>, which mutates <see cref="ActionRun.TargetKey"/> in place. A caller
    /// that applies the actual hit after <see cref="OnResolveDue"/> returns <see
    /// cref="ActionOutcome.Resolved"/> needs this to know who to hit — the runner itself never applies
    /// damage (this class's own header: "what an action does... belongs to the combat action
    /// program"). Null when the actor holds no active run, which callers that only ever check
    /// mid-action actors (guarded by <see cref="IsMidAction"/>) will not observe.
    /// </summary>
    public string? CurrentTarget(string actorKey) =>
        _runs.TryGetValue(actorKey, out var run) && run.Active ? run.TargetKey : null;

    /// <summary>
    /// A18f (spec-action-dispatch-generalization.md T55.1): the envelope this actor actually
    /// committed, for the caller applying the resolved hit — the runner itself never applies damage
    /// (this class's own header: "what an action does... belongs to the combat action program").
    /// Mirrors <see cref="CurrentTarget"/> exactly: null under the identical conditions.
    /// </summary>
    public ActionEnvelope? CurrentEnvelope(string actorKey) =>
        _runs.TryGetValue(actorKey, out var run) && run.Active ? run.Envelope : null;

    /// <summary>
    /// Commits an intent: takes a slot if the action needs one, publishes a resolve handle per hit,
    /// and moves the actor into <see cref="TurnState.Committed"/>.
    /// </summary>
    public CommitRefusal TryCommit(ActorTurnMachine actor, string sideId, in ActionIntent intent, long nowTick)
    {
        if (actor == null) throw new ArgumentNullException(nameof(actor));
        if (intent.IsNone) return CommitRefusal.NoIntent;
        if (actor.State != TurnState.Ready) return CommitRefusal.NotReady;

        var envelope = intent.Envelope;

        // Validated before anything is acquired. A throw after TryAcquire would leak the slot, and a
        // leaked slot deadlocks a W = 1 battle outright — a much worse failure than the bad content.
        ValidateOffsets(envelope);

        if (!_cooldowns.IsReady(actor.ActorKey, envelope, nowTick)) return CommitRefusal.OnCooldown;

        var tookSlot = false;
        if (envelope.SlotConsuming)
        {
            if (!_slots.TryAcquire(actor.ActorKey, sideId)) return CommitRefusal.NoSlot;
            tookSlot = true;
        }

        var run = RunFor(actor.ActorKey);
        run.Envelope = envelope;
        run.TargetKey = intent.TargetKey;
        run.SideId = sideId;
        run.TookSlot = tookSlot;
        run.ResolvesFired = 0;
        run.HasRecovery = false;
        run.Active = true;
        // D6: envelope first, profile default second -- resolved ONCE here, not re-read per hit.
        run.Commitment = envelope.Commitment ?? _defaultCommitment;

        var offsets = envelope.ResolveOffsets;
        run.ResolveCount = offsets.Count;
        if (run.Resolves.Length < offsets.Count) run.Resolves = new EventHandle[offsets.Count];

        var windupEnd = nowTick + envelope.WindupTicks;
        for (var i = 0; i < offsets.Count; i++)
            run.Resolves[i] = _queue.Schedule(
                windupEnd + offsets[i], actor.ActorKey, (int)TimelineEventKind.Resolve, i);

        actor.TransitionTo(TurnState.Committed);
        if (envelope.StartsAt == CooldownStart.Commit) _cooldowns.Start(actor.ActorKey, envelope, nowTick);
        return CommitRefusal.None;
    }

    /// <summary>
    /// Applies one hit. The caller invokes this when a <see cref="TimelineEventKind.Resolve"/> event
    /// drains, and acts on the outcome — the runner decides <i>whether</i> the hit happens, never
    /// what it does.
    /// </summary>
    public ActionOutcome OnResolveDue(ActorTurnMachine actor, in ScheduledEvent scheduled)
    {
        if (actor == null) throw new ArgumentNullException(nameof(actor));
        if (!_runs.TryGetValue(actor.ActorKey, out var run) || !run.Active)
            throw new InvalidOperationException($"Actor '{actor.ActorKey}': resolve fired with no committed action.");

        // First hit crosses into Resolving; later hits of the same combo are already there. The
        // actor stays in one Resolving span for the whole action, which is what makes "Resolving is
        // atomic with respect to the clock" a statement about the action rather than about one hit.
        if (actor.State == TurnState.Committed) actor.TransitionTo(TurnState.Resolving);

        run.ResolvesFired++;

        // battle-tempo commitment-binding (D6): checked per hit, not once, because a combo's target
        // can die between its own hits — and a lost target must be handled again on the very next
        // hit, the same as on the first.
        if (!IsTargetActive(run))
        {
            if (run.Commitment == Commitment.EarlyBound || !TryReselect(actor.ActorKey, run))
            {
                // EarlyBound: locked at commit, no re-read, fizzles outright (unchanged from before
                // this module). LateBound/EarlyBoundWithFallback ALSO fizzle here only when
                // re-selection itself found nothing legal (no reselectTarget delegate configured, or
                // the delegate found no live target) — never a silent no-op.
                CancelOutstandingResolves(run);
                FinishResolution(actor, run, scheduled.DueTick);
                return ActionOutcome.Fizzled;
            }
            // TryReselect updated run.TargetKey in place; fall through and resolve against it.
        }

        if (run.ResolvesFired >= run.ResolveCount) FinishResolution(actor, run, scheduled.DueTick);
        return ActionOutcome.Resolved;
    }

    /// <summary>Ends the post-action lockout and returns the actor to <see cref="TurnState.Charging"/>.</summary>
    public void OnRecoveryDue(ActorTurnMachine actor, in ScheduledEvent scheduled)
    {
        if (actor == null) throw new ArgumentNullException(nameof(actor));
        if (!_runs.TryGetValue(actor.ActorKey, out var run))
            throw new InvalidOperationException($"Actor '{actor.ActorKey}': recovery fired with no action.");

        run.HasRecovery = false;
        run.Active = false;
        if (run.Envelope.StartsAt == CooldownStart.RecoveryEnd)
            _cooldowns.Start(actor.ActorKey, run.Envelope, scheduled.DueTick);

        // Downed, dead, or withdrawn mid-recovery: the lockout still expires, but it must not drag
        // a terminal actor back into the cycle.
        if (actor.State == TurnState.Recovering) actor.TransitionTo(TurnState.Charging);
    }

    /// <summary>
    /// Breaks a committed action before it resolves, cancelling every outstanding hit. Refused when
    /// the envelope does not yield to this cause, or once resolution has begun.
    /// </summary>
    public InterruptResult Interrupt(ActorTurnMachine actor, long nowTick, InterruptCause cause)
    {
        if (actor == null) throw new ArgumentNullException(nameof(actor));
        if (!_runs.TryGetValue(actor.ActorKey, out var run) || !run.Active) return InterruptResult.Refused;
        if (actor.State != TurnState.Committed) return InterruptResult.Refused;
        if (!YieldsTo(run.Envelope.Interruptible, cause)) return InterruptResult.Refused;

        CancelOutstandingResolves(run);
        ReleaseSlot(run, actor.ActorKey);
        run.Active = false;
        actor.TransitionTo(TurnState.Charging);

        // No recovery is scheduled: an action broken before it landed never reaches RecoveryEnd.
        // The cooldown IS charged, though — action-todo.md T12, replacing the previous behaviour of
        // starting none at all. InterruptCooldownMilli scales it; 1000 (the default) means full,
        // and it is inert for any envelope with CooldownTicks at zero.
        if (run.Envelope.CooldownTicks > 0 && run.Envelope.InterruptCooldownMilli > 0)
        {
            var scaledTicks = run.Envelope.CooldownTicks * run.Envelope.InterruptCooldownMilli / 1000;
            _cooldowns.Start(actor.ActorKey, run.Envelope with { CooldownTicks = scaledTicks }, nowTick);
        }

        return new InterruptResult(true, run.Envelope.InterruptRefundMilli);
    }

    /// <summary>
    /// Abandons an actor's action without recovery — for death, withdrawal, or a battle ending
    /// mid-swing. Idempotent, because every exit path calls it and a double-release must not
    /// corrupt the slot count.
    /// </summary>
    public bool Abandon(string actorKey)
    {
        if (!_runs.TryGetValue(actorKey, out var run) || !run.Active) return false;
        CancelOutstandingResolves(run);
        if (run.HasRecovery)
        {
            _queue.Cancel(run.Recovery);
            run.HasRecovery = false;
        }

        ReleaseSlot(run, actorKey);
        run.Active = false;
        return true;
    }

    // ---- internals ----

    /// <summary>
    /// Leaves <see cref="TurnState.Resolving"/>: release the slot, start a resolve-scoped cooldown,
    /// and schedule the lockout.
    ///
    /// Recovery is scheduled <b>here</b> rather than at commit. Scheduling it up front would fix it
    /// to the last hit's tick, so a combo that fizzled on hit one would stay locked out as if it had
    /// landed all three — lengthening the punishment for the branch that already achieved nothing.
    /// Measuring from where resolution actually ended keeps the duration full and the end point
    /// honest on both paths.
    /// </summary>
    void FinishResolution(ActorTurnMachine actor, ActionRun run, long atTick)
    {
        ReleaseSlot(run, actor.ActorKey);
        if (run.Envelope.StartsAt == CooldownStart.Resolve)
            _cooldowns.Start(actor.ActorKey, run.Envelope, atTick);

        if (actor.State == TurnState.Resolving) actor.TransitionTo(TurnState.Recovering);

        run.Recovery = _queue.Schedule(
            atTick + run.Envelope.RecoveryTicks, actor.ActorKey, (int)TimelineEventKind.Recovery, 0);
        run.HasRecovery = true;
    }

    void CancelOutstandingResolves(ActionRun run)
    {
        for (var i = run.ResolvesFired; i < run.ResolveCount; i++) _queue.Cancel(run.Resolves[i]);
        run.ResolvesFired = run.ResolveCount;
    }

    void ReleaseSlot(ActionRun run, string actorKey)
    {
        if (!run.TookSlot) return;
        _slots.Release(actorKey);
        run.TookSlot = false;
    }

    bool IsTargetActive(ActionRun run) => run.TargetKey is null || _isActive(run.TargetKey);

    /// <summary>battle-tempo commitment-binding (D6/D11): `LateBound`/`EarlyBoundWithFallback` only
    /// — `EarlyBound` never reaches this call (checked by the caller). Delegates to the caller's own
    /// re-selection function (the ORIGINAL `ActionTargetSpec`, resolved engine-side per D11 — this
    /// class owns no targeting logic of its own by design). Updates <paramref name="run"/>'s target
    /// in place and returns true on success; returns false (and touches nothing) when no delegate is
    /// configured or the delegate found no legal target, so the caller's own fizzle path is the only
    /// one that ever runs — never a silent partial update.</summary>
    bool TryReselect(string actorKey, ActionRun run)
    {
        if (_reselectTarget is null) return false;
        var next = _reselectTarget(actorKey, run.TargetKey);
        if (next is null) return false;
        run.TargetKey = next;
        return true;
    }

    ActionRun RunFor(string actorKey)
    {
        if (_runs.TryGetValue(actorKey, out var run)) return run;
        run = new ActionRun();
        _runs[actorKey] = run;
        return run;
    }

    /// <summary>
    /// A stun stops a swing whatever the envelope says about damage, so <c>OnDamage</c> yields to
    /// both causes while <c>OnCC</c> yields only to crowd control. The asymmetry is deliberate:
    /// "this action can be knocked out of you" implies "this action can be stunned out of you", and
    /// the reverse does not hold.
    /// </summary>
    static bool YieldsTo(Interruptible policy, InterruptCause cause) => policy switch
    {
        Interruptible.Never => false,
        Interruptible.OnCC => cause == InterruptCause.CrowdControl,
        Interruptible.OnDamage => true,
        _ => false
    };

    /// <summary>
    /// Offsets must be non-negative and non-decreasing. Out of order, hit 2 would be scheduled
    /// before hit 1, and "cancel the remaining hits" — which walks the array from the fired index —
    /// would cancel the wrong ones.
    /// </summary>
    static void ValidateOffsets(ActionEnvelope envelope)
    {
        var offsets = envelope.ResolveOffsets;
        if (offsets == null || offsets.Count == 0)
            throw new ArgumentException(
                $"Action '{envelope.ActionId}' declares no resolve offsets; it would commit and never resolve.",
                nameof(envelope));

        var previous = long.MinValue;
        for (var i = 0; i < offsets.Count; i++)
        {
            if (offsets[i] < 0)
                throw new ArgumentException(
                    $"Action '{envelope.ActionId}' has a negative resolve offset at index {i} " +
                    $"({offsets[i]}) — a hit cannot land before its wind-up ends.", nameof(envelope));
            if (offsets[i] < previous)
                throw new ArgumentException(
                    $"Action '{envelope.ActionId}' has out-of-order resolve offsets at index {i} " +
                    $"({offsets[i]} after {previous}); hits must be declared in the order they land.",
                    nameof(envelope));
            previous = offsets[i];
        }
    }
}
