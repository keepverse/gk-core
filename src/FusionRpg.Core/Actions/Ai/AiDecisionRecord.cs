using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `decision-inspector` (module 10, CAI2.4, spec-decision-inspector.md §1): real-time trigger
/// state — swings since the last cast, the timer's tick, the post-cast lock's expiry tick, tokens held.
/// <see cref="None"/> is the turn-mode answer: a turn mode has no trigger of its own, because the
/// engine PULLS (`S1-battle-core.md` "a pull model") rather than the policy ticking.
/// </summary>
public readonly record struct AiTriggerState(int Swings, long TimerTick, long LockUntilTick, int Tokens)
{
    public static readonly AiTriggerState None = default;
}

/// <summary>
/// combat-ai `decision-inspector` §1: who ORIGINATED the decision. Three members, a closed vocabulary
/// the code owns: <c>Policy</c> (the intent source's own choice), <c>Order</c> (a player/commander
/// direct order took the decision) and <c>Steered</c> (an interactive or replay override supplied it).
/// The inspector reads this field; it never infers an origin from <c>Candidates[0]</c> — read-time
/// re-derivation is exactly what the spec forbids. Adding a fourth is a reviewed change and moves the
/// count here in the same commit.
/// </summary>
public enum AiDecisionOrigin { Policy = 0, Order = 1, Steered = 2 }

/// <summary>
/// combat-ai `decision-inspector` §1: one candidate's gate outcome and score breakdown, as
/// <see cref="UsabilityResult"/>'s own typed vocabulary (never a second refusal vocabulary and never a
/// bare bool — that enum exists precisely because "the FE needs to explain a greyed button").
///
/// <para><b><see cref="Gate"/> and <see cref="ActionId"/> are nullable on purpose.</b> A place records
/// the verdicts it ACTUALLY ran. At a target-selection site the gate loop runs against the CHOSEN
/// candidate only, so several candidates legitimately carry no verdict — and a fabricated
/// <c>Usable</c> would be the read-time re-derivation §1 bans. The spec's non-null sketch assumed a
/// per-action gate loop, which is the core policy's action stage rather than every place's target
/// stage; null here means "no gate ran for this candidate", which is a fact, not a gap.</para>
/// </summary>
public readonly record struct AiCandidateVerdict(
    string TargetKey, string? ActionId, UsabilityResult? Gate, ScoreBreakdown? Breakdown);

/// <summary>
/// combat-ai `decision-inspector` §1: one decision, exactly as it was made. Every field is a value the
/// decision ACTUALLY read or produced — never a re-derivation. <c>ScoreBreakdown.Total</c> comes from
/// <see cref="CandidateScorer.Score"/> called directly, the discipline
/// <see cref="CandidateScorer.ScoreBreakdownOf"/> already keeps ("a second accumulation could
/// overflow/round differently than the tested, already-shipped one").
///
/// <para><b>Provenance is nullable, and null is a fact.</b> <see cref="Tier"/>, <see cref="ProfileId"/>
/// and <see cref="Personality"/> are module 2/3 values that only the policy which RESOLVED a profile
/// can supply. `SiegeAiIntentSource` is handed <see cref="ScoringWeights"/> directly (CAI1.8's H7
/// move), so it has no profile to name and records null rather than inventing an id; module 3's
/// <c>CoreIntentPolicy</c> is the place that will fill them.</para>
/// </summary>
public readonly record struct AiDecisionRecord(
    long NowTick, int Round, string ActorKey,
    AiTier? Tier, string? ProfileId, AiPersonality? Personality,
    AiTriggerState Trigger,
    AiDecisionOrigin Origin,
    IReadOnlyList<AiCandidateVerdict> Candidates,
    string? ChosenActionId, string? ChosenTargetKey, int ChosenSaturatedBy,
    IReadOnlyList<(string ActorKey, ScoreBreakdown Breakdown)> TopThree);

/// <summary>
/// combat-ai `decision-inspector` §1: the ONE recording seam. A null sink means the inspector is off,
/// and OFF MUST COST NOTHING — every call site is <c>sink is not null</c>-guarded and every argument is
/// built INSIDE that guard, so a sink-less decision allocates none of the record.
///
/// <para>Thin contribution API over a fat surface (SOLID I): a future FE panel, the lawn's bounded
/// ring, a `BattleTrace` adapter and a test spy all implement this one method, and no call site
/// depends on any of them.</para>
/// </summary>
public interface IAiDecisionSink
{
    void Record(in AiDecisionRecord record);
}
