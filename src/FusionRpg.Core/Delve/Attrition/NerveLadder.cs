using FusionRpg.Core.Actions.Cost;

namespace FusionRpg.Core.Delve.Attrition;

/// <summary>
/// `delve-attrition` D2.19 (spec-delve-attrition.md §4) — the pure stage resolver for the staged
/// `nerve` status. `StatusInstance` has no stack count and the shipped `Counter` kind (`bond`,
/// `StatusCatalogBootstrap.cs`) still carries none, so the counter lives in party state
/// (`DelveMemberState.NerveStacks`) and this resolver is its projection to a stage index — never a
/// status field itself.
/// </summary>
public static class NerveLadder
{
    /// <summary>Highest stage whose threshold ≤ stacks; exhausted spirit is the top stage regardless. -1 = none.</summary>
    public static int StageFor(int stacks, long spiritResolved, IReadOnlyList<int> thresholds)
    {
        if (thresholds is null) throw new ArgumentNullException(nameof(thresholds));
        if (ExhaustionPolicy.IsExhausted(spiritResolved)) return thresholds.Count - 1;
        var stage = -1;
        for (var i = 0; i < thresholds.Count; i++) if (stacks >= thresholds[i]) stage = i;
        return stage;
    }
}
