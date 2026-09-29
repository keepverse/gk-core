using FusionRpg.Core.Battle.Timeline;

namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `decision-inspector` (module 10, CAI2.4, spec-decision-inspector.md §1): the router-level
/// recorder. <see cref="IIntentSource"/> is a ONE-member interface, so wrapping an arm is the smallest
/// way to make every arm of <see cref="IntentRouter"/> report to the same sink — no per-policy edit, and
/// no field invented to fill a gap.
///
/// <para><b>What it can and cannot say, stated so the empty fields are read as facts.</b> The router
/// knows the actor, the tick, which arm answered (→ <see cref="AiDecisionOrigin"/>) and the intent that
/// came back, so those are the fields it fills. It does NOT know the round (`Round` is 0 — the tick→round
/// map belongs to the policy that owns the clock seam, not to the router) and it does not know the
/// per-candidate detail, so <c>Candidates</c> and <c>TopThree</c> are empty. That detail is produced where
/// it is produced — `SiegeAiIntentSource` fills both (CAI2.4's first two slices), and the core policy's
/// action stage is where the smart tier's per-action gate verdicts live. An empty list here means "this
/// arm exposed no candidate detail", never "there were no candidates".</para>
///
/// <para><b>Why an arm wrapper rather than sink parameters on each policy.</b> A per-policy sink would
/// change three public constructors and would have to be repeated for every future arm; this decorates
/// whatever the router was handed, including a test fake, so the property "every arm records" holds by
/// construction at the one construction entry point.</para>
/// </summary>
sealed class AiDecisionRecordingSource : IIntentSource
{
    readonly IIntentSource _inner;
    readonly IAiDecisionSink _sink;
    readonly AiDecisionOrigin _origin;

    public AiDecisionRecordingSource(IIntentSource inner, IAiDecisionSink sink, AiDecisionOrigin origin)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _origin = origin;
    }

    public ActionIntent TryDeclare(string actorKey, long nowTick)
    {
        var intent = _inner.TryDeclare(actorKey, nowTick);

        RecordRouterDecision(_sink, actorKey, nowTick, _origin, intent);

        return intent;
    }

    /// <summary>
    /// The ONE place a router-level record is shaped, so the three decorated source arms and the
    /// router's own ORDER arm (which returns before any source runs — `IntentRouter.Resolve`'s order
    /// step) cannot drift apart. What the router can say is exactly what it knows: the actor, the tick,
    /// which arm answered (→ <see cref="AiDecisionOrigin"/>), and the intent that came back.
    ///
    /// <para><b>A None intent still records.</b> "The chain reached nothing" is a decision the inspector
    /// has to be able to explain, and a missing record would be indistinguishable from the router never
    /// running.</para>
    /// </summary>
    public static void RecordRouterDecision(
        IAiDecisionSink? sink, string actorKey, long nowTick, AiDecisionOrigin origin, ActionIntent intent)
    {
        if (sink is null) return;   // the inspector is OFF: off must cost nothing, so nothing is built

        sink.Record(new AiDecisionRecord(
            NowTick: nowTick, Round: 0, ActorKey: actorKey,
            Tier: null, ProfileId: null, Personality: null,
            Trigger: AiTriggerState.None, Origin: origin,
            Candidates: Array.Empty<AiCandidateVerdict>(),
            ChosenActionId: intent.IsNone ? null : intent.ActionId,
            ChosenTargetKey: intent.TargetKey, ChosenSaturatedBy: 0,
            TopThree: Array.Empty<(string ActorKey, ScoreBreakdown Breakdown)>()));
    }
}
