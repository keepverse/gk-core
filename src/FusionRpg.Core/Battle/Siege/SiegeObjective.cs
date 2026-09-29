namespace FusionRpg.Core.Battle.Siege;

/// <summary>
/// base-defense `siege-objective` (spec-siege-objective.md), decision 1: how a siege ends. The central
/// defense area IS the objective — "lose it and you lose the base. Capture requires killing every
/// troop standing in it." Not a wipe: a defender who still has soldiers in the outer ground has not
/// lost, and an attacker who has cleared the Core has won even with enemies behind them.
/// </summary>
public enum SiegeOutcomeKind
{
    /// <summary>Every ANIMATE defender in the Core zone is dead (or withdrawn). The base falls.</summary>
    CoreTaken,

    /// <summary>Every animate attacker is dead or withdrawn. The base holds.</summary>
    AssaultBroken,

    /// <summary>Neither, at the horizon. Decision 24: the engagement ends, the siege does not.</summary>
    Inconclusive
}

/// <summary>
/// One combatant's state as the objective needs to see it — deliberately decoupled from
/// `BattleActorSetup`/`IBattleView` so this module has no dependency on how a caller resolves position
/// or liveness (board wiring is `siege-resolver`'s job, a later module); it only needs these five facts.
/// </summary>
public readonly record struct SiegeCombatant(
    string ActorKey, string Side, bool Alive, bool Withdrawn, bool InCore, CombatantKind Kind);

/// <summary>
/// The win-condition evaluator. Pure and stateless — call it once per round boundary (never per
/// action, or the order of two simultaneous deaths could decide the winner); the caller owns when that
/// boundary is, this module only owns what the answer is at that instant.
/// </summary>
public static class SiegeObjective
{
    public static SiegeOutcomeKind Evaluate(
        IReadOnlyList<SiegeCombatant> combatants, string defenderSide, string attackerSide)
    {
        // Structures are excluded from BOTH conditions (combatant-kind's own Animate filter, restated
        // here) — a wall standing in the Core would make the base uncapturable, and "demolish
        // everything" is not the objective decision 1 describes.
        static bool AnimateActive(SiegeCombatant c) => c.Kind == CombatantKind.Animate && c.Alive && !c.Withdrawn;

        // Checked first: an empty Core (zero animate defenders standing in it, whether because they
        // died or because there were never any) has, by decision 1's own literal wording, already had
        // "every troop standing in it" killed — a base with no garrison in its heart has already
        // fallen. Zone-restricted to the DEFENDER's side only; a defender who still has soldiers in the
        // outer ground has not lost (that is the whole reason the district's geometry matters).
        var coreHeld = combatants.Any(c => string.Equals(c.Side, defenderSide, StringComparison.Ordinal) && c.InCore && AnimateActive(c));
        if (!coreHeld) return SiegeOutcomeKind.CoreTaken;

        // Not zone-restricted: the attacker does not need to BE in the Core to still be a threat, only
        // to have any animate member left standing anywhere on the field.
        var attackerStanding = combatants.Any(c => string.Equals(c.Side, attackerSide, StringComparison.Ordinal) && AnimateActive(c));
        if (!attackerStanding) return SiegeOutcomeKind.AssaultBroken;

        return SiegeOutcomeKind.Inconclusive;
    }
}
