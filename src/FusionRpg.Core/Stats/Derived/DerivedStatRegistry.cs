using FusionRpg.Core.Battle.Timeline;

namespace FusionRpg.Core.Stats.Derived;

public enum DerivedComposeKind
{
    FlatSum,
    FlatReplace,
    SumIncreased,
    MaxPriorityFlag
}

/// <summary>
/// <c>Class</c> and <c>Unit</c> are two orthogonal axes (spec-stat-taxonomy.md §2.5): <c>Class</c> answers
/// "does it need a counterpart" (null only for the two non-combat progression channels the counterbalance
/// rule does not apply to — actor-hub-ssot.md §H.0's "Non-combat" row); <c>Unit</c> answers "what
/// arithmetic is it and how does it render" and is null only when no reader exists yet to name (§2.7) —
/// never a guessed placeholder. <c>double</c> composition fields are unchanged by design — §10.7 of
/// ssot-power-scale.md decided the <c>long</c> rule binds what composition produces, not the arithmetic
/// that composes it.
/// </summary>
public sealed record DerivedStatDef(
    string ChannelId,
    DerivedComposeKind Compose,
    double DefaultValue,
    double? Cap = null,
    StatClass? Class = null,
    UnitClass? Unit = null,
    string? CounterpartOf = null,
    /// <summary>class-system P1.6 (2026-08-26) — required whenever <see cref="Unit"/> is null: names
    /// the missing reader, so "not classified yet" (an oversight) and "classified as unread" (a
    /// documented fact, re-checked by the census) are never the same shape in the data. Mirrors
    /// catalog.json's own <c>unitClassNote</c> field.</summary>
    string? UnitClassNote = null);

/// <summary>Catalog SSOT for derived channels — unknown id → reject.</summary>
public sealed class DerivedStatRegistry
{
    readonly Dictionary<string, DerivedStatDef> _defs = new(StringComparer.Ordinal);

    /// <summary>Captured once at construction, not re-read live by <see cref="TryResolveChannel"/> —
    /// so a registry instance stays internally consistent between its statically-registered channels
    /// (whose <see cref="DerivedStatDef.Cap"/> is frozen into the def at registration) and the sparse
    /// per-status-id ones resolved on demand. Both must agree on which cap value they saw.</summary>
    readonly double _categoryResistCap;

    /// <summary>T14/B28 — the turn.speed channel's base, captured at construction for the same reason
    /// <see cref="_categoryResistCap"/> is: a registry instance must stay internally consistent with the
    /// values its defs were frozen against.</summary>
    readonly long _turnDefaultSpeed;

    DerivedStatRegistry()
    {
        _categoryResistCap = DerivedStatPolicy.CategoryResistCap;
        _turnDefaultSpeed = DerivedStatPolicy.TurnDefaultSpeed;
    }

    public static DerivedStatRegistry CreateDefault()
    {
        var r = new DerivedStatRegistry();
        r.RegisterDefaults();
        return r;
    }

