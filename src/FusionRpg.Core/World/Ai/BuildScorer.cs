using FusionRpg.Core.World.Ai.Utility;

namespace FusionRpg.Core.World.Ai;

/// <summary>
/// `ai-build-scorer` EP5.1 (spec-ai-build-scorer.md) — one rung the assign ladder could produce for a
/// species, with the two belief inputs the scorer reads for it.
///
/// <para><b>The inputs are supplied, never derived here.</b> <c>NeedFit</c> is "how badly this empire
/// wants what this rung's lean produces", per-mille against the neutral 1000 (<see cref="INeedVector"/>'s
/// own scale); <c>CounterFit</c> is "how well this rung answers the enemy posture we believe we are
/// facing". The mapping from a rung to a need is the part `sector-development`'s economy supplies (the
/// spec's own note: per-species weights would be a seedsmith DETERMINISTIC stage), so it arrives as a
/// caller's input here — the scorer's job is the scoring and the choice, not the belief.</para>
/// </summary>
/// <param name="RuleId">The ladder rule id the distribution came from (a closed vocabulary,
/// <c>AptitudeAutoAssignRules</c>). It is the tie-break key, so two rungs must never share one.</param>
public readonly record struct BuildRung(string RuleId, int NeedFit, int CounterFit);

/// <summary>The scorer's answer: the winning rung, its score, and the considerations that produced it —
/// the turn report names <see cref="Weakest"/> (`ai-build-scorer` EP5.3), the same "why did it choose
/// that" contract <see cref="Considerations.Weakest"/> already gives every other scorer.</summary>
public sealed record BuildChoice(
    string RuleId, int Score, IReadOnlyList<Consideration> Considerations, Consideration? Weakest);

/// <summary>
/// `ai-build-scorer` EP5.1 — the scorer's tunables: one curve and one threshold per consideration, plus
/// the continuity cooldown.
///
/// <para><b>No built-in default.</b> These are published data (`data/tuning/ai.v{n}.json`'s
/// <c>buildScorer</c> block, EP5.2, through `gk-core/tools/tuning/publish.py`), so this parameter object carries
/// what the caller loaded — never a value the code invents (tunables-ssot.md T5).</para>
/// </summary>
public sealed record BuildScorerTuning(
    ResponseCurve NeedFitCurve,
    int NeedFitThreshold,
    ResponseCurve CounterFitCurve,
    int CounterFitThreshold,
    int ContinuityCooldownTurns);

/// <summary>
/// `ai-build-scorer` EP5.1 — "which build rung should this species follow?" as a utility score, instead
/// of always taking the ladder's first legal rung.
///
/// <para><b>It computes no arithmetic of its own.</b> The product, the arity compensation and the zero
/// veto are <see cref="Considerations.Score"/>'s, already shipped and already tested in isolation; this
/// class writes the three considerations and picks the argmax. A second product here would be the
/// parallel-path defect the spec names (map D6) — a rules-locked decision (R-Q5: "in the new scorer
/// only", migrate nothing) reusing the one scorer the repo already has rather than writing a twin.</para>
///
/// <para><b>Deterministic.</b> A pure function of the rungs, the belief inputs and the tuning: no seed,
/// no clock, no model call. Ties break by ordinal rule id, so the same belief always names the same
/// rung, which is what makes a replay of the same turn agree with itself.</para>
///
/// <para><b>Neutral inputs change nothing.</b> With <see cref="UniformNeeds"/>-derived inputs and no
/// observed enemy posture every candidate scores the same, and the ordinal tie-break returns the ladder's
/// own rung — the property that documented why this module waited for an economy (spec testing strategy
/// 3). It is not a fallback path: the same arithmetic produces it, and real needs make it stop
/// holding.</para>
///
/// <para><b>Fairness.</b> The candidates are rungs the ladder could already produce, so the winner is
/// always a distribution a species could have had anyway — "a harder Zomboss is a higher Theta or a
/// better allocation, never a stat nobody could have had" (`class-system-ideal.md` section 6.1).</para>
/// </summary>
public static class BuildScorer
{
    /// <summary>The three consideration names, so a turn report and a test name the same axis.</summary>
    public const string NeedFitName = "needFit";
    public const string CounterFitName = "counterFit";
    public const string ContinuityName = "continuity";

    /// <summary>
    /// The three considerations for one rung: what the empire needs, how well it answers the believed
    /// enemy posture, and whether re-patterning now is allowed at all. The last one is the rate limit — a
    /// rung re-picked inside the cooldown scores zero and is therefore vetoed whatever its other two axes
    /// say (`zomboss-adaptive`: "the rate limit is not optional").
    /// </summary>
    public static IReadOnlyList<Consideration> ConsiderationsFor(
        BuildRung rung, int turnsSinceRepattern, BuildScorerTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        return new[]
        {
            new Consideration(NeedFitName, tuning.NeedFitCurve, rung.NeedFit, tuning.NeedFitThreshold),
            new Consideration(CounterFitName, tuning.CounterFitCurve, rung.CounterFit, tuning.CounterFitThreshold),
            // Higher elapsed turns scores better here, and the threshold IS the cooldown: below it the
            // curve answers zero, and zero vetoes the rung.
            new Consideration(ContinuityName, ResponseCurve.Threshold, turnsSinceRepattern, tuning.ContinuityCooldownTurns),
        };
    }

    /// <summary>The argmax rung, ties broken by ordinal rule id. Throws when there is nothing to score:
    /// an empty candidate set is a caller wiring defect, never "no build today".</summary>
    public static BuildChoice Choose(
        IReadOnlyList<BuildRung> rungs, int turnsSinceRepattern, BuildScorerTuning tuning)
    {
        if (rungs is null) throw new ArgumentNullException(nameof(rungs));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (rungs.Count == 0)
            throw new ArgumentException("no candidate rungs to score", nameof(rungs));

        return rungs
            .Select(rung =>
            {
                var considerations = ConsiderationsFor(rung, turnsSinceRepattern, tuning);
                return new BuildChoice(
                    rung.RuleId,
                    // The one product, and it is not written here.
                    Considerations.Score(considerations),
                    considerations,
                    Considerations.Weakest(considerations));
            })
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.RuleId, StringComparer.Ordinal)
            .First();
    }
}
