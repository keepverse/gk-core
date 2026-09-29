using FusionRpg.Core.Effects.Atoms.Power;

namespace FusionRpg.Core.Actions;

/// <summary>
/// action-enrich `action-base` (spec-action-base.md §The read):
/// <c>BaseOverlayDamage = basePowerMilli(action, effectiveRung) × P(Θ) / 1000</c>.
///
/// <para>A MAGNITUDE, not a ratio — <c>long</c>, <c>checked</c>, and the single <c>/1000</c> happens
/// LAST (docs/architecture/numeric-types.md numeric rules 1–3). Both operands are already <c>long</c>, so the multiply is widened
/// by construction; a <c>long</c> overflow throws rather than wrapping.</para>
/// </summary>
public static class ActionBaseMath
{
    public static long BasePerHit(long basePowerMilli, long pTheta) =>
        checked(basePowerMilli * pTheta) / PowerMath.One;
}
