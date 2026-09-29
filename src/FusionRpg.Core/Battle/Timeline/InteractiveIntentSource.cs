namespace FusionRpg.Core.Battle.Timeline;

/// <summary>
/// What a player chose, or nothing if the window has not produced a choice yet. Returned by the
/// session layer, which owns the countdown — this module never measures time.
/// </summary>
public readonly record struct PlayerChoice(string ActionId, string? TargetKey)
{
    public bool IsNone => string.IsNullOrEmpty(ActionId);
    public static readonly PlayerChoice None = default;
}

/// <summary>
/// **T6 — the interactive intent source.** A human occupying the `Ready` dwell an actor already has.
///
/// <para><b>It adds an implementation, not an interface.</b> `IIntentSource` is already documented as
/// "the AI-policy seam the auto-resolved modes need, <b>and the player-input seam an interactive mode
/// needs</b>". The kernel's `Ready → Committed` gating, slot contention and `Passed` outcome are all
/// built; an interactive turn is a slower `TryDeclare`, not a new state machine.</para>
///
/// <para><b>Every declaration is recorded, including a timeout.</b> A timeout is a decision at a tick,
/// not a duration to re-measure — see <see cref="DecisionSource.Timeout"/>. That is what makes
/// `(setup, seed, trace)` a complete description of an interactive battle.</para>
///
/// <para><b>Replay reads the trace instead of asking.</b> Constructed with an existing trace, this
/// never consults the player at all: it replays the recorded decision for each actor, so a completed
/// trace reproduces its battle byte-identically and an AFK timeout replays as the same timeout rather
/// than as a fresh countdown that might resolve differently.</para>
/// </summary>
public sealed class InteractiveIntentSource : IIntentSource
{
    readonly IIntentSource _fallback;
    readonly Func<string, long, PlayerChoice>? _ask;
    readonly Func<string, ActionEnvelope?> _envelopeOf;
    readonly DecisionTrace _trace;
    readonly bool _replaying;
    readonly bool _replayThenLive;
    readonly Action<TracedDecision>? _onRecorded;
    bool _wentLive;

    /// <summary>Live: ask the player, fall back to the default action when the window elapses.
    ///
    /// <para><paramref name="onRecorded"/> (party-dungeon D2.16, spec-delve-battle-profile.md §4b) is
    /// the incremental-persistence seam: it fires once, synchronously, immediately after EVERY new
    /// decision this call makes (player or timeout) — never during replay, since replay reads decisions
    /// that already exist rather than creating new ones. The spec's own §4b already names this exact
    /// shape as `RpgStore.WriteWebMatchDecisions`'s intended caller: "called after every
    /// `DecisionTrace.Record`, with `DecisionTrace.ToJson()`". Optional and defaulted to <c>null</c> so
    /// every existing caller (including every shipped battle) is byte-identical — a battle's own report
    /// never depends on whether anyone is listening for its decisions.</para>
    /// </summary>
    public InteractiveIntentSource(
        IIntentSource fallback,
        Func<string, long, PlayerChoice> ask,
        Func<string, ActionEnvelope?> envelopeOf,
        DecisionTrace trace,
        Action<TracedDecision>? onRecorded = null)
        : this(fallback, ask ?? throw new ArgumentNullException(nameof(ask)), envelopeOf, trace,
            replaying: false, replayThenLive: false, onRecorded)
    {
    }

    /// <summary>Replay: read the trace, never the player. The battle is reproduced, not replayed live.</summary>
    public InteractiveIntentSource(
        IIntentSource fallback,
        Func<string, ActionEnvelope?> envelopeOf,
        DecisionTrace recorded)
        : this(fallback, null, envelopeOf, recorded, replaying: true, replayThenLive: false, onRecorded: null)
    {
    }

