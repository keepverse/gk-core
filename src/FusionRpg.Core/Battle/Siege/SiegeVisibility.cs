using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Board;

namespace FusionRpg.Core.Battle.Siege;

/// <summary>
/// base-defense `siege-fog` (spec-siege-fog.md §1, module 30, 2026-09-06). ONE shared visibility
/// computation — <see cref="FoggedBattleView"/> and <see cref="FoggedOccupancy"/> both call this, never
/// two divergent implementations of "can side X see cell Y" that could disagree with each other.
///
/// <para>Pure, deterministic, no caching — the same "recompute, don't cache" precedent
/// `LineOfFire`/`GridDistance` already set on this board. `int` throughout: vision range and cell
/// distances are small, board-bounded tile counts, not a `long`-scale magnitude (docs/architecture/numeric-types.md's `long`
/// rule is for hp/damage/stock; a coordinate bounded by board side length is a different thing —
/// the same reasoning `BoardPath.TotalCost`'s own doc comment already draws).</para>
/// </summary>
public static class SiegeVisibility
{
    /// <summary>One friendly watcher: where it stands, and how far it can see.</summary>
    public readonly record struct Watcher(GridPos Position, int VisionRangeTiles);

    /// <summary>
    /// True when SOME watcher is within its own vision range of <paramref name="target"/> AND has an
    /// unobstructed line to it (<see cref="LineOfFire.HasLineOfFire"/> — the SAME geometry
    /// `siege-obstacles`' own hard line-of-fire blocking already uses; fog is a third, distinct concept
    /// from that and from `siege-cover`'s softer obstruction math, and reuses neither's blocking rule
    /// beyond this one shared primitive).
    /// </summary>
    public static bool IsVisible(GridPos target, IReadOnlyList<Watcher> watchers, Func<GridPos, bool> blocksVision)
    {
        if (watchers is null) throw new ArgumentNullException(nameof(watchers));
        if (blocksVision is null) throw new ArgumentNullException(nameof(blocksVision));

        for (var i = 0; i < watchers.Count; i++)
        {
            var w = watchers[i];
            if (GridDistance.Chebyshev(w.Position, target) > w.VisionRangeTiles) continue;
            if (LineOfFire.HasLineOfFire(w.Position, target, blocksVision)) return true;
        }

        return false;
    }
}