    void RegisterDefaults()
    {
        // Pool: absorbed at the AppliedCombat merge into hp/maxHp/atk/defense/arm* — the actor's own
        // progression bonus, nothing to contest (actor-hub-ssot.md §H.0).
        Register(new(DerivedStatChannels.ProgressionBonusMaxHp, DerivedComposeKind.FlatSum, 0,
                     Class: StatClass.Pool, Unit: UnitClass.GameUnits));
        // RETIRED 2026-09-18 — `progression.bonus.atk` (solid-enforcement `retire-atk` R4). The id
        // lives in `DerivedStatChannels.Retired`, is NOT registered here, is never composed, and is
        // dropped with a count by `AptitudeTuningLoader.Parse` wherever archived tuning names it
        // (v1–v8 all do). Unregistering is what finally lets the loader's drop be complete: no edge
        // can resolve to this channel any more, so any future reference fails loudly at resolve time.
        // The regression guard is `guard-class-system` G3, which now exits 0 and gates in CI.
        Register(new(DerivedStatChannels.ProgressionBonusDefense, DerivedComposeKind.FlatSum, 0,
                     Class: StatClass.Pool, Unit: UnitClass.GameUnits));
        Register(new(DerivedStatChannels.ProgressionBonusArm1, DerivedComposeKind.FlatSum, 0,
                     Class: StatClass.Pool, Unit: UnitClass.GameUnits));
        Register(new(DerivedStatChannels.ProgressionBonusArm2, DerivedComposeKind.FlatSum, 0,
                     Class: StatClass.Pool, Unit: UnitClass.GameUnits));

        // Class: null — the counterbalance rule does not apply to Θ itself ("Non-combat" row, H.0).
        // Unit: LadderIndex — an index on the power ladder, not a magnitude (spec-magnitude-and-units §3.2).
        Register(new(DerivedStatChannels.ProgressionPower, DerivedComposeKind.FlatReplace, 1.0,
                     Class: null, Unit: UnitClass.LadderIndex));
        Register(new(DerivedStatChannels.ProgressionRealm, DerivedComposeKind.FlatReplace, 1.0,
                     Class: null, Unit: UnitClass.LadderIndex));

        // Contest: attacker half, paired with status.resist.{category}. Unit: StatusPotencyPoints —
        // raw magnitude, context part suppressed (spec-magnitude-and-units.md §4.3).
        Register(new(DerivedStatChannels.StatusPowerOmni, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusResistOmni));
        Register(new(DerivedStatChannels.StatusPowerDot, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusResistDot));
        Register(new(DerivedStatChannels.StatusPowerCc, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusResistCc));
        Register(new(DerivedStatChannels.StatusPowerContagion, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusResistContagion));

        // Contest: defender half. omni is the balance knob and stays uncapped; dot/cc/contagion cap at
        // the tuned DerivedStatPolicy.CategoryResistCap — ONE home (cap-consolidation, T1; was clamped
        // a second time in ResistanceEvaluator, which made raising the tunable past 0.95 a silent
        // no-op — see gk-core/data/tuning/derived-stats.v1.json's _meta). The cap is StatusPotencyPoints-shaped,
        // not GameUnits — ContestHalvesAreUncapped exempts it by design (spec-stat-taxonomy.md §6.1:
        // the "uncapped" rule binds GameUnits/GameUnitsPerSecond magnitudes).
        Register(new(DerivedStatChannels.StatusResistOmni, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusPowerOmni));
        Register(new(DerivedStatChannels.StatusResistDot, DerivedComposeKind.SumIncreased, 0, _categoryResistCap,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusPowerDot));
        Register(new(DerivedStatChannels.StatusResistCc, DerivedComposeKind.SumIncreased, 0, _categoryResistCap,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusPowerCc));
        Register(new(DerivedStatChannels.StatusResistContagion, DerivedComposeKind.SumIncreased, 0, _categoryResistCap,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusPowerContagion));

        // P0.5 (battle-timeline B9, spec-readiness-model.md): flat and non-elemental on purpose — see
        // DerivedTurnChannels' own doc comment for why these stay out of the generated combat roster.
        // Non-zero defaults are load-bearing: 0 would divide-by-zero (Speed) or mean instant actions
        // (Haste). Class: null matches the Progression channels above — a pacing axis, not a combat
        // stat with a counterpart. FlatSum, not FlatReplace: a haste buff/item contributes an amount,
        // it does not replace the whole channel.
        Register(new(DerivedTurnChannels.Speed, DerivedComposeKind.FlatSum, _turnDefaultSpeed,
                     Class: null, Unit: UnitClass.GameUnits));
        Register(new(DerivedTurnChannels.Haste, DerivedComposeKind.FlatSum, DerivedTurnChannels.NominalHasteMilli,
                     Class: null, Unit: UnitClass.PerMilleRatio));

        RegisterNonElementExtensions();
        RegisterCombatDefaults();
    }

