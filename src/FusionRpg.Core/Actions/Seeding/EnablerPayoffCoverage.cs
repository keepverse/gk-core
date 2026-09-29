using System.Linq;

namespace FusionRpg.Core.Actions.Seeding;

public readonly record struct EnablerPayoffCoverageResult(bool IsOk, string? UnpairedPayoffFamily)
{
    public static readonly EnablerPayoffCoverageResult Ok = new(true, null);
    public static EnablerPayoffCoverageResult Fail(string payoffFamily) => new(false, payoffFamily);
}

/// <summary>
/// T32 (spec-action-seeding.md §5, testing-strategy table): "every conditional payoff in a pool has
/// at least one enabler in the same pool" — asserted here, in Core, against a real generated pool,
/// never deferred to seedsmith (which does not run in the game and cannot gate the feature that
/// produces the pool it measures).
/// </summary>
public static class EnablerPayoffCoverage
{
    /// <param name="poolAtomFamilies">Every atom family present in one generated pool — the SAME pool
    /// a loadout or a rung's container would draw from, not a whole-catalog scan.</param>
    public static EnablerPayoffCoverageResult Check(IReadOnlyList<string> poolAtomFamilies, EnablerPayoffPairings pairings)
    {
        var present = new HashSet<string>(poolAtomFamilies, StringComparer.Ordinal);

        foreach (var family in present)
        {
            if (!pairings.IsPayoff(family)) continue;

            var enablers = pairings.EnablersOf(family);
            if (!enablers.Any(present.Contains))
                return EnablerPayoffCoverageResult.Fail(family);
        }

        return EnablerPayoffCoverageResult.Ok;
    }
}
