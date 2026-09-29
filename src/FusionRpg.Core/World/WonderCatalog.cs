namespace FusionRpg.Core.World;

/// <summary>How far a Wonder's effect and existence-cap reach (loam-relics-and-wonders `wonder-structure`).
/// Sector/Empire are live this wave. World/Multiverse are named per the owner's own "reserve vocabulary
/// for extend later" instruction but are refused by <see cref="StructureCatalog.Validate"/> — see the
/// spec's §Design 5. Never select/construct a World or Multiverse row from any live code path; the
/// member exists only so a future spec can cite it without re-deriving the ladder.</summary>
public enum WonderScope
{
    Sector,
    Empire,

    /// <summary>Reserved. Needs the same LoamProduction.For faction-input plumbing as Empire, looped
    /// over every faction in the world, plus a named wonder-race mitigation (decisions.md, Loam relics
    /// and wonders SSOT) — not this program's wave. Refused by Validate today.</summary>
    World,

    /// <summary>Reserved. Needs a wholly new player-scoped, world-surviving ledger with no precedent
    /// anywhere in this codebase (loam-relics-and-wonders-ideal.md §The owner's fourth-pass framing).
    /// Refused by Validate today.</summary>
    Multiverse
}

/// <summary>Scarcity axis, orthogonal to WonderScope by construction — a Sector-scope Wonder can be
/// Common or Unique independently of an Empire-scope one, exactly the way CreatureRarity and a creature's
/// other closed axes stay independent (AGENTS.md closed-vocabulary table).</summary>
public enum WonderRarity
{
    /// <summary>No existence cap — WonderPolicy.ExistenceCapFor returns long.MaxValue, the same
    /// "dynamic headroom, never a constant" idiom RpgStore.MaxSoulAwardFrom already uses
    /// (decisions.md, Caps (project-wide)). Never a silent hard cap of 1.</summary>
    Common,

    /// <summary>Capped by a tunable count per WonderScope (gk-core/data/tuning/loam-relics-wonders.v1.json) —
    /// never a hard-coded 1 (AGENTS.md "no hard progression ceilings"). Which scope-unit the cap
    /// counts against (per sector / per faction) is module 4's (`wonder-build-flow`) own enforcement
    /// concern; this module ships only the tunable lookup.</summary>
    Unique
}

/// <summary>What a Wonder's effect boosts. Closed, reviewed-growth, mirroring StatusCatalogBootstrap's
/// "a new member is a reviewed addition, never an open string" discipline (StatusCatalogBootstrap.cs).
/// LoamGenerationRate is the only member Validate accepts this wave.</summary>
public enum WonderEffectKind
{
    LoamGenerationRate,

    /// <summary>Reserved — boosts a defending unit's combat power in a warded/garrisoned sector. Needs
    /// a real sector-scoped combat-power read at world/siege scope, not audited at spec time
    /// (loam-relics-and-wonders-ideal.md §The effect-kind vocabulary). ⛔ When eventually designed,
    /// must contribute via ActorHub (IActorStatSubsystem / registered atom reader) or consume Hub
    /// output only — never a private per-sector combat fold (AGENTS.md "One ActorHub compose / one
    /// read"). Refused by Validate today.</summary>
    DefensePower,

    /// <summary>Reserved — an aura/buff for defending units, not just a flat stat add. Needs a
    /// world-map-to-battle aura delivery path that does not exist today. Same ActorHub warning as
    /// DefensePower applies once designed. Refused by Validate today.</summary>
    AuraGrant,

    /// <summary>Reserved — a buff reaching every sector/legion the faction owns. WorldFaction.ScopeModifierMilli
    /// is the storage; each consumer is its own wiring task. Refused by Validate today.</summary>
    EmpireBuff
}

/// <summary>One thing a Wonder does. A row's WonderEffects list holds one or more of these — v1
/// content authors exactly one, Kind = LoamGenerationRate (Validate enforces the Kind restriction,
/// not the count restriction, so a future wave can add a second live Kind to an existing Wonder's
/// list without a schema change).</summary>
public sealed record WonderEffectDef
{
    public WonderEffectKind Kind { get; init; }

    /// <summary>Must equal the owning StructureDef.WonderScope — Validate enforces this.
    /// Carried on the effect itself, not merely inferred from the parent row, so a WonderEffectDef
    /// travels as a self-describing unit into whatever consumer wonder-effect-empire (module 3)
    /// builds, without that consumer needing to also thread the parent StructureDef through.</summary>
    public WonderScope Scope { get; init; }

    /// <summary>Per-mille magnitude, tunable-by-content (seed-authored).
    /// <para><b>For a Sector-scope LoamGenerationRate effect, this field is DESCRIPTIVE, not the
    /// consumed magnitude</b> — the real number `LoamProduction.For` reads is this same row's own
    /// `YieldMultiplierMilli`/`FlatYieldPerTurn` (already-existing StructureDef fields, unchanged),
    /// because a Sector-scope Wonder sits on one slot in one sector and the existing per-slot fields
    /// already reach exactly that sector, zero new plumbing. `ValueMilli` here should read the same
    /// delta a content author put in those fields, for tooltip/UI consistency — Validate does not
    /// cross-check the two this wave (a future content-QA pass, not an architecture requirement).</para>
    /// <para><b>For an Empire-scope LoamGenerationRate effect, this field IS the consumed magnitude</b>
    /// — there is no existing "reaches every sector this faction owns" field to reuse (`LoamProduction.For`
    /// only ever reads the one sector passed to it). `ValueMilli` is what module 3 (`wonder-effect-empire`)
    /// reads off every built Empire-scope Wonder for a faction and sums (decisions.md's locked SUM rule)
    /// into that faction's `WorldFaction.ScopeModifierMilli`.</para></summary>
    public long ValueMilli { get; init; }
}

/// <summary>Every tunable Wonder constant, mirroring LoamPolicy's Configure/static-holder shape
/// exactly (LoamPolicy.cs). Values live in gk-core/data/tuning/loam-relics-wonders.v1.json.</summary>
public static class WonderPolicy
{
    static Loam.WonderTuning? _tuning;

    /// <summary>Host-only (Server startup, or a test's inline construction).</summary>
    public static void Configure(Loam.WonderTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static Loam.WonderTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "WonderPolicy.Configure(...) has not run. Every Wonder rule reads " +
        "data/tuning/loam-relics-wonders.v1.json — there is no built-in default to fall back to.");

    /// <summary>Common = uncapped (long.MaxValue, the RpgStore.MaxSoulAwardFrom "dynamic headroom"
    /// idiom, decisions.md Caps (project-wide) — never a silent hard cap). Unique = a tunable count
    /// per scope. World/Multiverse throw — Validate already refuses them as a WonderScope on any
    /// catalog row, so this method is never called with either in practice; it still names them
    /// loudly rather than returning a made-up number if a future caller ever tries.</summary>
    public static long ExistenceCapFor(WonderScope scope, WonderRarity rarity)
    {
        if (rarity == WonderRarity.Common) return long.MaxValue;
        return scope switch
        {
            WonderScope.Sector => Tuning.UniqueExistenceCap.Sector,
            WonderScope.Empire => Tuning.UniqueExistenceCap.Empire,
            _ => throw new ArgumentOutOfRangeException(nameof(scope),
                $"WonderScope.{scope} has no registered existence-cap tunable (reserved, unregistered).")
        };
    }
}