    /// <summary>H.2-H.7 (actor-hub-ssot.md, derived-stats program, 2026-08-24) — registration only, no
    /// reader wired. Every new channel defaults to 0 and carries no Cap: assigning one now would be
    /// shipping a balance value ahead of the module that owns its formula (spec-catalog-extension.md
    /// §7 "Never"). Unit stays null throughout — none of the 157 has a nameable consumer yet (§2.3).</summary>
    void RegisterNonElementExtensions()
    {
        // H.2 -- status potency, attacker-side (duration/intensity) mirrors status.power exactly.
        // Unit: StatusPotencyPoints (class-system P1.5, 2026-08-26) -- ResistanceEvaluator.cs:331-336's
        // ComputePotencyDelta reads these through the SAME formula shape as status.power/status.resist
        // (a dynamically-built "status.{family}.{omni|category|statusId}" lookup, not the named
        // constant, which is why a constant-name grep alone misses this reader).
        Register(new(DerivedStatChannels.StatusDurationOmni, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusDurationReductionOmni));
        Register(new(DerivedStatChannels.StatusDurationDot, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusDurationReductionDot));
        Register(new(DerivedStatChannels.StatusDurationCc, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusDurationReductionCc));
        Register(new(DerivedStatChannels.StatusDurationContagion, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusDurationReductionContagion));

        Register(new(DerivedStatChannels.StatusDurationReductionOmni, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusDurationOmni));
        Register(new(DerivedStatChannels.StatusDurationReductionDot, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusDurationDot));
        Register(new(DerivedStatChannels.StatusDurationReductionCc, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusDurationCc));
        Register(new(DerivedStatChannels.StatusDurationReductionContagion, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusDurationContagion));

        Register(new(DerivedStatChannels.StatusIntensityOmni, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusIntensityReductionOmni));
        Register(new(DerivedStatChannels.StatusIntensityDot, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusIntensityReductionDot));
        Register(new(DerivedStatChannels.StatusIntensityCc, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusIntensityReductionCc));
        Register(new(DerivedStatChannels.StatusIntensityContagion, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusIntensityReductionContagion));

        Register(new(DerivedStatChannels.StatusIntensityReductionOmni, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusIntensityOmni));
        Register(new(DerivedStatChannels.StatusIntensityReductionDot, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusIntensityDot));
        Register(new(DerivedStatChannels.StatusIntensityReductionCc, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusIntensityCc));
        Register(new(DerivedStatChannels.StatusIntensityReductionContagion, DerivedComposeKind.SumIncreased, 0,
                     Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: DerivedStatChannels.StatusIntensityContagion));

        // H.3 -- action-category. cooldown is Race (Q3, unpaired by nature); effectiveness is Feeder
        // (Q3, inherits its pair from combat.defense downstream — no CounterpartOf at this layer).
        // Unit stays null (class-system P1.5/P1.6 census, 2026-08-26): skill.cooldown has TWO reader
        // stubs with zero callers (CooldownMath.ApplyReduction's own doc comment: "No caller wired yet
        // -- the action system... is still being specified"; ActionEnvelope.CooldownChannel is never
        // dereferenced). skill.effectiveness's OverlayCombatRequest.EffectivenessMultiplier genuinely
        // participates in the formula but every one of its three construction sites (OverlayCombatMath.cs,
        // DebugCombatActions.cs, BattleEngine.cs) leaves it at the default 1.0 no-op -- nothing sets it
        // from this channel. Both await the action/timeline layer (action-map.md, approved unbuilt).
        // The note is per-CATEGORY on purpose. The reader MECHANISM is generic -- any action whose
        // envelope names `skill.cooldown.{category}` / `skill.effectiveness.{category}` is read
        // (species-skills S2/S3) -- but only shipped CONTENT decides which categories are actually
        // exercised, and today exactly one action ships: the basic attack, which is `attack`.
        // Claiming a reader for the other four would make `MovementPayloadTests`'s deliberate
        // "no production reader today" tripwire lie, which is the opposite of what it is for.
        foreach (var category in DerivedStatChannels.ActionCategories)
        {
            var opted = category == DerivedStatChannels.ActionCategoryAttack;

            Register(new(DerivedStatChannels.SkillCooldown(category), DerivedComposeKind.FlatSum, 0,
                         Class: StatClass.Race,
                         UnitClassNote: opted
                             ? "Read by CooldownLedger.Start via CooldownMath.ApplyReduction, resolved from ActionEnvelope.CooldownChannel at the ARMING site (species-skills S2, 2026-09-04). The shipped basic attack opts in, so this is live in every battle; 0 is neutral and is the exact identity. NOT read by the balance predictor -- DominanceGuard.BuildReservedFamilies still reserves it, correctly, because that list is about the closed-form duel model rather than the battle path."
                             : "No reader in shipped content: the MECHANISM exists (CooldownLedger.Start reads whatever channel ActionEnvelope.CooldownChannel names, species-skills S2), but no shipped action in this category names it -- the basic attack is the only opted-in action and it is `attack`. Wiring one here must update this note."));

            Register(new(DerivedStatChannels.SkillEffectiveness(category), DerivedComposeKind.FlatSum, 0,
                         Class: StatClass.Feeder,
                         UnitClassNote: opted
                             ? "Read by BasicAttack, which sets OverlayCombatRequest.EffectivenessMultiplier from this channel via OverlayCombatRequest.MultiplierFromPerMille, resolved from ActionEnvelope.EffectivenessChannel (species-skills S3, 2026-09-04). 0 is neutral and yields exactly 1.0. NOT read by the balance predictor -- see the cooldown note."
                             : "No reader in shipped content: the MECHANISM exists (BasicAttack reads whatever channel ActionEnvelope.EffectivenessChannel names, species-skills S3), but no shipped action in this category names it. Wiring one here must update this note."));
        }

        // H.4 -- healing. Pool, unpaired (owner decision 2026-08-24 — dissolves §4.3 rather than
        // reopening it: no defender-side term means no delta on the heal path at all). Unit: GameUnits
        // (class-system P1.5, 2026-08-26) -- OverlayCombatMath.cs:81,86 reads it as a flat additive
        // magnitude (effectiveHeal = max(0, signedAmount + healPower)), same shape as combat.power.

        // RETIRED 2026-09-02 -- `combat.heal.power` was generalised into `resource.restore.{resource}` (0.8).
        // It stays REGISTERED, and only for this reason: `data/tuning/aptitudes.v1/v2/v3.json` stay on
        // disk as revert points and still carry edges naming it, and `TerminationGuardTests` deliberately
        // pins v1 to prove historical facts about it. An unregistered channel is a hard load rejection,
        // so retiring the id outright would make every archived config unloadable and delete those
        // regression checks. Migration-shim only, exactly like `CreatureRarity`'s retired four-value ladder:
        // **nothing reads it** (OverlayCombatMath moved to resource.restore.hp) and **no new edge may name
        // it** -- the live config v4 has none, and AptitudeTuningTests' coverage test is over
        // `resource.restore`, not this.
        Register(new("combat.heal.power", DerivedComposeKind.FlatSum, 0, Class: StatClass.Pool,
                     UnitClassNote: "RETIRED 2026-09-02 -- superseded by resource.restore.hp. Registered only so archived aptitudes.v1/v2/v3.json remain loadable; no reader, no new edges."));

        // H.5 -- resource. Pool throughout (Q4): the counters are statuses, not stats. max/regen are
        // magnitudes and stay FlatSum/uncapped (spec-actor-channels.md §2.2). efficiency is a bounded
        // 0..1 ratio (T4.4) -- SumIncreased, not FlatSum: ComposeChannel's FlatSum case never calls
        // Cap(...) at all (only SumIncreased does), so a Cap on a FlatSum channel would be a silent
        // no-op. SumIncreased also reads Increased-op modifiers, the correct shape for a percentage-
        // like stacking ratio (matching status.power/status.resist's own SumIncreased+Cap precedent,
        // the only other capped channel family in this registry).
        // Unit stays null (class-system P1.5/P1.6 census, 2026-08-26): no src/ code reads any
        // resource.max/regen/efficiency.{id} for any of the six resource ids. gk-core/tools/CombatSim's POC
        // reads resource.max.hp/resource.regen.hp specifically (AptitudeModel.cs, Analytic.cs) but a
        // standalone tool is not a shipped reader; its own JsonEmit.cs marks every non-hp id and all
        // of efficiency as "reserved" (action-priced, action layer unbuilt).
        foreach (var resourceId in DerivedStatChannels.ResourceIds)
        {
            Register(new(DerivedStatChannels.ResourceMax(resourceId), DerivedComposeKind.FlatSum, 0, Class: StatClass.Pool,
                         UnitClassNote: "Readers exist but are narrow (corrected 2026-09-02 -- this note previously read 'No shipped reader for any resource id', which was stale): ExhaustionPolicy.cs:59 reads ResourceRegen(resourceId) GENERICALLY over whatever resources it manages, and Predictor.cs reads the hp and poise members by name. No reader consumes max/regen for hunger/qi/spirit/stamina, and the action/resource economy that would is unbuilt (action-map.md)."));
            Register(new(DerivedStatChannels.ResourceRegen(resourceId), DerivedComposeKind.FlatSum, 0, Class: StatClass.Pool,
                         UnitClassNote: "Readers exist but are narrow (corrected 2026-09-02 -- this note previously read 'No shipped reader for any resource id', which was stale): ExhaustionPolicy.cs:59 reads ResourceRegen(resourceId) GENERICALLY over whatever resources it manages, and Predictor.cs reads the hp and poise members by name. No reader consumes max/regen for hunger/qi/spirit/stamina, and the action/resource economy that would is unbuilt (action-map.md)."));
            Register(new(DerivedStatChannels.ResourceEfficiency(resourceId), DerivedComposeKind.SumIncreased, 0,
                         DerivedStatPolicy.ResourceEfficiencyCap, Class: StatClass.Pool,
                         UnitClassNote: "No reader: action cost reduction has no consumer until the action layer exists (action-map.md). CombatSim's own tuning explicitly marks this family 'reserved'."));
            // Active restoration power -- was the hp-only `combat.heal.power` until 2026-09-02. `hp` is
            // the one member with a shipped reader (OverlayCombatMath.cs, flat additive magnitude), so
            // it alone carries GameUnits; the other five are registered and unread, exactly like
            // max/regen/efficiency, until the action layer grants a non-hp resource.
            Register(resourceId == "hp"
                ? new(DerivedStatChannels.ResourceRestore(resourceId), DerivedComposeKind.FlatSum, 0,
                      Class: StatClass.Pool, Unit: UnitClass.GameUnits)
                : new(DerivedStatChannels.ResourceRestore(resourceId), DerivedComposeKind.FlatSum, 0, Class: StatClass.Pool,
                      UnitClassNote: "No shipped reader for any non-hp resource id -- active restoration for stamina/hunger/spirit/qi/poise has no consumer until the action layer grants one (action-map.md)."));
        }

        // H.6 -- movement. Pool (Q4, same reasoning as resource). Unit stays null: A9 movement-actions
        // (BasicAttack.cs's ApplyBasicAttack, 2026-09-07) is the first and only reader, and it consumes
        // the value as a cell count, not through any registered UnitClass -- no dedicated board-distance
        // unit exists yet, a separate question from whether a reader exists at all.
        Register(new(DerivedStatChannels.MoveRange, DerivedComposeKind.FlatSum, 0, Class: StatClass.Pool,
                     UnitClassNote: "Reader: A9 movement-actions (BasicAttack.cs's ApplyBasicAttack, 2026-09-07) reads this to bound how many cells a Movement-category action moves an actor toward the nearest living enemy."));

        // H.7 -- progression. xpRate: Class null, matching progression.power/progression.realm — a
        // rate/magnitude the counterbalance rule does not apply to ("Non-combat" row, H.0); FlatSum,
        // uncapped. breakthroughSuccess (T4.4): unlike power/realm's LadderIndex shape, this is the
        // actor's OWN roll probability, no pair -- Pool fits it the same way it fits resource.efficiency,
        // and EveryCapIsClassified (spec-stat-taxonomy.md §6.1, StatTaxonomyTests.cs) requires any
        // capped channel to carry a StatClass, not stay ambiguous. SumIncreased + Cap, same reasoning
        // as resource.efficiency above. Unit stays null on both (class-system P1.5/P1.6, 2026-08-26):
        // RpgXpAwardMap.cs's NoKillPowerScaleYet is a hardcoded 1.0 placeholder, not sourced from
        // progression.xpRate; no breakthrough roll/grant mechanism is wired to progression.breakthroughSuccess.
        Register(new(DerivedStatChannels.ProgressionXpRate, DerivedComposeKind.FlatSum, 0, Class: null,
                     UnitClassNote: "No reader: RpgXpAwardMap's NoKillPowerScaleYet is a hardcoded 1.0 placeholder, not sourced from this channel."));
        Register(new(DerivedStatChannels.ProgressionBreakthroughSuccess, DerivedComposeKind.SumIncreased, 0,
                     DerivedStatPolicy.BreakthroughSuccessCap, Class: StatClass.Pool,
                     UnitClassNote: "No reader: the breakthrough roll/grant mechanism this probability would drive is unbuilt."));

        // H.8 -- loadout.slots (D4.25, spec-unique-pipeline.md §4). Pool (Q4, same reasoning as
        // resource/movement above): the actor's own equipped-slot capacity, no attacker-side
        // counterpart. Count unit (StatClass.cs): a discrete slot count, not a magnitude. FlatSum,
        // uncapped composition -- composing two +1 grants must sum to a faithful 2, not saturate at 1,
        // or a reader wanting the raw worn count (e.g. a UI badge counting extend-slot items) would
        // have no way to recover it. The three readers (LoadoutSet.EffectiveMaxSize, called from
        // LoadoutSet.Validate, AutoEquip.Select and CapPolicy.EquippedSkillCap) apply the "one at a
        // time" rule (channel > 0 ? 1 : 0) AT THE READ, never here.
        //
        // W14 (battle-derived-wire T18): readers exist but the channel is UNFED in production -- every
        // reader takes `loadoutSlotsChannel` as a defaulted 0, and no site supplies a nonzero value
        // (the Data-layer SetLoadout seam cannot reach a composed Derived). Reserved at 0, and listed
        // in DominanceGuard's reserved families so the coverage report says so.
        Register(new(DerivedStatChannels.LoadoutSlots, DerivedComposeKind.FlatSum, 0,
                     Class: StatClass.Pool, Unit: UnitClass.Count,
                     UnitClassNote: "Readers exist (LoadoutSet.EffectiveMaxSize via Validate, AutoEquip.Select, CapPolicy.EquippedSkillCap) but NO production site supplies a nonzero value -- the equip/atom layer that would grant an extend-slot item is unbuilt, so the channel is reserved at 0."));

        // H.9 -- siege AI targeting priority (base-defense/spec-siege-ai.md §5.20 rule 4). FlatSum,
        // no Cap: the -2..+2 bound is enforced by SiegeAi.EffectiveTier throwing out of range, not by
        // composition (Cap only clamps one-sided, wrong shape for a symmetric range).
        Register(new(DerivedStatChannels.AiAggression, DerivedComposeKind.FlatSum, 0,
                     Class: StatClass.Pool, Unit: UnitClass.GameUnits,
                     UnitClassNote: "Reader: IBattleView.AggressionOf (BattleRunState.cs, 2026-09-07) -> SiegeAiIntentSource -> SiegeAi.EffectiveTier."));
    }

