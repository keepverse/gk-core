using FusionRpg.Core.Combat.Element;
namespace FusionRpg.Core.Stats.Derived;

/// <summary>Known derived channel id patterns — see actor-hub-ssot.md §3.</summary>
public static class DerivedStatChannels
{
    /// <summary>
    /// Channels the design has RETIRED. Owner ruling 2026-09-18 retired `progression.bonus.atk`
    /// ("my design don't have atk, it only have power and defense, seem like atk is adundant need to
    /// retire"). A retired id is <b>not registered</b>, is <b>never composed</b>, and is <b>DROPPED —
    /// with a count — wherever historical tuning names it</b> (`AptitudeTuning.DroppedRetiredEdges`),
    /// because published tuning versions are immutable and must stay loadable.
    ///
    /// <para><b>Closed vocabulary: adding an id is a reviewed change, and its size is pinned by
    /// `RetiredChannelsTests`.</b> One owner, not a filter per consumer — `AptitudeResolver` throws on
    /// any edge whose channel is neither registered nor retired, so a typo or an unknown channel still
    /// fails loudly; only a channel the design deliberately retired is dropped.</para>
    ///
    /// <para><b>Retirement is not re-targeting.</b> A consumer decides what a retired id means *for
    /// that consumer*: the aptitude loader DROPS an atk edge (Might and Ferocity already pay
    /// `combat.power`, so converting would double-pay), while a passive-tree node must still grant
    /// something and therefore RE-TARGETS through the tree generator's own `RETIRED_FAMILY_SUCCESSOR`
    /// (`gk-forge/tools/seedsmith/seedsmith/adapters/trees/nodegen/quota.py`). One registry state, two consumer
    /// policies, each owned where its invariant lives.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> Retired = new HashSet<string>(StringComparer.Ordinal)
    {
        "progression.bonus.atk",
    };

    public const string ProgressionBonusMaxHp = "progression.bonus.maxHp";

    // RETIRED 2026-09-18 — `progression.bonus.atk` (solid-enforcement `retire-atk` R4). The id lives
    // in `Retired` above, and NOTHING else: no const, no registration, no producer. The const is
    // deleted outright rather than kept as a deprecated alias, so any future reference fails at
    // compile time instead of resolving to a channel nothing reads.
    public const string ProgressionBonusDefense = "progression.bonus.defense";
    public const string ProgressionBonusArm1 = "progression.bonus.arm1";
    public const string ProgressionBonusArm2 = "progression.bonus.arm2";

    public const string ProgressionPower = "progression.power";
    public const string ProgressionRealm = "progression.realm";

    public const string StatusPowerOmni = "status.power.omni";
    public const string StatusPowerDot = "status.power.dot";
    public const string StatusPowerCc = "status.power.cc";
    public const string StatusPowerContagion = "status.power.contagion";

    public const string StatusResistOmni = "status.resist.omni";
    public const string StatusResistDot = "status.resist.dot";
    public const string StatusResistCc = "status.resist.cc";
    public const string StatusResistContagion = "status.resist.contagion";

    public static string StatusPower(string statusId) => $"status.power.{statusId}";
    public static string StatusResist(string statusId) => $"status.resist.{statusId}";
    public static string StatusImmune(string tag) => $"status.immune.{tag}";
    public static string StatusImmuneReduction(string tag) => $"status.immuneReduction.{tag}";
    public static string StatusExpose(string category) => $"status.expose.{category}";

    public const string CombatPowerOmni = "combat.power.omni";
    public const string CombatPowerFire = "combat.power.fire";
    public const string CombatPowerIce = "combat.power.ice";
    public const string CombatPowerAir = "combat.power.air";
    public const string CombatPowerEarth = "combat.power.earth";
    public const string CombatPowerLight = "combat.power.light";
    public const string CombatPowerDark = "combat.power.dark";

    public const string CombatDefenseOmni = "combat.defense.omni";
    public const string CombatDefenseFire = "combat.defense.fire";
    public const string CombatDefenseIce = "combat.defense.ice";
    public const string CombatDefenseAir = "combat.defense.air";
    public const string CombatDefenseEarth = "combat.defense.earth";
    public const string CombatDefenseLight = "combat.defense.light";
    public const string CombatDefenseDark = "combat.defense.dark";

    public const string CombatCritRateOmni = "combat.crit.rate.omni";
    public const string CombatCritRateFire = "combat.crit.rate.fire";
    public const string CombatCritRateIce = "combat.crit.rate.ice";
    public const string CombatCritRateAir = "combat.crit.rate.air";
    public const string CombatCritRateEarth = "combat.crit.rate.earth";
    public const string CombatCritRateLight = "combat.crit.rate.light";
    public const string CombatCritRateDark = "combat.crit.rate.dark";

