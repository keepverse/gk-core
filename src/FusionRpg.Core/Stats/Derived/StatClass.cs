namespace FusionRpg.Core.Stats.Derived;

/// <summary>
/// Does this channel need a counterpart? See docs/architecture/derived-stats/spec-stat-taxonomy.md §2.1.
/// Orthogonal to <see cref="UnitClass"/> (§2.5) — never redefine a third scheme instead of using both.
/// </summary>
public enum StatClass
{
    /// <summary>Two actors' values meet in one roll or one delta. Pair required, neither half capped.</summary>
    Contest,

    /// <summary>Both actors want the same direction; advantage is being ahead. Pair forbidden — the
    /// opponent's own value is already the counter.</summary>
    Race,

    /// <summary>One actor's own capacity or rate. No pair; depletion is the limit.</summary>
    Pool,

    /// <summary>Modifies a quantity that is contested downstream. Pair is inherited from what it feeds
    /// (combat-damage-ssot.md §6.7 decides which quantity that is).</summary>
    Feeder
}

/// <summary>
/// What arithmetic is this channel, and how does it render? Mirrors the ten-class unit ledger in
/// docs/design/spec-magnitude-and-units.md §3 — this enum is that ledger's first code materialization,
/// not a second scheme. Do not add an eleventh member here without updating that spec first.
/// </summary>
public enum UnitClass
{
    /// <summary>Flat magnitude, e.g. +12 fire power. No context part.</summary>
    GameUnits,

    /// <summary>Flat magnitude per second, e.g. +3 shield hp/s. No context part.</summary>
    GameUnitsPerSecond,

    /// <summary>Uncapped input to a sigmoid probability, e.g. crit rate. Context part is an estimate.</summary>
    SigmoidPoints,

    /// <summary>Uncapped input to a sigmoid-shaped multiplier, e.g. crit damage. Saturates; context
    /// part must state the ceiling.</summary>
    SigmoidMultiplierPoints,

    /// <summary>Raw status potency magnitude. Context part suppressed — spec-magnitude-and-units.md §4.3.</summary>
    StatusPotencyPoints,

    /// <summary>A per-mille ratio — Increased sums then applies once, More multiplies separately.</summary>
    PerMilleRatio,

    /// <summary>A duration in milliseconds.</summary>
    Milliseconds,

    /// <summary>A discrete count, e.g. bullets or max targets.</summary>
    Count,

    /// <summary>Present/absent — never a number.</summary>
    Flag,

    /// <summary>An index on the Θ / P(Θ) power ladder, not a magnitude. The tenth class, added
    /// 2026-08-24 — spec-magnitude-and-units.md §3.2. Context part is exact, not an estimate.</summary>
    LadderIndex,

    /// <summary>An aptitude's own point investment (e.g. "Might 55") — class-system/spec-primary-
    /// stats.md §3.2, authorised 2026-08-26. The `LadderIndex` precedent applies exactly: PS-3 reads
    /// an aptitude two ways (contest linearly, magnitude through `P(Θ)`), so both the point count and
    /// its effect matter. Unlike `LadderIndex`, the context part is an ESTIMATE, not exact — `share`
    /// divides by the actor's whole allocation, so "Might 55" alone buys nothing determinate. Shown
    /// only on a surface with a real allocation (actor sheet); suppressed elsewhere, same rule as
    /// `StatusPotencyPoints` (§4.3) — a number computed against an allocation the player has not made
    /// is not a hedge, it is a fiction.</summary>
    AptitudePoints,

    /// <summary>An uncapped point delta that feeds a RECIPROCAL-shaped bounded factor — the reciprocal
    /// analog of `SigmoidPoints`' sigmoid-shaped one. class-system/spec-unit-class-close.md §3.3/§3.5,
    /// authorised 2026-08-26: verified reader is `CombatDerivedReader` + `OverlayCombatCalculator`'s
    /// mitigation chain — `PierceFactor(d,s) = 1/(1+max(0,d)/s)` and its amplification-side mirror
    /// `AmpFactorReciprocal`, both asymptotic rather than linear or sigmoid. Covers `combat.penetration`
    /// `/absorption/amplification/reduction`.</summary>
    ReciprocalPoints,

    /// <summary>
    /// A whole count of loam — the world economy's resource. **The thirteenth class**, authorised
    /// 2026-09-04 by the world-numbers program (W37/W38) and specified in
    /// `spec-magnitude-and-units.md` §3.
    ///
    /// <para>No context part: it is a plain whole count, and a flow's own sign/arrow/colour belong to
    /// `LoamFigure`'s composition rather than to this class. Covers `WorldSectorDto`'s loam production
    /// / upkeep / net / stock blocks and `StructureDef.Cost` / `LoamPolicy.WellCost` /
    /// `WaystationCost` / `GranaryCost` — those fields were named `…CostMilli` and said milli while
    /// carrying whole loam, which is precisely the misreading `BuildResolver.cs:101,115` exists
    /// downstream of; renamed off the misleading suffix world-map W57 (2026-09-05).</para>
    ///
    /// <para>⛔ **Added here 2026-09-04, later than it should have been.** The doc and the TypeScript
    /// `UnitClass` union both gained it on the authorisation date; this enum did not, so
    /// `UnitClassContractParityTests` — the guard written for exactly this "a class added to one side
    /// with the other forgotten" case — sat red. It did its job; nobody read it.</para>
    /// </summary>
    LoamUnits
}