    void RegisterCombatDefaults()
    {
        foreach (var entry in DerivedStatChannels.AllCombatChannelEntries)
        {
            // StatClass is required for every family -- it is a known, decided property (H.0/H.1).
            // UnitClass is OPTIONAL: only families with a nameable reader today are present in
            // CombatFamilyUnitClass (the original 12) -- the 16 new H.1 families have none yet, so
            // this must stay null rather than throw (§2.3, §2.7: no placeholder for a channel with no
            // consumer at registration time).
            var (cls, counterpartFamily) = DerivedStatChannels.CombatFamilyClassification[entry.Family];
            UnitClass? unit = DerivedStatChannels.CombatFamilyUnitClass.TryGetValue(entry.Family, out var u) ? u : null;
            string? counterpart = counterpartFamily is null ? null : $"{counterpartFamily}.{entry.Slot}";
            Register(new(entry.ChannelId, DerivedComposeKind.FlatSum, 0,
                         Class: cls, Unit: unit, CounterpartOf: counterpart));
        }
    }

    public void Register(DerivedStatDef def)
    {
        if (string.IsNullOrWhiteSpace(def.ChannelId))
            throw new ArgumentException("ChannelId required");
        _defs[def.ChannelId] = def;
        _allRegistered = null; // the cached view below is rebuilt on the next read
    }

