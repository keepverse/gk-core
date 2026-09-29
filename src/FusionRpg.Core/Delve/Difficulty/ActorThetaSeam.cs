using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Difficulty;

/// <summary>
/// D1.22 — `Θ_actor` composition, named as the wiring gap it is (spec-difficulty-ladder.md §7). The
/// contest side (accuracy/dodge/crit/critResist, <see cref="BattleRuleset"/>) already reads `Θ`
/// directly (PS-3: contests read `Θ`) — but the squad's `Θ` today is the specimen's own level
/// (`BattleStatComposer.cs:108` <c>int theta = setup.Index;</c>; `BattleModels.cs:24`
/// <c>Index => Level</c>), never a composed `Θ_actor` from the party's own gear and stats. The
/// proposed seam — <see cref="FusionRpg.Core.Power.IPowerIndexProvider.ActorIndex"/> feeding a new
/// field on `BattleActorSetup` — is `delve-battle-profile`'s and `power-index`'s to land (§7: "the
/// seam this module proposes, not builds"); this file is the pure contest math that seam will call
/// into, proven against §7's own worked table so that on the day the field lands, only the CALLER
/// changes — nothing here invents a second curve.
///
/// <b>Fallback until the seam lands:</b> the gap between `Θ_content` and the specimen's `Θ_actor`
/// closes only through specimen levels — 35 specimen levels per creature per +35 `Θ`
/// (`ssot-power-scale.md` §10 row 27's cost ladder) — never a `bandDelta` tied to `Θ_actor` (Last
/// Epoch corruption, ideal §11.2); `RungValidator`'s actor-axis name ban is the enforcement.
/// </summary>
public static class ActorThetaSeam
{
    /// <summary>One side's chance to land a normal hit on the other — <see cref="BattleRuleset.BaseAccuracy"/>
    /// vs <see cref="BattleRuleset.BaseDodge"/> through the shipped <see cref="CombatProbability.Sigmoid"/>.
    /// No new curve, no new scale: `accuracyScale` is `stats.v1.json`'s own key.</summary>
    public static double HitChance(int attackerTheta, int defenderTheta) =>
        CombatProbability.Sigmoid(
            BattleRuleset.BaseAccuracy(attackerTheta) - BattleRuleset.BaseDodge(defenderTheta),
            CombatProbabilityPolicy.AccuracyScale);

    /// <summary>Same shape for the crit contest — <c>critRateScale</c>, its own tunable, never
    /// borrowed from <see cref="HitChance"/>'s scale even though both read 100 today.</summary>
    public static double CritChance(int attackerTheta, int defenderTheta) =>
        CombatProbability.Sigmoid(
            BattleRuleset.BaseCritRate(attackerTheta) - BattleRuleset.BaseCritResist(defenderTheta),
            CombatProbabilityPolicy.CritRateScale);

    /// <summary>§7's four-column read for one `(Θ_content, Θ_actor)` pair — "our" is the party
    /// attacking the room's specimen, "their" is the specimen attacking the party. Every term is a
    /// function of `gap = Θ_content − Θ_actor` alone (the two base terms' own `Θ` cancels) — proven
    /// by the property test, not asserted here.</summary>
    public static (double OurHit, double TheirHit, double OurCrit, double TheirCrit) Contest(int contentTheta, int actorTheta) =>
        (
            HitChance(actorTheta, contentTheta),
            HitChance(contentTheta, actorTheta),
            CritChance(actorTheta, contentTheta),
            CritChance(contentTheta, actorTheta)
        );
}