    public const string CombatCritResistOmni = "combat.crit.resist.omni";
    public const string CombatCritResistFire = "combat.crit.resist.fire";
    public const string CombatCritResistIce = "combat.crit.resist.ice";
    public const string CombatCritResistAir = "combat.crit.resist.air";
    public const string CombatCritResistEarth = "combat.crit.resist.earth";
    public const string CombatCritResistLight = "combat.crit.resist.light";
    public const string CombatCritResistDark = "combat.crit.resist.dark";

    public const string CombatCritDamageOmni = "combat.crit.damage.omni";
    public const string CombatCritDamageFire = "combat.crit.damage.fire";
    public const string CombatCritDamageIce = "combat.crit.damage.ice";
    public const string CombatCritDamageAir = "combat.crit.damage.air";
    public const string CombatCritDamageEarth = "combat.crit.damage.earth";
    public const string CombatCritDamageLight = "combat.crit.damage.light";
    public const string CombatCritDamageDark = "combat.crit.damage.dark";

    public const string CombatCritResistDamageOmni = "combat.crit.resist.damage.omni";
    public const string CombatCritResistDamageFire = "combat.crit.resist.damage.fire";
    public const string CombatCritResistDamageIce = "combat.crit.resist.damage.ice";
    public const string CombatCritResistDamageAir = "combat.crit.resist.damage.air";
    public const string CombatCritResistDamageEarth = "combat.crit.resist.damage.earth";
    public const string CombatCritResistDamageLight = "combat.crit.resist.damage.light";
    public const string CombatCritResistDamageDark = "combat.crit.resist.damage.dark";

    public const string CombatAccuracyOmni = "combat.accuracy.omni";
    public const string CombatAccuracyFire = "combat.accuracy.fire";
    public const string CombatAccuracyIce = "combat.accuracy.ice";
    public const string CombatAccuracyAir = "combat.accuracy.air";
    public const string CombatAccuracyEarth = "combat.accuracy.earth";
    public const string CombatAccuracyLight = "combat.accuracy.light";
    public const string CombatAccuracyDark = "combat.accuracy.dark";

    public const string CombatDodgeOmni = "combat.dodge.omni";
    public const string CombatDodgeFire = "combat.dodge.fire";
    public const string CombatDodgeIce = "combat.dodge.ice";
    public const string CombatDodgeAir = "combat.dodge.air";
    public const string CombatDodgeEarth = "combat.dodge.earth";
    public const string CombatDodgeLight = "combat.dodge.light";
    public const string CombatDodgeDark = "combat.dodge.dark";

    // Shield families (shield-system-spec.md §2.3) — element halves are generated, never hand-listed.
    public const string CombatShieldCapacityPrefix = "combat.shield.capacity";
    public const string CombatShieldToughnessPrefix = "combat.shield.toughness";
    public const string CombatShieldPenPrefix = "combat.shield.pen";
    public const string CombatShieldRegenPrefix = "combat.shield.regen";

    public const string CombatShieldCapacityOmni = "combat.shield.capacity.omni";
    public const string CombatShieldToughnessOmni = "combat.shield.toughness.omni";
    public const string CombatShieldPenOmni = "combat.shield.pen.omni";
    public const string CombatShieldRegenOmni = "combat.shield.regen.omni";

    public static string CombatShieldCapacity(ElementTypeId e) => $"{CombatShieldCapacityPrefix}.{e.ToElementId()}";
    public static string CombatShieldToughness(ElementTypeId e) => $"{CombatShieldToughnessPrefix}.{e.ToElementId()}";
    public static string CombatShieldPen(ElementTypeId e) => $"{CombatShieldPenPrefix}.{e.ToElementId()}";
    public static string CombatShieldRegen(ElementTypeId e) => $"{CombatShieldRegenPrefix}.{e.ToElementId()}";

    // H.1 (actor-hub-ssot.md) — 16 new element-typed combat families (8 pairs), 2026-08-24. Generated
    // over omni + roster exactly like the shipped 12 — prefix + element helper only, never a
    // hand-listed per-element const (spec-catalog-extension.md §5). Six of the eight pairs are
    // ROLE-INVERTED: for parry/block/reflection the DEFENDER owns the half that raises an outcome and
    // the ATTACKER owns the half that suppresses it — the opposite of power/defense. The seed catalog's
    // `role` field (attacker/defender/owner) carries this, never a name parse (H.9 Q2).
    public const string CombatPenetrationPrefix = "combat.penetration";
    public const string CombatAbsorptionPrefix = "combat.absorption";
    public const string CombatAmplificationPrefix = "combat.amplification";
    public const string CombatReductionPrefix = "combat.reduction";

    // T5.1 (spec-mitigation-chain.md) reader support — omni consts for the one H.1 pair a reader now
    // exists for, matching the older H.0/Shield style (CombatPowerOmni etc.) rather than raw
    // ElementRoster.OmniId interpolation, so CombatDerivedReader's new methods read the same way its
    // existing ones do.
    public const string CombatPenetrationOmni = "combat.penetration.omni";
    public const string CombatAbsorptionOmni = "combat.absorption.omni";
    public const string CombatAmplificationOmni = "combat.amplification.omni";
    public const string CombatReductionOmni = "combat.reduction.omni";

