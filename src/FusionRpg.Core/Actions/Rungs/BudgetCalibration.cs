using FusionRpg.Core.Effects.Atoms.Power;

namespace FusionRpg.Core.Actions.Rungs;

/// <summary>
/// ST4.2 (`spec-budget-calibration-report.md` contract 2): one authored rung's reading — the
/// distribution of <c>impliedReferencePower</c> over the priced actions that sit on it.
///
/// <para><see cref="Count"/> of <c>0</c> means the four percentile fields are <c>null</c>: a rung with
/// no content has nothing to read, and reporting four zeros would read as "every action on this rung
/// needs a scalar of 0", which is the opposite of the truth.</para>
/// </summary>
/// <param name="ActionsAboveLoadedScalar">
/// The ids whose implied scalar is above what the LOADED table's own <c>powerBudgetMilli</c> implies for
/// this rung — i.e. the actions the budget check rejects. Empty when the loaded table carries no
/// <c>powerBudgetMilli</c> column (a pre-A-G1 table budgets nothing, so nothing is above it).
/// </param>
/// <param name="ActionsAboveP90">
/// ST4.5b (contract 6): the ids whose implied scalar is strictly above this rung's own reported
/// <see cref="P90"/> — the outliers the change description names, so an action that drags the scalar up
/// is visible rather than silent. Strictly above, and the <c>P90</c> is nearest-rank, so on a rung with
/// fewer than ten priced actions <c>P90</c> IS <see cref="Max"/> and this list is empty by construction:
/// there is no tenth action to be above it, and the largest reading is already <see cref="Max"/>.
/// Empty, never null, on a rung with no content.
/// </param>
public sealed record RungCalibration(
    int Rung,
    int Count,
    long? Min,
    long? P50,
    long? P90,
    long? Max,
    int PoolRolls,
    int QPowerMilli,
    long? PowerBudgetMilli,
    IReadOnlyList<string> ActionsAboveLoadedScalar,
    IReadOnlyList<string> ActionsAboveP90);

/// <summary>
/// ST4.2 (`spec-budget-calibration-report.md` contracts 2 and 6): the whole reading. <see cref="Rungs"/>
/// carries one row per rung the loaded table declares, in rung order, including the rungs with no
/// content — the absence is part of the picture. <see cref="PricedActionCount"/> is what the per-rung
/// counts must reconcile to, so a caller can check the report rather than trust it.
///
/// <para><see cref="RecommendedReferencePower"/> is contract 6's rule: the smallest scalar at which no
/// priced action's realized power exceeds its rung's budget, or <c>null</c> when nothing was priced.
/// It uses CEILING division where <c>impliedReferencePower</c> uses floor — deliberately, and the two
/// differ by at most one: the recommendation has to be safe (a scalar one too low would reject content
/// the owner already accepted), while the per-rung reading is the honest inverse of the published
/// floor-derived budget.</para>
///
/// <para><see cref="RecommendedBy"/> is the argmax of that ceiling — the ids that SET the scalar, so
/// the outlier dragging it up is named rather than merely implied by a number. Rung 4 of the real
/// corpus is why it exists: n=3, so its p90 IS its max and every rung's <c>ActionsAboveP90</c> is
/// empty by construction there, yet its one 1511 action is what sets the scalar to 1512.</para>
/// </summary>
public sealed record BudgetCalibrationResult(
    IReadOnlyList<RungCalibration> Rungs,
    int PricedActionCount,
    long? RecommendedReferencePower,
    IReadOnlyList<string> RecommendedBy,
    IReadOnlyList<UnpricedAction> UnpricedActions);

/// <summary>
/// ST4.5e (manager ruling on the ST4.5a diagnosis): one action whose container holds atoms the pricing
/// could not price, with the atom ids. The action's <see cref="PricedAction.RealizedPowerMilli"/> is
/// unchanged — this is not a correction to the number, it is the NAMING of what the number left out,
/// so a zero can no longer be read as "this action is worth nothing" when the truth is "these atoms
/// could not be priced". The three pooled-channel actions in the real ST4.5a reading were exactly this.
/// </summary>
public sealed record UnpricedAction(string ActionId, int AuthoredRung, IReadOnlyList<string> AtomIds);

