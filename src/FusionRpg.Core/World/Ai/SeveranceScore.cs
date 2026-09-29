using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Topology;

namespace FusionRpg.Core.World.Ai;

/// <summary>
/// How much cutting one of an enemy's sectors would cost *them* (spec-loam-ai.md §8.7/§12.3) — the
/// same reconnection-cost sweep <see cref="ValueMap"/>'s own strategic axis already runs against the
/// viewer's own holdings, pointed at a target faction's believed holdings instead. A sibling to
/// <see cref="ValueMap"/>, not a method on it: this answers a structurally different question
/// (attacking someone else's topology, not scoring my own candidates), so it keeps
/// <see cref="SectorValue"/>'s six-axis shape from growing a column that means something else.
///
/// Scouting-gated by construction, not by accident: <see cref="ReconnectionCost"/> gates itself to
/// zero below three sectors in scope, and a believed lane with neither end in sight reads Open
/// regardless of truth — both combine to make this read near-zero, often flat zero, until the viewer
/// has actually scouted a meaningful chunk of the target's territory as enemy-owned. That degenerate
/// reading is accepted, not patched around: an AI with poor scouting should be unable to find good
/// severance targets, the same way it cannot find hidden rootbeds.
/// </summary>
public static class SeveranceScore
{
    public static long For(IWorldView view, string targetFactionId, string sectorId)
    {
        var targetHoldings = view.SectorIds
            .Where(id => view.Believed(id) is { } believed
                         && string.Equals(believed.OwnerFactionId, targetFactionId, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        if (!targetHoldings.Contains(sectorId)) return 0;

        var costs = ReconnectionCost.For(view.SectorIds, view.Lanes, MarchGraph.ClimateOf(view), targetHoldings);
        return costs.TryGetValue(sectorId, out var cost) ? cost : 0;
    }
}
