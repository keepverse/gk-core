using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;

namespace FusionRpg.Core.Actions.Rungs;

public readonly record struct RungMonotonicityResult(bool Ok, string Detail);

/// <summary>
/// T5 (action-todo.md): prices every rung through E9's <see cref="PowerVector"/> and fails if rung
/// <c>u+1</c> is not worth more than rung <c>u</c> — spec-rung-table.md §5.
///
/// <para><b>What this does and does not prove.</b> It prices the ladder's own <c>qPowerMilli</c>
/// column against a synthetic non-conditional reference vector, which needs nothing from `P0.3`
/// (predicate pricing) to be meaningful — no condition is involved. `P0.3` has since landed (see
/// <see cref="PredicatePricingLanded"/>), so a conditional atom now prices at a real discount rather
/// than as though its condition always holds — but this test still does not exercise that path: what
/// it does NOT prove is that a REAL generated container spending its rung's `structureBudget` on a
/// `condition` axis climbs monotonically once discounted. That check belongs to `A13`'s generation
/// tests (Phase 9), once real conditional containers exist — this one is scoped to the authored
/// ladder itself, per §5's own words: "an authored ladder is a list of numbers nobody checked."</para>
/// </summary>
public static class RungMonotonicity
{
    /// <summary>Whether `P0.3` (predicate pricing) has landed. True as of 2026-08-28 — `CostFunction
    /// .Conditionality` now folds <see cref="PredicatePricer"/> into a triggered atom's price, and
    /// <see cref="PowerTables.PredicateFrequencyOf"/> backs it with real (or safely-neutral 1000‰
    /// default) data. Recorded here rather than silently assumed, so a future conditional-container
    /// monotonicity test (`A13`, Phase 9) knows the discount is live and can assert against it instead
    /// of treating every condition as though it always holds.</summary>
    public const bool PredicatePricingLanded = true;

    static readonly PowerVector Reference = PowerVector.FromCategory(PowerCategory.Offense, 1000);

    /// <summary>Every rung must price strictly higher than the one before it.</summary>
    public static RungMonotonicityResult VerifyPowerClimbs(RungTable table)
    {
        int? prev = null;
        int? prevRung = null;

        foreach (var row in table.Rows)
        {
            var priced = Reference.ScaleMilli(row.QPowerMilli).Total;
            if (prev is { } p)
            {
                if (priced <= p)
                    return new RungMonotonicityResult(false,
                        $"rung {row.Rung} prices at {priced}, not worth more than rung {prevRung} at {p}");
            }
            prev = priced;
            prevRung = row.Rung;
        }

        return new RungMonotonicityResult(true, "");
    }

    /// <summary>
    /// `qCost(cap)/qCost(2) > qPower(cap)/qPower(2)` — spec-rung-table.md §3's balance rule, asserted
    /// as a number. A regression to a flat tax (cost span == power span) is exactly the shape that
    /// makes the loadout a sort instead of a decision.
    /// </summary>
    public static RungMonotonicityResult VerifyCostSpanExceedsPowerSpan(RungTable table)
    {
        if (!table.TryGet(2, out var r2))
            return new RungMonotonicityResult(false, "table has no rung 2 to compare against");
        if (!table.TryGet(table.Cap, out var rCap))
            return new RungMonotonicityResult(false, $"table has no rung {table.Cap} (its own cap)");

        var powerSpanMilli = (long)rCap.QPowerMilli * 1000 / r2.QPowerMilli;
        var costSpanMilli = (long)rCap.CostMulti * 1000 / r2.CostMulti;

        if (costSpanMilli <= powerSpanMilli)
            return new RungMonotonicityResult(false,
                $"cost span {costSpanMilli}‰ does not exceed power span {powerSpanMilli}‰ — " +
                "a flat or inverted tax makes the top rung strictly dominate");

        var taxMilli = costSpanMilli * 1000 / powerSpanMilli;
        return new RungMonotonicityResult(true,
            $"cost span {costSpanMilli}‰ / power span {powerSpanMilli}‰ ({taxMilli}‰ tax)");
    }
}