/// <summary>
/// ST4.2 — the calibration of A-G1's per-rung power budget against real content (ruling 3 shipped
/// <c>referencePower</c> neutral and stated untuned; R8 makes the first reading tune it).
///
/// <para><b>Pure, and reads only the loaded table.</b> <c>impliedReferencePower(a) = realizedPowerMilli
/// (a) × PowerMath.One / (poolRolls(r) × qPowerMilli(r))</c> inverts the published derivation
/// (<c>powerBudgetMilli(r) = poolRolls × referencePower × qPowerMilli / 1000</c>), so it works against
/// whatever table the server has loaded and needs no second curve and no new tuning key. The arithmetic
/// is <c>checked</c> <c>long</c>, widened before multiplying, one division last and exactly once; it
/// throws on overflow and refuses a non-positive factor by name rather than dividing by zero.</para>
/// </summary>
public static class BudgetCalibration
{
    /// <summary>Report columns, not balance numbers: the two percentiles this reading publishes. Named
    /// constants with this comment, matching the spec's own Tunables note.</summary>
    public const int MedianPercentile = 50;
    public const int P90Percentile = 90;

    /// <summary>
    /// The reading. <paramref name="actions"/> is the priced catalog (<c>RpgStore.ListActionPricing</c>)
    /// and <paramref name="table"/> the table those prices are read against — the same pair A-G1's
    /// catalog check uses, so the report calibrates the real check (contract 1).
    /// </summary>
    public static BudgetCalibrationResult Read(IReadOnlyList<PricedAction> actions, RungTable table)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(table);

        var impliedByRung = new Dictionary<int, List<(string ActionId, long Implied)>>();
        long? recommended = null;
        var recommendedBy = new List<string>();

        foreach (var action in actions)
        {
            if (!table.TryGet(action.AuthoredRung, out var row))
                throw new InvalidOperationException(
                    $"action '{action.ActionId}' has authored rung {action.AuthoredRung}, which the " +
                    $"loaded rung table does not carry (cap {table.Cap}) — the budget check reads the " +
                    $"authored rung, so a table without it cannot be calibrated against");

            var implied = ImpliedReferencePower(action.RealizedPowerMilli, row);
            var ceiling = CeilingReferencePower(action.RealizedPowerMilli, row);

            // ST4.5f: keep the argmax, not just the max. A tie is kept whole -- two actions that need
            // the same scalar both set it, and naming one of them would hide the other.
            if (recommended is null || ceiling > recommended)
            {
                recommended = ceiling;
                recommendedBy.Clear();
                recommendedBy.Add(action.ActionId);
            }
            else if (ceiling == recommended)
            {
                recommendedBy.Add(action.ActionId);
            }

            if (!impliedByRung.TryGetValue(row.Rung, out var list))
                impliedByRung[row.Rung] = list = new List<(string, long)>();
            list.Add((action.ActionId, implied));
        }

        var rungs = new List<RungCalibration>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            var entries = impliedByRung.TryGetValue(row.Rung, out var found)
                ? found
                : new List<(string ActionId, long Implied)>();

            if (entries.Count == 0)
            {
                rungs.Add(new RungCalibration(
                    row.Rung, 0, null, null, null, null,
                    row.PoolRolls, row.QPowerMilli, row.PowerBudgetMilli,
                    Array.Empty<string>(), Array.Empty<string>()));
                continue;
            }

            var sorted = entries.Select(e => e.Implied).OrderBy(v => v).ToList();
            var p50 = NearestRank(sorted, MedianPercentile);
            var p90 = NearestRank(sorted, P90Percentile);

