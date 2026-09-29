using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Items.Requirements;

/// <summary>The trial evaluator (item/spec-requirement-profiles.md §"Trial evaluator"): pure, over the
/// frozen profile plus unassisted actor inputs (level, allocation). Returns `ready` or the first
/// named unmet clause — never an equip refusal, never an effect toggle, never a write of any kind.
///
/// <para>Ratios stay integer-exact: `aptitudePoints * 1000 >= grandAllocationPoints *
/// minimumShareMilli` in `checked long`, widened before the multiply, compared once. 1000 is the
/// structural denominator of the persisted per-mille ratio, not a balance value. Overflow throws; an
/// empty allocation fails every positive floor by explicit guard (0 >= 0 would otherwise read ready,
/// which invents a build where none was chosen).</para>
///
/// <para>Upkeep is deliberately ignored: module 24 evaluates that obligation through the shared cost
/// path, and a second resource-payment mechanism here would be the defect the spec names.</para>
/// </summary>
public static class RequirementTrialEvaluator
{
    public const long PerMille = 1000;

    public static TrialEvaluation Evaluate(RequirementProfile profile, int level, AptitudeAllocation allocation)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        if (allocation is null) throw new ArgumentNullException(nameof(allocation));

        if (profile.MinimumLevel is { } floor && level < floor)
            return new TrialEvaluation(false, TrialUnmetClause.Level);

        if (profile.BuildTrial is { } trial)
        {
            if (trial.MinimumPoints is { } points)
            {
                var total = allocation.Total(trial.AptitudeId);
                if (total < points)
                    return new TrialEvaluation(false, TrialUnmetClause.FixedAptitude);
            }
            else if (trial.MinimumShareMilli is { } share)
            {
                if (!MeetsShare(allocation, trial.AptitudeId, share))
                    return new TrialEvaluation(false, TrialUnmetClause.RatioAptitude);
            }
        }

        return new TrialEvaluation(true, TrialUnmetClause.None);
    }

    /// <summary>The spec's own sample, verbatim in contract (`AptitudeAllocation.Share`'s `double`
    /// convenience reader must never back this): scope-summed totals, integer-exact, overflow-throwing.</summary>
    public static bool MeetsShare(AptitudeAllocation allocation, string aptitudeId, long minimumShareMilli)
    {
        if (allocation is null) throw new ArgumentNullException(nameof(allocation));
        if (minimumShareMilli < 0 || minimumShareMilli > PerMille)
            throw new ArgumentOutOfRangeException(nameof(minimumShareMilli), minimumShareMilli,
                "a per-mille share lives in [0..1000]");
        var total = allocation.GrandTotal();
        // An empty allocation fails every positive floor: without this guard 0 >= 0 reads ready and
        // invents a build where none was chosen (AptitudeAllocation's own empty-means-zero rule).
        if (total == 0) return minimumShareMilli == 0;
        var points = allocation.Total(aptitudeId);
        checked
        {
            return points * PerMille >= total * minimumShareMilli;
        }
    }
}
