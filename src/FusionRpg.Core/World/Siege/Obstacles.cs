namespace FusionRpg.Core.World.Siege;

/// <summary>
/// base-defense `siege-obstacles` (spec-siege-obstacles.md §5.18): FIVE rows, five distinct decisions
/// — the authoring rule is that a row exists only because cutting it removes a decision no other row
/// can produce. A sixth kind must name its own decision or it is the second vocabulary the program's
/// own §2 rule 10 forbids. Obstacles are <see cref="StructureDef"/> facets, not a parallel system —
/// they have HP (`structure-state`), occupy cells, and are destructible like any other structure.
/// </summary>
public enum ObstacleKind
{
    /// <summary>Not an obstacle — an ordinary building (well, granary, emplacement's own base kind…).</summary>
    None,

    /// <summary>Occupiable AND passable. A flat `combat.dodge.omni` delta on the occupant — the
    /// decision it creates: "where is it worth standing still?"</summary>
    Trench,

    /// <summary>Not occupiable. Blocks movement AND fire. Destructible — razing it is a legitimate
    /// attacker action. The decision: "which routes exist at all?" A moat (laboured) is a Rampart —
    /// "a cell you cannot enter and cannot stand on IS a wall. Identical verbs."</summary>
    Rampart,

    /// <summary>Neither blocks nor covers. Multiplies the STAMINA cost of entering the cell — never
    /// movement cost, or it becomes a second `Rough`. The decision: "is the short route worth the
    /// stamina?"</summary>
    Wire,

    /// <summary>Damage on entry, single-use, ignores cover, REVEALED to both sides (audit F9 — a
    /// hidden mine is a coin flip, a visible one is a denied cell the attacker must pay to cross or
    /// route around). The decision: "open ground or covered ground?" — the only obstacle that punishes
    /// the safe-looking cell.</summary>
    Mine,

    /// <summary>A building, not an obstacle mechanically — garrisoned (`combatant-kind` §4), acts
    /// through its occupant, who gets high cover plus a ranged action. The decision: "is a body better
    /// spent shooting or standing?" — real only because the field cap (`siege-objective`) makes bodies
    /// scarce.</summary>
    Emplacement
}

/// <summary>
/// base-defense `siege-obstacles` §7, decision 27, §5.24: which of the four acquisition paths can
/// produce a structure. A `StructureDef`'s own set is a SUBSET of these — `none` (an empty set) is
/// illegal, validated at load, since a structure no path can produce is a catalog row that can never
/// appear on a board.
/// </summary>
public enum AcquisitionPath
{
    /// <summary>Constructed by a legion action, on the spot, at the shared placement validator's cost.</summary>
    Built,

    /// <summary>Prefabricated elsewhere and assembled here — faster, costs the prefabricated goods.</summary>
    Assembled,

    /// <summary>Conjured by a summoning effect/ritual rather than built by hand.</summary>
    Summoned,

    /// <summary>Dug or raised by hand over time — a moat (Rampart) is the canonical laboured row.</summary>
    Laboured
}

/// <summary>
/// base-defense `siege-obstacles` §5: what kind of hit this is, for `siege-cover`'s own
/// (damage source × cover type) matrix — a data shape rather than a scalar precisely because a row
/// needs to answer "does this cover type apply to THIS source." This module declares only the value it
/// owns and needs: <see cref="Entry"/>, a Mine's damage-on-entry, which ignores cover unconditionally
/// (every cover row against it is 0). `siege-cover` (a later, level-5 module) is the one that defines
/// its own matrix and may extend this enum with the melee/ranged/spell distinctions its own spec names
/// — not guessed at here.
/// </summary>
public enum DamageSourceKind
{
    /// <summary>A Mine's damage on entry (§5.18). Ignores cover unconditionally.</summary>
    Entry
}

/// <summary>
/// base-defense `siege-obstacles` §4: the Wire reader — a per-mille multiplier on the STAMINA cost of
/// entering a cell, never movement cost (`siege-pathing`'s own `MoveCosts` reads open/rough terrain
/// only and must stay untouched by this — proven by an import-scan test, not just this comment).
/// Movement already costs stamina elsewhere in the action-cost system; this is the multiplier a future
/// caller applies to that existing per-cell stamina charge, never a second cost of its own.
/// </summary>
public static class WireStamina
{
    /// <summary>Divide by 1000 last, exactly once; <c>checked</c> — the multiplier is a magnitude a
    /// balance pass raises without an upper bound (a 5000‰ wire is legal), per AGENTS.md's
    /// no-hard-ceilings rule.</summary>
    public static long ApplyEntryMultiplier(long baseStaminaCost, int entryStaminaMultiplierMilli)
    {
        if (baseStaminaCost < 0) throw new ArgumentOutOfRangeException(nameof(baseStaminaCost));
        if (entryStaminaMultiplierMilli < 0) throw new ArgumentOutOfRangeException(nameof(entryStaminaMultiplierMilli));
        return checked(baseStaminaCost * entryStaminaMultiplierMilli / 1000);
    }
}