            // ST4.5b: the outliers contract 6's change description has to name. Read against the SAME
            // p90 this row publishes, so the list and the percentile cannot disagree about where the
            // line is -- the same discipline the loaded-scalar list below already applies.
            var aboveP90 = entries.Where(e => e.Implied > p90)
                                  .Select(e => e.ActionId)
                                  .OrderBy(id => id, StringComparer.Ordinal)
                                  .ToList();

            // The loaded table's own implied scalar, read with the SAME inversion, so "above the line"
            // and the report's own numbers cannot disagree about where the line is.
            var above = row.PowerBudgetMilli is { } budget
                ? entries.Where(e => e.Implied > ImpliedReferencePower(budget, row))
                         .Select(e => e.ActionId)
                         .OrderBy(id => id, StringComparer.Ordinal)
                         .ToList()
                : new List<string>();

            rungs.Add(new RungCalibration(
                row.Rung, entries.Count,
                sorted[0],
                p50,
                p90,
                sorted[^1],
                row.PoolRolls, row.QPowerMilli, row.PowerBudgetMilli, above, aboveP90));
        }

        return new BudgetCalibrationResult(
            rungs,
            actions.Count,
            recommended,
            recommendedBy.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            // ST4.5e: the actions whose price left something out, named and in id order. A reader that
            // reported no findings (null) contributes nothing here -- the report never guesses.
            actions.Where(a => a.UnpricedAtomIds is { Count: > 0 })
                   .Select(a => new UnpricedAction(a.ActionId, a.AuthoredRung, a.UnpricedAtomIds!))
                   .OrderBy(u => u.ActionId, StringComparer.Ordinal)
                   .ToList());
    }

    /// <summary>Nearest-rank on the sorted readings: no interpolation, no floating point (the spec's
    /// own Numeric-types note allows floats; they are not needed). <c>ceil(p × n / 100)</c> is the rank
    /// in 1..n, so the index is that rank − 1 and always in range for a non-empty list.</summary>
    static long NearestRank(IReadOnlyList<long> sorted, int percentile)
    {
        var rank = (int)checked(((long)percentile * sorted.Count + 99) / 100);
        return sorted[Math.Max(1, rank) - 1];
    }

    /// <summary>
    /// The referencePower an action would need to sit exactly on its authored rung's budget — the
    /// published derivation inverted, floor division, <c>checked</c> throughout. `PowerMath.One` is the
    /// per-mille denominator the budget derivation itself uses, so this is an inversion of the shipped
    /// formula rather than a second one.
    /// </summary>
    static long ImpliedReferencePower(long realizedPowerMilli, RungRow row) =>
        checked(realizedPowerMilli * PowerMath.One) / DivisorOf(row);

    /// <summary>Contract 6's rule, in the safe direction: the smallest scalar whose budget still covers
    /// the action. Ceiling is exact against the publisher's floor — <c>R × REF × q ≥ realized × One</c>
    /// implies <c>floor(R × REF × q / One) ≥ realized</c> — which is why the recommendation is never one
    /// short.</summary>
    static long CeilingReferencePower(long realizedPowerMilli, RungRow row)
    {
        var divisor = DivisorOf(row);
        return checked((checked(realizedPowerMilli * PowerMath.One) + divisor - 1) / divisor);
    }

    /// <summary>The inversion's divisor, with the one refusal it needs: a non-positive <c>poolRolls</c>
    /// or <c>qPowerMilli</c> has no reading, and a divide-by-zero would say that far less clearly.</summary>
    static long DivisorOf(RungRow row) =>
        row.PoolRolls <= 0 || row.QPowerMilli <= 0
            ? throw new InvalidOperationException(
                $"rung {row.Rung} carries poolRolls={row.PoolRolls} and qPowerMilli={row.QPowerMilli}; " +
                $"the implied reference power divides by their product, so a non-positive factor has no reading")
            : checked((long)row.PoolRolls * row.QPowerMilli);
}