    InteractiveIntentSource(
        IIntentSource fallback,
        Func<string, long, PlayerChoice>? ask,
        Func<string, ActionEnvelope?> envelopeOf,
        DecisionTrace trace,
        bool replaying,
        bool replayThenLive,
        Action<TracedDecision>? onRecorded)
    {
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _ask = ask;
        _envelopeOf = envelopeOf ?? throw new ArgumentNullException(nameof(envelopeOf));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _replaying = replaying;
        _replayThenLive = replayThenLive;
        _onRecorded = onRecorded;
    }

    /// <summary>
    /// D2.16 (spec-delve-battle-profile.md §4b) — resume after a freeze: replay <paramref
    /// name="recorded"/>'s prefix byte for byte, then go live once it is exhausted. <paramref
    /// name="recorded"/> is both the replay source and the trace new live decisions get appended to,
    /// so what the caller reads back after this session ends (or freezes again) is the complete
    /// history, not just the newly-recorded suffix.
    ///
    /// <para><b>Why a factory and not a third <c>(fallback, ask, envelopeOf, trace)</c> constructor.</b>
    /// That signature is byte-for-byte identical to the live constructor's overload — the only
    /// difference is whether <c>trace</c> arrives pre-populated, which the type system cannot see. A
    /// named factory says so instead of colliding with it.</para>
    ///
    /// <para><b><c>_wentLive</c> is a sticky latch, never a re-check of
    /// <see cref="DecisionTrace.ReplayExhausted"/>.</b> <see cref="DecisionTrace.Record"/> appends to
    /// the very list <c>ReplayExhausted</c> counts against, so the instant a live decision is recorded,
    /// <c>_replayCursor &gt;= _decisions.Count</c> would go false again — a naive re-check on the next
    /// call would try to "replay" the decision that was just live-recorded instead of asking anew.
    /// Latching the moment exhaustion is first observed means <c>ReplayExhausted</c> is read at most
    /// once per session.</para>
    /// </summary>
    public static InteractiveIntentSource ResumeReplayThenLive(
        IIntentSource fallback,
        Func<string, long, PlayerChoice> ask,
        Func<string, ActionEnvelope?> envelopeOf,
        DecisionTrace recorded,
        Action<TracedDecision>? onRecorded = null)
        => new(fallback, ask ?? throw new ArgumentNullException(nameof(ask)), envelopeOf, recorded,
            replaying: false, replayThenLive: true, onRecorded);

    public ActionIntent TryDeclare(string actorKey, long nowTick)
    {
        if (_replaying) return Replay(actorKey);

        if (_replayThenLive && !_wentLive)
        {
            if (!_trace.ReplayExhausted) return Replay(actorKey);
            _wentLive = true;
        }

        var choice = _ask!(actorKey, nowTick);
        if (!choice.IsNone && _envelopeOf(choice.ActionId) is { } envelope)
        {
            _trace.Record(nowTick, actorKey, choice.ActionId, choice.TargetKey, DecisionSource.Player);
            _onRecorded?.Invoke(_trace.Decisions[^1]);
            return new ActionIntent(choice.ActionId, choice.TargetKey, envelope);
        }

        // The window elapsed (or named an action this actor cannot use): the default action is taken
        // and RECORDED as a timeout decision, so replay takes the same branch without re-timing it.
        var fallback = _fallback.TryDeclare(actorKey, nowTick);
        if (fallback.IsNone) return ActionIntent.None;   // genuinely nothing legal — nothing to record

        _trace.Record(nowTick, actorKey, fallback.ActionId, fallback.TargetKey, DecisionSource.Timeout);
        _onRecorded?.Invoke(_trace.Decisions[^1]);
        return fallback;
    }

    ActionIntent Replay(string actorKey)
    {
        if (_trace.NextFor(actorKey) is not { } recorded) return ActionIntent.None;
        return _envelopeOf(recorded.ActionId) is { } envelope
            ? new ActionIntent(recorded.ActionId, recorded.TargetKey, envelope)
            : ActionIntent.None;
    }
}
