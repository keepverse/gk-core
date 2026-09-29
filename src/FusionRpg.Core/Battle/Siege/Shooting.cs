using FusionRpg.Core.Actions;

namespace FusionRpg.Core.Battle.Siege;

/// <summary>One live thing standing in a line of fire — a structure obstacle or an animate actor.
/// Units obstruct too, not only obstacles (decision 35) — that is what makes body-blocking real.</summary>
public readonly record struct Obstruction(Actions.GridPos Cell);

/// <summary>base-defense `siege-cover`'s own balance surface, `gk-core/data/tuning/siege.v1.json`'s
/// `shooting.*` block. All per-mille multipliers, 1000 = no penalty, deliberately no `P(Θ)` anywhere
/// here — a per-mille multiplier is scale-free by construction (`×500‰` at Θ=1 is `×500‰` at Θ=200),
/// which is the whole reason decision 35 is architecturally legal in the first place.</summary>
public sealed record SiegeShootingTuning(
    int RangeThresholdMilli, int RangePowerMilli,
    int ObstructionPowerMilli, int ObstructionFloorMilli,
    int MeleeLockPowerMilli);

/// <summary>
/// base-defense `siege-cover` (spec-siege-cover.md), owner decision 35: the HoMM3-inspired shooting
/// model — cover area, range falloff, obstruction, projectile kind — composed into one per-mille power
/// factor applied to the outgoing damage BEFORE the dispatcher, so shields/elements/Funnel/FA10 all see
/// one already-adjusted number. Every mechanic REDUCES power; none of them blocks a shot outright —
/// only <see cref="LineOfFire"/>'s own separate, harder `RequiresLineOfSight` gate (a Rampart's
/// `BlocksLineOfFire`) can refuse a shot entirely, and that is `siege-obstacles`' mechanism, not this
/// module's.
/// </summary>
public static class Shooting
{
    /// <summary>Mechanic 2: power falls off beyond a threshold measured as a FRACTION of the board's
    /// own side — an 18-cell and a 30-cell board must not share a falloff point, or a stronghold's
    /// longer sightlines become a free buff to every archer standing on it. `checked`, divide by 1000
    /// last and once.</summary>
    public static int RangePowerMilli(int chebyshevDistance, int boardSide, SiegeShootingTuning tuning)
    {
        if (chebyshevDistance < 0) throw new ArgumentOutOfRangeException(nameof(chebyshevDistance));
        if (boardSide <= 0) throw new ArgumentOutOfRangeException(nameof(boardSide));
        var thresholdCells = checked((long)boardSide * tuning.RangeThresholdMilli / 1000);
        return chebyshevDistance > thresholdCells ? tuning.RangePowerMilli : 1000;
    }

    /// <summary>Mechanic 3: power multiplier from everything standing in the line of fire —
    /// multiplicative PER obstruction, bounded by a soft floor so a crowded board stays shootable
    /// (two obstructions are worse than one; twenty are not twenty times worse). `checked` throughout;
    /// the floor is a MAX, never a clamp that hides an authoring mistake — it only ever raises a result
    /// that would otherwise round to nothing.</summary>
    public static int ObstructionPowerMilli(IReadOnlyList<Obstruction> inLine, SiegeShootingTuning tuning)
    {
        if (inLine is null) throw new ArgumentNullException(nameof(inLine));
        long milli = 1000;
        foreach (var _ in inLine)
            milli = checked(milli * tuning.ObstructionPowerMilli / 1000);
        return (int)Math.Max(milli, tuning.ObstructionFloorMilli);
    }

    /// <summary>3b, HoMM3's third penalty: a shooter with an enemy adjacent (Chebyshev distance 1)
    /// shoots at reduced power — makes closing on archers the correct answer.</summary>
    public static int MeleeLockPowerMilli(bool shooterHasAdjacentEnemy, SiegeShootingTuning tuning) =>
        shooterHasAdjacentEnemy ? tuning.MeleeLockPowerMilli : 1000;

    /// <summary>
    /// Mechanic 1: the best SINGLE cover applying to a target cell — never stacked (a cluster of cheap
    /// works must never be strictly better than one good one, the distribution-skew failure
    /// `05-failure-modes.md` records). A live obstacle whose `Spec.CoverRadius` reaches the target,
    /// keyed by Chebyshev distance from the obstacle's own cell. Callers supply only LIVE obstacles
    /// (a destroyed one, `SlotState.Ruined`, must already be excluded before calling this — mechanic 5's
    /// whole appeal is that destruction removes it) — this function does not know about HP or ruin
    /// state, only geometry and the two fields `siege-obstacles` already carries as data.
    /// </summary>
    public static int BestCoverMilli(
        Actions.GridPos target,
        IEnumerable<(Actions.GridPos Cell, int CoverRadius, int CoverPowerMilli)> liveObstacles)
    {
        if (liveObstacles is null) throw new ArgumentNullException(nameof(liveObstacles));
        var best = 1000; // no cover = full power
        foreach (var o in liveObstacles)
        {
            if (o.CoverRadius <= 0) continue;
            if (Actions.GridDistance.Chebyshev(o.Cell, target) > o.CoverRadius) continue;
            best = Math.Min(best, o.CoverPowerMilli);
        }
        return best;
    }

    /// <summary>
    /// Mechanic 6: the single composition point. Long × int × int × int × int, ONE widen (the initial
    /// cast to `long` for `basePower`), FOUR divides by 1000 — one per multiplier, each strictly after
    /// every multiply, never combined into `/ 1_000_000_000_000`. That combined divisor is itself a
    /// magnitude the numerator can overflow before ever reaching — docs/architecture/numeric-types.md rule 4's own "the naive
    /// simplification is the bug" case, made concrete.
    /// </summary>
    public static long ComposedPower(long basePower, int coverMilli, int rangeMilli, int obstructionMilli, int meleeLockMilli)
    {
        var afterCover = checked(basePower * coverMilli / 1000);
        var afterRange = checked(afterCover * rangeMilli / 1000);
        var afterObstruction = checked(afterRange * obstructionMilli / 1000);
        return checked(afterObstruction * meleeLockMilli / 1000);
    }
}

/// <summary>§5.17 rule 5, legibility: each factor visible SEPARATELY on the wire, never only the
/// composed product — Relic's most repeated bug class is cover illegibility.</summary>
public readonly record struct ShootingBreakdown(int CoverMilli, int RangeMilli, int ObstructionMilli, int MeleeLockMilli, long FinalPower);
