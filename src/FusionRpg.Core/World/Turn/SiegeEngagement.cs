using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.World.Movement;

namespace FusionRpg.Core.World.Turn;

/// <summary>
/// base-defense `siege-engagement` (module 20, spec-siege-engagement.md), decision 24: a siege is a
/// multi-turn loop — one map turn resolves one "engagement" (a district assault), and the siege
/// continues across turns until the Core falls or the assault breaks. This module adds the vocabulary
/// for "the engagement ended inconclusively and that is normal" plus a derived, never-stored
/// "is this sector under siege" predicate — deliberately reusing what already ships rather than
/// inventing either.
///
/// <para><b>Real, named, un-started gap this module does NOT fix</b>: decision 24's own core mechanic
/// — "a siege spans turns because engagements repeat" — does not actually work end to end today.
/// `DistrictAssaultPhase.Run` only ever fires for an explicit `WorldCommandKinds.Assault` command
/// (`DistrictAssaultPhase.cs:33`); a continuing siege with no fresh order every turn instead falls to
/// `ContactResolver.SectorContacts` (`World/Movement/MovementPhase.cs`), which unconditionally builds a
/// `BattleKinds.Sector` request for any two hostile forces sharing a sector — never `BattleKinds.District`
/// — confirmed by reading that file directly. `DistrictAssaultResolver`'s own delegation guard then
/// correctly (per its own contract) refuses that Sector-kind request outright (`placeholder-battle-hub`,
/// T20: no engine resolves a non-district kind, so no winner is invented for one either) — a
/// second-and-later turn of the same siege now honestly does nothing instead of continuing on the real
/// board, where before this fix it silently resolved as an ordinary open-field placeholder fight.
/// Neither is decision 24's real fix. Fixing this needs
/// `MovementPhase`/`ContactResolver` to detect an ongoing district siege (via <see cref="IsUnderSiege"/>
/// below) and emit `BattleKinds.District` with a real `BoardProjection` instead — a design call that
/// touches a phase neither this module nor `siege-resolver`'s own task list currently names, and is
/// named here rather than guessed at under time pressure.</para>
/// </summary>
public enum EngagementExit
{
    /// <summary>The defender's Core fell — the siege ends, the base changes hands.</summary>
    CoreTaken,

    /// <summary>Every animate attacker is dead — the siege ends, the base holds.</summary>
    AssaultBroken,

    /// <summary>Neither side's objective was met at the engagement's own horizon (decision 24: the
    /// engagement ends, the siege does not) — the everyday case. The siege continues next turn.</summary>
    Spent,

    /// <summary>The attacker left the field deliberately, whole (audit F5) — distinct from
    /// <see cref="AssaultBroken"/>, which is a defeat. No rout penalty.</summary>
    Withdrawn,
}

public static class SiegeEngagement
{
    /// <summary>
    /// Derived, never stored (§3) — a thin wrapper over the already-shipped
    /// <see cref="SupplyGraph.IsBesieged"/>, never a second derivation. An unowned sector (no faction
    /// to be besieged against) is never under siege.
    /// </summary>
    public static bool IsUnderSiege(WorldState world, string sectorId)
    {
        var sector = world.Sectors.FirstOrDefault(s => string.Equals(s.SectorId, sectorId, StringComparison.Ordinal));
        return sector is not null
               && sector.OwnerFactionId is { } owner
               && SupplyGraph.IsBesieged(world, sectorId, owner);
    }

    /// <summary>
    /// Maps <see cref="SiegeObjective"/>'s three-way outcome, plus the attacker's own
    /// <see cref="BattleSideOutcome.Withdrawn"/> flag, onto the four-way <see cref="EngagementExit"/> —
    /// splitting <see cref="SiegeOutcomeKind.AssaultBroken"/> (which conflates "every attacker dead"
    /// and "the attacker withdrew" in its own doc comment) into <see cref="EngagementExit.AssaultBroken"/>
    /// vs <see cref="EngagementExit.Withdrawn"/> by checking which one actually happened.
    /// </summary>
    public static EngagementExit ExitFor(SiegeOutcomeKind kind, IReadOnlyList<BattleSideOutcome> sides, string attackerEntityId) => kind switch
    {
        SiegeOutcomeKind.CoreTaken => EngagementExit.CoreTaken,
        SiegeOutcomeKind.Inconclusive => EngagementExit.Spent,
        SiegeOutcomeKind.AssaultBroken => IsWithdrawn(sides, attackerEntityId) ? EngagementExit.Withdrawn : EngagementExit.AssaultBroken,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "SiegeEngagement.ExitFor: unknown SiegeOutcomeKind."),
    };

    static bool IsWithdrawn(IReadOnlyList<BattleSideOutcome> sides, string attackerEntityId) =>
        sides.FirstOrDefault(s => string.Equals(s.EntityId, attackerEntityId, StringComparison.Ordinal))?.Withdrawn == true;
}
