using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Combat.Shield;

/// <summary>
/// Shield-owned matchup table — attack element vs the shield's element. Returns the UNIT
/// relation (+1 strong / −1 weak / 0 neutral or same); K is applied exactly once in ShieldMath,
/// unlike ElementRingMatrix.RelationShare which bakes K in (shield-system-spec.md §2.4).
/// v1 content is seeded identical to the ring + light/dark mutual counter but is independently
/// editable — diverging from the seed is an Ask-first balance decision (spec §8). The default
/// arm is fail-open 0; the roster-generated golden is what guarantees no pair falls through.
/// </summary>
public static class ShieldElementMatrix
{
    /// <summary>
    /// Reads the roster's shield matrix (E18) — a separate table from the combat ring, seeded
    /// identical and independently editable. Verified 2026-08-22: the two are the same in all 36
    /// pairs today, including light ⇄ dark, so the asymmetry the atom spec warned about is not there.
    /// </summary>
    public static int RelationUnit(ElementTypeId attacker, ElementTypeId shieldElement)
    {
        if (attacker == shieldElement)
            return 0;

        return ElementTable.Current.ShieldUnit(
            ElementTable.IdOf(attacker), ElementTable.IdOf(shieldElement));
    }
}
