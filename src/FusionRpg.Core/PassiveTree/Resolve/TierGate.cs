namespace FusionRpg.Core.PassiveTree.Resolve;

/// <summary>
/// D26/D12 (spec-tree-resolve.md §3, task B6). `req(t) = k·t(t+1)/2` reads APTITUDE POINTS, never
/// the skill wallet (R1) — a different currency from `tree-state`'s unlock cost. D12 ("tier gates
/// read base allocation, never item bonuses") needs no enforcement code: an aptitude is a SOURCE,
/// never a registered channel, so no item-derived quantity can move it at all.
/// </summary>
public static class TierGate
{
    /// <summary>The deepest tier reached, bounded by the catalog's own authored tier count — an
    /// ASCENDING INTEGER LOOP, never a closed form (solving `k·t(t+1)/2 &lt;= g` needs a square
    /// root, and a rounded root could misplace an exact boundary on a gate that decides whether
    /// content exists — the integer loop is exact by construction; a design choice, not a
    /// floating-point ban, which was removed 2026-09-15). The loop
    /// bound is structural (the tree's own shape), not a progression cap: nothing is refused, there
    /// is simply no node above the authored depth to buy.</summary>
    public static int Reached(long aptitudePoints, int authoredTierCount, long reqScalePoints)
    {
        if (authoredTierCount < 1)
            throw new ArgumentOutOfRangeException(nameof(authoredTierCount), authoredTierCount, "must be >= 1");

        var reached = 0;
        for (var t = 1; t <= authoredTierCount; t++)
        {
            long req;
            checked { req = reqScalePoints * t * (t + 1) / 2; }
            if (aptitudePoints < req) break;
            reached = t;
        }
        return reached;
    }
}