    public bool IsKnown(string channelId) => TryGet(channelId, out _);

    public bool TryGet(string channelId, out DerivedStatDef def) =>
        _defs.TryGetValue(channelId, out def!);

    public DerivedStatDef GetRequired(string channelId)
    {
        if (!TryGet(channelId, out var def))
            throw new UnknownDerivedChannelException(channelId);
        return def;
    }

    public bool TryResolveChannel(string channelId, out DerivedStatDef def)
    {
        if (TryGet(channelId, out def!))
            return true;

        if (channelId.StartsWith("status.power.", StringComparison.Ordinal))
        {
            var counterpart = "status.resist." + channelId["status.power.".Length..];
            def = new DerivedStatDef(channelId, DerivedComposeKind.SumIncreased, 0,
                Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: counterpart);
            return true;
        }

        if (channelId.StartsWith("status.resist.", StringComparison.Ordinal))
        {
            var counterpart = "status.power." + channelId["status.resist.".Length..];
            def = new DerivedStatDef(channelId, DerivedComposeKind.SumIncreased, 0, _categoryResistCap,
                Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: counterpart);
            return true;
        }

        if (channelId.StartsWith("status.durationReduction.", StringComparison.Ordinal))
        {
            var counterpart = "status.duration." + channelId["status.durationReduction.".Length..];
            def = new DerivedStatDef(channelId, DerivedComposeKind.SumIncreased, 0,
                Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: counterpart);
            return true;
        }

        if (channelId.StartsWith("status.duration.", StringComparison.Ordinal))
        {
            var counterpart = "status.durationReduction." + channelId["status.duration.".Length..];
            def = new DerivedStatDef(channelId, DerivedComposeKind.SumIncreased, 0,
                Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: counterpart);
            return true;
        }

        if (channelId.StartsWith("status.intensityReduction.", StringComparison.Ordinal))
        {
            var counterpart = "status.intensity." + channelId["status.intensityReduction.".Length..];
            def = new DerivedStatDef(channelId, DerivedComposeKind.SumIncreased, 0,
                Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: counterpart);
            return true;
        }

        if (channelId.StartsWith("status.intensity.", StringComparison.Ordinal))
        {
            var counterpart = "status.intensityReduction." + channelId["status.intensity.".Length..];
            def = new DerivedStatDef(channelId, DerivedComposeKind.SumIncreased, 0,
                Class: StatClass.Contest, Unit: UnitClass.StatusPotencyPoints, CounterpartOf: counterpart);
            return true;
        }

        if (channelId.StartsWith("status.immune.", StringComparison.Ordinal)
            && !channelId.StartsWith("status.immuneReduction.", StringComparison.Ordinal))
        {
            def = new DerivedStatDef(channelId, DerivedComposeKind.MaxPriorityFlag, 0, 1,
                Class: StatClass.Pool, Unit: UnitClass.Flag);
            return true;
        }

        if (channelId.StartsWith("status.immuneReduction.", StringComparison.Ordinal))
        {
            def = new DerivedStatDef(channelId, DerivedComposeKind.MaxPriorityFlag, 0, 1,
                Class: StatClass.Pool, Unit: UnitClass.Flag);
            return true;
        }

        if (channelId.StartsWith("status.expose.", StringComparison.Ordinal))
        {
            // No StatClass/Unit: registered vocabulary with zero readers today (ssot-affixes.md flags
            // authoring it as RuntimeUnsupported) — §2.7 forbids inventing a placeholder classification
            // for a channel with no nameable consumer.
            def = new DerivedStatDef(channelId, DerivedComposeKind.SumIncreased, 0);
            return true;
        }

        def = null!;
        return false;
    }

    public void ValidateChannel(string channelId)
    {
        if (!TryResolveChannel(channelId, out _))
            throw new UnknownDerivedChannelException(channelId);
    }

    DerivedStatDef[]? _allRegistered;

    /// <summary>
    /// ⚡ Cached, and invalidated by <see cref="Register"/>. This used to be
    /// <c>_defs.Values.ToList()</c> — a fresh ~261-element list on EVERY read, and
    /// <c>DerivedComposer.Compose</c> reads it once per compose, which on the lawn is once per actor per
    /// hit. The registry is append-mostly (built once at bootstrap, then read), so caching the view and
    /// dropping it on a register is the whole fix.
    /// </summary>
    public IReadOnlyCollection<DerivedStatDef> AllRegistered =>
        _allRegistered ??= _defs.Values.ToArray();
}

public sealed class UnknownDerivedChannelException : Exception
{
    public string ChannelId { get; }

    public UnknownDerivedChannelException(string channelId)
        : base($"Unknown derived channel: {channelId}")
    {
        ChannelId = channelId;
    }
}