    // T5.3 (spec-evasion-chain.md) reader support — omni consts for parry/block, the pair a reader
    // now exists for. Omni-only: parry/block read no per-component breakdown (spec §3 never describes
    // one, and block/parry are explicitly not element-typed, §7 — "block is not a shield"). The
    // per-element slots these families' generator methods can still build stay registered (H.1) and
    // unread, same honest partial-wiring as every other not-yet-consumed sparse slot in this catalog.
    public const string CombatParryRateOmni = "combat.parry.rate.omni";
    public const string CombatParryBreakOmni = "combat.parry.break.omni";
    public const string CombatParryStrengthOmni = "combat.parry.strength.omni";
    public const string CombatParryShredOmni = "combat.parry.shred.omni";
    public const string CombatBlockRateOmni = "combat.block.rate.omni";
    public const string CombatBlockBreakOmni = "combat.block.break.omni";
    public const string CombatBlockStrengthOmni = "combat.block.strength.omni";
    public const string CombatBlockShredOmni = "combat.block.shred.omni";

    // T5.4 (spec-reflection.md) reader support — omni consts for reflection, resolved OMNI only,
    // same reasoning as parry/block (T5.3): no per-component breakdown described anywhere in the
    // spec, and reflection reads post-mitigation finalDamage, which by then has no per-element shape
    // left to weight against.
    public const string CombatReflectRateOmni = "combat.reflect.rate.omni";
    public const string CombatReflectResistRateOmni = "combat.reflect.resist.rate.omni";
    public const string CombatReflectDamageOmni = "combat.reflect.damage.omni";
    public const string CombatReflectResistDamageOmni = "combat.reflect.resist.damage.omni";
    public const string CombatReflectResistRatePrefix = "combat.reflect.resist.rate";
    public const string CombatReflectRatePrefix = "combat.reflect.rate";
    public const string CombatReflectResistDamagePrefix = "combat.reflect.resist.damage";
    public const string CombatReflectDamagePrefix = "combat.reflect.damage";
    public const string CombatParryBreakPrefix = "combat.parry.break";
    public const string CombatParryRatePrefix = "combat.parry.rate";
    public const string CombatParryShredPrefix = "combat.parry.shred";
    public const string CombatParryStrengthPrefix = "combat.parry.strength";
    public const string CombatBlockBreakPrefix = "combat.block.break";
    public const string CombatBlockRatePrefix = "combat.block.rate";
    public const string CombatBlockShredPrefix = "combat.block.shred";
    public const string CombatBlockStrengthPrefix = "combat.block.strength";

    public static string CombatPenetration(ElementTypeId e) => $"{CombatPenetrationPrefix}.{e.ToElementId()}";
    public static string CombatAbsorption(ElementTypeId e) => $"{CombatAbsorptionPrefix}.{e.ToElementId()}";
    public static string CombatAmplification(ElementTypeId e) => $"{CombatAmplificationPrefix}.{e.ToElementId()}";
    public static string CombatReduction(ElementTypeId e) => $"{CombatReductionPrefix}.{e.ToElementId()}";
    public static string CombatReflectResistRate(ElementTypeId e) => $"{CombatReflectResistRatePrefix}.{e.ToElementId()}";
    public static string CombatReflectRate(ElementTypeId e) => $"{CombatReflectRatePrefix}.{e.ToElementId()}";
    public static string CombatReflectResistDamage(ElementTypeId e) => $"{CombatReflectResistDamagePrefix}.{e.ToElementId()}";
    public static string CombatReflectDamage(ElementTypeId e) => $"{CombatReflectDamagePrefix}.{e.ToElementId()}";
    public static string CombatParryBreak(ElementTypeId e) => $"{CombatParryBreakPrefix}.{e.ToElementId()}";
    public static string CombatParryRate(ElementTypeId e) => $"{CombatParryRatePrefix}.{e.ToElementId()}";
    public static string CombatParryShred(ElementTypeId e) => $"{CombatParryShredPrefix}.{e.ToElementId()}";
    public static string CombatParryStrength(ElementTypeId e) => $"{CombatParryStrengthPrefix}.{e.ToElementId()}";
    public static string CombatBlockBreak(ElementTypeId e) => $"{CombatBlockBreakPrefix}.{e.ToElementId()}";
    public static string CombatBlockRate(ElementTypeId e) => $"{CombatBlockRatePrefix}.{e.ToElementId()}";
    public static string CombatBlockShred(ElementTypeId e) => $"{CombatBlockShredPrefix}.{e.ToElementId()}";
    public static string CombatBlockStrength(ElementTypeId e) => $"{CombatBlockStrengthPrefix}.{e.ToElementId()}";

