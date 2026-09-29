using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Combat.Element;

/// <summary>
/// Normative STR/WEK/NEU table — ring fire → ice → earth → air → fire, plus the
/// light ⇄ dark mutual counter (both STR vs each other; neutral vs the ring four).
/// Single SSOT for matchup relations; combat must not duplicate this table.
/// The default arm is fail-open Neutral — the roster-generated golden matrix test
/// is what guarantees no pair silently falls through.
/// </summary>
public static class ElementRingMatrix
{
    /// <summary>
    /// Reads the roster's combat matrix (E18). It was a <c>switch</c> over the enum; the relations
    /// are now rows, so a seventh element is rows plus regeneration rather than an edit here.
    /// <c>Same</c> stays a code distinction — the table carries relations between <i>different</i>
    /// elements, and "attacking your own element" is a property of the pair, not a row.
    /// </summary>
    public static ElementMatchupRelation GetRelation(ElementTypeId attacker, ElementTypeId defender)
    {
        if (attacker == defender)
            return ElementMatchupRelation.Same;

        return ElementTable.Current.CombatUnit(ElementTable.IdOf(attacker), ElementTable.IdOf(defender)) switch
        {
            > 0 => ElementMatchupRelation.Strong,
            < 0 => ElementMatchupRelation.Weak,
            _ => ElementMatchupRelation.Neutral,
        };
    }

    /// <summary>Relation share added to 1.0 multiplier: STR +k, WEK −k, NEU/SAME 0.</summary>
    public static double RelationShare(ElementMatchupRelation relation) =>
        relation switch
        {
            ElementMatchupRelation.Strong => ElementMatchupPolicy.MatchupShareK,
            ElementMatchupRelation.Weak => -ElementMatchupPolicy.MatchupShareK,
            _ => 0.0
        };
}
