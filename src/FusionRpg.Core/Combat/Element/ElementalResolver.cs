using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Combat.Element;

/// <summary>
/// The battle engine's elemental sub-module: the one place a contest between two actors is decided
/// from their <c>omni + element</c> totals.
///
/// <para><b>The owner's statement of it (2026-09-17).</b> <i>"Action have elementals (they will build
/// become a matrix) then resolve the stats base on the matrix… when we resolve attack dodge, block,
/// parry, absorb, reflect they basicly resolve from elemental matrix with omni is the base. So
/// elemental resolver should be a sub module of battle engine and it play almost role in the damage
/// calculation and shield mechanism."</i></para>
///
/// <para><b>What this type does NOT own, deliberately.</b> It does not own the additive read — that is
/// <see cref="CombatDerivedReader"/>, which has resolved nine families as <c>omni + element</c> since
/// before this module existed, and a second implementation of it here would be the exact defect
/// `solid-remediation` exists to remove. It does not own the logistic curve either — that is
/// <c>ResistanceEvaluator.Sigmoid</c>, reached through <see cref="CombatProbability.Sigmoid"/>. It does
/// not own the matchup table — that is <see cref="ElementRingMatrix"/>. This type owns the **contest
/// shapes**: the two ways a pair of totals becomes an outcome, each of which was written out by hand at
/// every call site before.</para>
///
/// <para><b>The three rules, transferred not invented.</b> From chaos-backend-service's element-core
/// docs, which this stack was already built on.</para>
/// </summary>
public static class ElementalResolver
{
    /// <summary>
    /// <b>Rule 1 — omni is additive, never multiplicative.</b>
    ///
    /// <para><i>"Omni stats chỉ cộng, không nhân"</i>. Multiplying omni by the element term snowballs a
    /// one-trick build: an actor who stacks one element would have its omni floor amplified by the very
    /// specialisation it already paid for. Adding them instead makes omni **anti-one-trick baseline
    /// protection** — every actor keeps a floor of capability in every element, and specialising raises
    /// one element above that floor rather than scaling the floor itself.</para>
    ///
    /// <para>This method exists to be the sentence, not the arithmetic. The arithmetic lives in
    /// <see cref="CombatDerivedReader"/>, which every caller already uses; what had no home was the
    /// statement of WHY it is a sum, and a place for a test to assert it stays one.</para>
    /// </summary>
    public static double Total(double omni, double element) => omni + element;

    /// <summary>
    /// <b>Rule 2 — a contest is a sigmoid on the difference of two totals.</b>
    ///
    /// <code>p = sigmoid((total_attacker − total_defender) / scale)</code>
    ///
    /// <para>⚠️ <b>The [0,1] bound here is MATHEMATICAL, not a gameplay cap.</b> The source says so
    /// outright — <i>"do sử dụng sigmoid (ràng buộc toán học, không phải cap gameplay)"</i> — and the
    /// distinction is load-bearing in this repo, which bans hard progression ceilings and audits for
    /// them. A sigmoid is not a ceiling: no amount of stat ever stops mattering, each point simply buys
    /// a smaller share of the remaining gap. Nothing here is to be "freed" by a later cap sweep.</para>
    ///
    /// <para>Difference-based, so it reads Θ rather than P(Θ) — the one-power-ladder rule: contests read
    /// the linear ladder, magnitudes read the quadratic one.</para>
    /// </summary>
    public static double Contest(double attackerTotal, double defenderTotal, double scale) =>
        CombatProbability.Sigmoid(attackerTotal - defenderTotal, scale);

    /// <summary>
    /// <b>The other contest shape: linear from zero, for rates already expressed as a share.</b>
    ///
    /// <para>Deliberately NOT a sigmoid, and the reason is recorded at both places that hand-wrote it.
    /// <c>CombatPolicy.ReflectRateScale</c>'s own comment: a sigmoid is 0.5 at delta 0, so an actor with
    /// no reflect stat at all would reflect half the time — <i>"a new default nobody chose"</i>, and a
    /// violation of NoGoldensMoveAtZero. The same reasoning is written again above the parry/block
    /// bands in <c>OverlayCombatCalculator</c>. Linear-from-zero gives 0 at parity, which is what an
    /// unstatted actor should get.</para>
    ///
    /// <para>Clamped to [0,1] because it is a probability and the linear form, unlike the sigmoid, has
    /// no bound of its own. Same note as above: a bound on a probability is arithmetic, not a
    /// progression ceiling — the underlying rate is uncapped and keeps mattering against a defender who
    /// also stacks resistance.</para>
    /// </summary>
    public static double RateFromZero(double attackerRate, double defenderResist, double scale) =>
        Math.Clamp(Math.Max(0.0, attackerRate - defenderResist) / scale, 0.0, 1.0);

    /// <summary>
    /// <b>Rule 3 — the interaction matrix stays intransitive.</b> No element beats every other, so no
    /// single-element build dominates. Consumed from <see cref="ElementRingMatrix"/> rather than
    /// re-derived here: the ring and the light/dark mutual counter are already the one table, and
    /// combat is told outright not to duplicate it.
    ///
    /// <para>Exposed through this type so the resolver is a complete statement of the elemental rules
    /// and a test can assert intransitivity at the seam that claims it, rather than only at the table.</para>
    /// </summary>
    public static ElementMatchupRelation Relation(ElementTypeId attacker, ElementTypeId defender) =>
        ElementRingMatrix.GetRelation(attacker, defender);
}