    /// <summary>Combat channel family prefixes — 28 families × (omni + roster) slots (12 shipped + 16
    /// from H.1, derived-stats program, 2026-08-24).</summary>
    public static readonly IReadOnlyList<string> CombatChannelFamilies = new[]
    {
        "combat.power",
        "combat.defense",
        "combat.crit.rate",
        "combat.crit.resist",
        "combat.crit.damage",
        "combat.crit.resist.damage",
        "combat.accuracy",
        "combat.dodge",
        CombatShieldCapacityPrefix,
        CombatShieldToughnessPrefix,
        CombatShieldPenPrefix,
        CombatShieldRegenPrefix,
        CombatPenetrationPrefix,
        CombatAbsorptionPrefix,
        CombatAmplificationPrefix,
        CombatReductionPrefix,
        CombatReflectResistRatePrefix,
        CombatReflectRatePrefix,
        CombatReflectResistDamagePrefix,
        CombatReflectDamagePrefix,
        CombatParryBreakPrefix,
        CombatParryRatePrefix,
        CombatParryShredPrefix,
        CombatParryStrengthPrefix,
        CombatBlockBreakPrefix,
        CombatBlockRatePrefix,
        CombatBlockShredPrefix,
        CombatBlockStrengthPrefix
    };

    /// <summary>
    /// Per-family classification — declared here, beside the family list, rather than inferred from a
    /// channel's name (spec-stat-taxonomy.md §5). <c>Pool</c> families carry a null counterpart family;
    /// <c>Contest</c> families name the family whose same element-slot is their counterpart — see
    /// actor-hub-ssot.md §H.0. Extending this table (T2.2) is how the 16 new element-typed families
    /// register their classification too — never a hand-written per-channel Register call.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (StatClass Class, string? CounterpartFamily)> CombatFamilyClassification =
        new Dictionary<string, (StatClass, string?)>(StringComparer.Ordinal)
        {
            ["combat.power"] = (StatClass.Contest, "combat.defense"),
            ["combat.defense"] = (StatClass.Contest, "combat.power"),
            ["combat.crit.rate"] = (StatClass.Contest, "combat.crit.resist"),
            ["combat.crit.resist"] = (StatClass.Contest, "combat.crit.rate"),
            ["combat.crit.damage"] = (StatClass.Contest, "combat.crit.resist.damage"),
            ["combat.crit.resist.damage"] = (StatClass.Contest, "combat.crit.damage"),
            ["combat.accuracy"] = (StatClass.Contest, "combat.dodge"),
            ["combat.dodge"] = (StatClass.Contest, "combat.accuracy"),
            // Pool: the owner's own capacity/rate, no attacker-side counterpart (actor-hub-ssot.md §H.0).
            [CombatShieldCapacityPrefix] = (StatClass.Pool, null),
            [CombatShieldToughnessPrefix] = (StatClass.Contest, CombatShieldPenPrefix),
            [CombatShieldPenPrefix] = (StatClass.Contest, CombatShieldToughnessPrefix),
            [CombatShieldRegenPrefix] = (StatClass.Pool, null),

            // H.1 -- all 8 new pairs are Contest (actor-hub-ssot.md §H.1). Six are role-inverted (the
            // seed catalog's `role` field carries which actor owns which half); the classification
            // table only needs to know the pairing, not the ownership.
            [CombatPenetrationPrefix] = (StatClass.Contest, CombatAbsorptionPrefix),
            [CombatAbsorptionPrefix] = (StatClass.Contest, CombatPenetrationPrefix),
            [CombatAmplificationPrefix] = (StatClass.Contest, CombatReductionPrefix),
            [CombatReductionPrefix] = (StatClass.Contest, CombatAmplificationPrefix),
            [CombatReflectResistRatePrefix] = (StatClass.Contest, CombatReflectRatePrefix),
            [CombatReflectRatePrefix] = (StatClass.Contest, CombatReflectResistRatePrefix),
            [CombatReflectResistDamagePrefix] = (StatClass.Contest, CombatReflectDamagePrefix),
            [CombatReflectDamagePrefix] = (StatClass.Contest, CombatReflectResistDamagePrefix),
            [CombatParryBreakPrefix] = (StatClass.Contest, CombatParryRatePrefix),
            [CombatParryRatePrefix] = (StatClass.Contest, CombatParryBreakPrefix),
            [CombatParryShredPrefix] = (StatClass.Contest, CombatParryStrengthPrefix),
            [CombatParryStrengthPrefix] = (StatClass.Contest, CombatParryShredPrefix),
            [CombatBlockBreakPrefix] = (StatClass.Contest, CombatBlockRatePrefix),
            [CombatBlockRatePrefix] = (StatClass.Contest, CombatBlockBreakPrefix),
            [CombatBlockShredPrefix] = (StatClass.Contest, CombatBlockStrengthPrefix),
            [CombatBlockStrengthPrefix] = (StatClass.Contest, CombatBlockShredPrefix),
        };

