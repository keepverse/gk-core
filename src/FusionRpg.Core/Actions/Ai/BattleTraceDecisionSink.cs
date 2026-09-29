using FusionRpg.Core.Battle.Timeline;

namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `decision-inspector` (module 10, CAI2.4, spec-decision-inspector.md §1): the turn-mode
/// sink — it formats an <see cref="AiDecisionRecord"/> onto the EXISTING
/// <see cref="BattleTrace.AiDecision"/> line and leaves <see cref="BattleTrace"/> untouched.
///
/// <para><b>Why the sink lives here and not in `Battle/Timeline/`.</b> Widening `BattleTrace`'s
/// signature to carry a tier, a personality and a candidate list would break the rule that class's own
/// comment states — it "stays domain-agnostic … takes only primitives and never a subsystem's own
/// scoring type" — and would create the `Timeline` → decision-layer dependency
/// <see cref="CandidateScorer.FormatTopThree"/> exists to avoid. So the dependency points the other
/// way: this adapter knows both sides, and the trace keeps its string parameter.</para>
///
/// <para><b>The emitted line is byte-identical to the pre-CAI2.4 one.</b> It calls the same
/// <see cref="CandidateScorer.FormatTopThree(IReadOnlyList<(string ActorKey, ScoreBreakdown Breakdown)>, int)"/>
/// on the same top-three and appends the same <c>saturatedBy</c> suffix only when the choice was
/// saturated. That is this module's identity proof: `SiegeAiIntentSourceTests`'s line-shape assertions
/// and `AggressionTierMapTests.The_trace_line_names_a_saturated_choice` pass UNCHANGED.</para>
/// </summary>
public sealed class BattleTraceDecisionSink : IAiDecisionSink
{
    readonly BattleTrace _trace;

    public BattleTraceDecisionSink(BattleTrace trace) =>
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));

    public void Record(in AiDecisionRecord record)
    {
        var summary = CandidateScorer.FormatTopThree(record.TopThree, record.TopThree.Count);

        // §4: "this module only guarantees the fact is AVAILABLE and that the trace line names it when
        // it is non-zero". Off the real decision, never a fabricated one, and no formatting at all when
        // the choice was not saturated.
        if (record.ChosenSaturatedBy != 0 && record.ChosenTargetKey is not null)
            summary = $"{summary} saturatedBy={record.ChosenSaturatedBy}({record.ChosenTargetKey})";

        _trace.AiDecision(record.Round, record.ActorKey, summary);
    }
}