    /// <summary>H.1's role field (H.9 Q2) — which actor owns each new element-typed family's half. Not
    /// asked of the shipped 12 (their existing "role" values already live only in catalog.json; this
    /// table is what <see cref="DerivedStatRegistry"/> needs to populate it without re-deriving from a
    /// name). Six of eight pairs are role-inverted: attacker owns the SUPPRESSING half.</summary>
    public static readonly IReadOnlyDictionary<string, string> CombatFamilyRole =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CombatPenetrationPrefix] = "attacker",
            [CombatAbsorptionPrefix] = "defender",
            [CombatAmplificationPrefix] = "attacker",
            [CombatReductionPrefix] = "defender",
            [CombatReflectResistRatePrefix] = "attacker",
            [CombatReflectRatePrefix] = "defender",
            [CombatReflectResistDamagePrefix] = "attacker",
            [CombatReflectDamagePrefix] = "defender",
            [CombatParryBreakPrefix] = "attacker",
            [CombatParryRatePrefix] = "defender",
            [CombatParryShredPrefix] = "attacker",
            [CombatParryStrengthPrefix] = "defender",
            [CombatBlockBreakPrefix] = "attacker",
            [CombatBlockRatePrefix] = "defender",
            [CombatBlockShredPrefix] = "attacker",
            [CombatBlockStrengthPrefix] = "defender",
        };

    /// <summary>Every combat family's <see cref="UnitClass"/> — GameUnits for flat magnitudes,
    /// SigmoidPoints/SigmoidMultiplierPoints for the four probability-shaped families
    /// (spec-magnitude-and-units.md §3, each verified against its consumer there).</summary>
    public static readonly IReadOnlyDictionary<string, UnitClass> CombatFamilyUnitClass =
        new Dictionary<string, UnitClass>(StringComparer.Ordinal)
        {
            ["combat.power"] = UnitClass.GameUnits,
            ["combat.defense"] = UnitClass.GameUnits,
            ["combat.crit.rate"] = UnitClass.SigmoidPoints,
            ["combat.crit.resist"] = UnitClass.SigmoidPoints,
            ["combat.crit.damage"] = UnitClass.SigmoidMultiplierPoints,
            ["combat.crit.resist.damage"] = UnitClass.SigmoidMultiplierPoints,
            ["combat.accuracy"] = UnitClass.SigmoidPoints,
            ["combat.dodge"] = UnitClass.SigmoidPoints,
            [CombatShieldCapacityPrefix] = UnitClass.GameUnits,
            [CombatShieldToughnessPrefix] = UnitClass.GameUnits,
            [CombatShieldPenPrefix] = UnitClass.GameUnits,
            [CombatShieldRegenPrefix] = UnitClass.GameUnitsPerSecond,

            // class-system P1.5 reader census (2026-08-26) — 16 of H.1's families verified against a
            // live production consumer, not a doc comment. ReciprocalPoints (class-system/spec-unit-
            // class-close.md §3.3/§3.5, authorised 2026-08-26 — NOT GameUnits, corrected after an
            // initial pass wrongly grouped these with combat.defense by tuning-file section rather
            // than by formula shape): an uncapped point delta feeding PierceFactor/AmpFactorReciprocal,
            // both asymptotic — verified at OverlayCombatCalculator.cs's mitigation chain.
            // PerMilleRatio: a linear-from-zero share clamped to [0,1] feeding a chance or a bounded
            // damage share — verified at OverlayCombatCalculator.cs:162-169 ("Rate contests are linear
            // and permille, not sigmoid") and CombatDamageDispatcher.cs:96-104 (reflect, same shape).
            [CombatPenetrationPrefix] = UnitClass.ReciprocalPoints,
            [CombatAbsorptionPrefix] = UnitClass.ReciprocalPoints,
            [CombatAmplificationPrefix] = UnitClass.ReciprocalPoints,
            [CombatReductionPrefix] = UnitClass.ReciprocalPoints,
            [CombatReflectRatePrefix] = UnitClass.PerMilleRatio,
            [CombatReflectResistRatePrefix] = UnitClass.PerMilleRatio,
            [CombatReflectDamagePrefix] = UnitClass.PerMilleRatio,
            [CombatReflectResistDamagePrefix] = UnitClass.PerMilleRatio,
            [CombatParryRatePrefix] = UnitClass.PerMilleRatio,
            [CombatParryBreakPrefix] = UnitClass.PerMilleRatio,
            // strength/shred: class-system/spec-unit-class-close.md §3.4 flags these "needs care" and
            // stops short of a definitive class rather than stretching one to fit (§2 step 4) — neither
            // SigmoidMultiplierPoints (not a sigmoid) nor ReciprocalPoints (not asymptotic) matches
            // ClampedContest.Apply's shape (a raw delta clamped LINEARLY to a permille share).
            // GameUnits is kept as the least-wrong fit: an uncapped ladder-scaled point delta at the
            // CHANNEL level, same as combat.power/defense, with the bounding happening downstream in
            // the clamp rather than in the channel's own arithmetic.
            [CombatParryStrengthPrefix] = UnitClass.GameUnits,
            [CombatParryShredPrefix] = UnitClass.GameUnits,
            [CombatBlockRatePrefix] = UnitClass.PerMilleRatio,
            [CombatBlockBreakPrefix] = UnitClass.PerMilleRatio,
            [CombatBlockStrengthPrefix] = UnitClass.GameUnits,
            [CombatBlockShredPrefix] = UnitClass.GameUnits,
        };

    /// <summary>One generated combat channel id together with the family and slot it came from — the
    /// richer form <see cref="AllCombatChannelIds"/> flattens away. Kept as a record struct so building
    /// it costs nothing extra over the flat list (see <see cref="BuildAllCombatChannelIds"/>).</summary>
    public readonly record struct CombatChannelEntry(string ChannelId, string Family, string Slot);

    /// <summary>
    /// All overlay combat derived channels — generated family × (omni + roster) so the list is
    /// exhaustive by construction (element-hub-ssot.md §6; 28 × 7 = 196 as of the derived-stats
    /// program's H.1, 2026-08-24 — was 12 × 7 = 84).
    ///
    /// <para><b>Generated from the roster TABLE, not the enum</b> (E18). That is what makes a seventh
    /// element rows plus regeneration: its 28 channels appear here, and every consumer picks them up
    /// because <c>CombatDerivedReader</c> matches channels by pattern rather than by name.</para>
    ///
    /// <para><b>Cached by reference to the source table</b> (E25, completeness-audit.md B3). The
    /// stated reason for never caching — "the roster is loaded after startup" — stopped being a
    /// reason once E20 shipped: <see cref="ElementTable.Use"/>/<see cref="ElementTable.UseScoped"/>
    /// always assign a <b>new</b> immutable instance rather than mutating one in place, so comparing
    /// <see cref="ElementTable.Current"/> by reference against what this cache was last built from is
    /// exactly as fresh as rebuilding every call, at 196 fewer string allocations per read.
    /// <see cref="BattleStatComposer.Compose"/> called this once per actor composed.</para>
    /// </summary>
    public static IReadOnlyList<string> AllCombatChannelIds => EnsureCache().List;

    /// <summary>Same generation, same cache slot as <see cref="AllCombatChannelIds"/> — the (family,
    /// slot) pair every channel id was built from, so a counterpart id can be computed as
    /// <c>counterpartFamily + "." + Slot</c> without re-parsing the id string. Built in the same pass
    /// as the flat list so the two can never desync (<see cref="EnsureCache"/>).</summary>
    public static IReadOnlyList<CombatChannelEntry> AllCombatChannelEntries => EnsureCache().Entries;

    /// <summary>
    /// O(1) membership over the same cached generation <see cref="AllCombatChannelIds"/> uses — the
    /// other half of E25: <c>StatusStatPayload.IsKnownChannel</c> used to <c>.Contains</c> a freshly
    /// allocated 84-element list on every channel it parsed.
    /// </summary>
    public static bool IsCombatChannel(string channel) => EnsureCache().Set.Contains(channel);

    /// <summary>
    /// Found 2026-08-25 re-running the full suite after adding more <c>ElementTable.UseScoped</c>-based
    /// tests (T2.6, T3): the cache used to be a single <c>static</c> slot guarded by a lock, keyed only
    /// by reference to <see cref="ElementTable.Current"/>. <c>Current</c> itself is <c>AsyncLocal</c>-
    /// scoped (different concurrently-running tests can legitimately see different rosters), but the
    /// CACHE was one shared slot for the whole process — so two tests scoped to different rosters,
    /// interleaved by the test runner, could thrash each other's cache and break
    /// <c>ChannelCacheTests.Repeated_reads_with_no_roster_change_return_the_same_list_instance</c>'s
    /// same-instance guarantee non-deterministically (reproduced: failed once in ~4 full-suite runs,
    /// passed in isolation and on retry — the signature of a race, not a regression). The cache is now
    /// itself <c>AsyncLocal</c>, one slot per scope, exactly matching how <see cref="ElementTable"/>
    /// already scopes the roster pointer it is keyed on.
    /// </summary>
    static readonly AsyncLocal<CacheSlot?> Local = new();

    readonly record struct CacheSlot(
        ElementTable Source, IReadOnlyList<string> List, HashSet<string> Set, IReadOnlyList<CombatChannelEntry> Entries);

    static CacheSlot EnsureCache()
    {
        var current = ElementTable.Current;
        var slot = Local.Value;
        if (slot is { } s && ReferenceEquals(s.Source, current))
            return s;

        var entries = BuildAllCombatChannelEntries(current.Elements.Where(e => e.Enabled).Select(e => e.ElementId));
        var list = entries.Select(e => e.ChannelId).ToList();
        var built = new CacheSlot(current, list, new HashSet<string>(list, StringComparer.Ordinal), entries);
        Local.Value = built;
        return built;
    }

    /// <summary>The channel set for an explicit roster — how a test can add a seventh element.</summary>
    public static IReadOnlyList<string> BuildAllCombatChannelIds(IEnumerable<string> elementIds) =>
        BuildAllCombatChannelEntries(elementIds).Select(e => e.ChannelId).ToList();

    /// <summary>Same generation as <see cref="BuildAllCombatChannelIds"/>, with family and slot kept
    /// alongside each id. The single source both that method and the cache build from, so they cannot
    /// desync.</summary>
    public static IReadOnlyList<CombatChannelEntry> BuildAllCombatChannelEntries(IEnumerable<string> elementIds)
    {
        var roster = elementIds.ToList();
        var entries = new List<CombatChannelEntry>(CombatChannelFamilies.Count * (roster.Count + 1));
        foreach (var family in CombatChannelFamilies)
        {
            entries.Add(new CombatChannelEntry($"{family}.{ElementRoster.OmniId}", family, ElementRoster.OmniId));
            foreach (var element in roster)
                entries.Add(new CombatChannelEntry($"{family}.{element}", family, element));
        }

        return entries;
    }

    // ================================================================================================
    // Non-element families (actor-hub-ssot.md §H.2-H.7, derived-stats program, 2026-08-24). Three
    // separate generators, per spec-catalog-extension.md §2.1 — none of these join
    // CombatChannelFamilies/AllCombatChannelIds. A non-element family that leaked in there would break
    // the roster assertion AND get swept into element expansion (actor-hub-ssot.md §3G rule 1).
    // ================================================================================================

    // H.2 -- status potency, 4 families x (omni|dot|cc|contagion) = 16, plus a sparse {statusId}
    // override exactly like status.power/status.resist's existing shape (same axis, same combine rule
    // per §3C/§3D). status.duration/status.intensity are the attacker-side terms; the Reduction
    // siblings are the defender-side terms.
    public const string StatusDurationOmni = "status.duration.omni";
    public const string StatusDurationDot = "status.duration.dot";
    public const string StatusDurationCc = "status.duration.cc";
    public const string StatusDurationContagion = "status.duration.contagion";

    public const string StatusDurationReductionOmni = "status.durationReduction.omni";
    public const string StatusDurationReductionDot = "status.durationReduction.dot";
    public const string StatusDurationReductionCc = "status.durationReduction.cc";
    public const string StatusDurationReductionContagion = "status.durationReduction.contagion";

    public const string StatusIntensityOmni = "status.intensity.omni";
    public const string StatusIntensityDot = "status.intensity.dot";
    public const string StatusIntensityCc = "status.intensity.cc";
    public const string StatusIntensityContagion = "status.intensity.contagion";

    public const string StatusIntensityReductionOmni = "status.intensityReduction.omni";
    public const string StatusIntensityReductionDot = "status.intensityReduction.dot";
    public const string StatusIntensityReductionCc = "status.intensityReduction.cc";
    public const string StatusIntensityReductionContagion = "status.intensityReduction.contagion";

    public static string StatusDuration(string statusId) => $"status.duration.{statusId}";
    public static string StatusDurationReduction(string statusId) => $"status.durationReduction.{statusId}";
    public static string StatusIntensity(string statusId) => $"status.intensity.{statusId}";
    public static string StatusIntensityReduction(string statusId) => $"status.intensityReduction.{statusId}";

    // H.3 -- action-category, 2 families x (attack|defense|support|movement|status) = 10. New axis,
    // not element-typed, not status-category-typed. skill.cooldown.* is Race (Q3: unpaired by nature —
    // the opponent's own cooldown is the counter). skill.effectiveness.* is Feeder (Q3: applied to
    // baseOverlayDamage before the power/defense delta, so it inherits its pair from combat.defense).
    public const string ActionCategoryAttack = "attack";
    public const string ActionCategoryDefense = "defense";
    public const string ActionCategorySupport = "support";
    public const string ActionCategoryMovement = "movement";
    public const string ActionCategoryStatus = "status";

    public static readonly IReadOnlyList<string> ActionCategories = new[]
    {
        ActionCategoryAttack, ActionCategoryDefense, ActionCategorySupport, ActionCategoryMovement, ActionCategoryStatus
    };

    public const string SkillCooldownPrefix = "skill.cooldown";
    public const string SkillEffectivenessPrefix = "skill.effectiveness";

    public static string SkillCooldown(string category) => $"{SkillCooldownPrefix}.{category}";
    public static string SkillEffectiveness(string category) => $"{SkillEffectivenessPrefix}.{category}";


    // H.5 -- resource, 3 families x 5 ids = 15 (supersedes §3G's 10). Pool class throughout (Q4): the
    // counters are statuses (root/qi-burn), never a paired channel. max/regen are magnitudes, FlatSum,
    // uncapped. efficiency is a bounded ratio (T4.4) -- SumIncreased + Cap: DerivedStatPolicy.
    // ResourceEfficiencyCap, since ComposeChannel's FlatSum case never applies Cap. None of the three
    // carries a UnitClass yet (§2.3: no reader at registration).
    public const string ResourceMaxPrefix = "resource.max";
    public const string ResourceRegenPrefix = "resource.regen";
    public const string ResourceEfficiencyPrefix = "resource.efficiency";

    /// <summary>Active restoration power, per resource. **Was `combat.heal.power`, an hp-only channel**
    /// — generalised 2026-09-02 (owner: "hp is a resource generating, so no need heal, we need resource
    /// generating; don't make healing for only 1 resource and don't support other resources, it is wrong
    /// design"). `hp` keeps its exact former coefficients, so the rename moves no balance.
    ///
    /// <para><b>Not to be confused with <see cref="ResourceRegenPrefix"/>.</b> `regen` is the PASSIVE
    /// per-tick drip; `gen` scales an ACTIVE grant at resolution. They are different mechanisms and a
    /// game wants both — the defect was that only hp had the active half. The two names are one letter
    /// apart, which is a readability hazard and not a matching one: prefix tests are `StartsWith`, and
    /// neither string is a prefix of the other, so `recovery.families = ["resource.regen"]` does not
    /// capture `resource.restore.*` and never will.</para></summary>
    public const string ResourceRestorePrefix = "resource.restore";

    public static string ResourceMax(string resourceId) => $"{ResourceMaxPrefix}.{resourceId}";
    public static string ResourceRegen(string resourceId) => $"{ResourceRegenPrefix}.{resourceId}";
    public static string ResourceEfficiency(string resourceId) => $"{ResourceEfficiencyPrefix}.{resourceId}";
    public static string ResourceRestore(string resourceId) => $"{ResourceRestorePrefix}.{resourceId}";

    /// <summary>The six actor resource ids — gk-data/packs/fusion/data/seed/resources/roster.json is the authored mirror;
    /// this is the code-side list registration walks. Kept in ordinal order to match that file's
    /// <c>ordinal</c> field. <c>poise</c> appended 2026-08-26 (class-system, module `poise-resource`,
    /// decisions.md "Resource model") — append-only, never reorder: the ordinal coupling is a comment,
    /// not a compiler-checked invariant, so reordering silently desyncs the two files.</summary>
    public static readonly IReadOnlyList<string> ResourceIds = new[] { "hp", "stamina", "hunger", "spirit", "qi", "poise" };

    // H.6 -- movement, 1 channel. Pool class (Q4, same reasoning as resource). Lands with §H.5 per
    // action-map.md's own promise ("registers in actor-hub-ssot.md §3 with resource.*").
    public const string MoveRange = "move.range";

    // H.7 -- progression, 2 channels. xpRate: Class null (non-combat rate, same as progression.power/
    // progression.realm above), uncapped. breakthroughSuccess (T4.4): Class Pool -- the actor's own
    // roll probability, no pair; capped at DerivedStatPolicy.BreakthroughSuccessCap (EveryCapIsClassified
    // requires a StatClass on any capped channel, spec-stat-taxonomy.md §6.1).
    public const string ProgressionXpRate = "progression.xpRate";
    public const string ProgressionBreakthroughSuccess = "progression.breakthroughSuccess";

    // H.8 -- loadout, 1 channel (D4.25, spec-unique-pipeline.md §4: "the extend-action-slot grant").
    // The actor's own equipped-slot capacity granted by worn gear -- LoadoutSet/AutoEquip/CapPolicy
    // are this channel's three readers.
    public const string LoadoutSlots = "loadout.slots";

    // H.9 -- siege AI, 1 channel (base-defense/spec-siege-ai.md §5.20 rule 4, resolved 2026-09-07).
    // Reader: IBattleView.AggressionOf (BattleRunState.cs), consumed by SiegeAiIntentSource ->
    // SiegeAi.EffectiveTier. Pool class: one actor's own value, no counterpart. Structurally bounded
    // to [-AiTuning.AggressionRange, +AiTuning.AggressionRange] (currently -2..+2, siege.v1.json's
    // "ai.aggression.range") by the VOCABULARY, not by this channel or a Cap (Cap only clamps the top
    // end -- DerivedComposer.cs:72 -- wrong shape for a symmetric range). A future taunt/stealth status
    // that sets this outside that range makes EffectiveTier throw, by design, matching this repo's
    // "throw, never silently clamp" rule generalized from magnitude overflow to a closed vocabulary.
    // Defaults to 0 (neutral) for every actor today -- no content targets this channel yet.
    public const string AiAggression = "ai.aggression";
}

/// <summary>Actor element type metadata field names — see element-hub-ssot.md §5.</summary>
public static class ElementMetadataKeys
{
    public const string Primary = "element.type.primary";
    public const string Secondary = "element.type.secondary";
}
