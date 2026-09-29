using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Battle;

/// <summary>
/// How a trait acts in web battles. Funnel-routed traits are stat modifiers and HP mutations on
/// the battle funnel; engine behaviors (targeting, retreat, report multipliers) are engine
/// semantics outside the FA vocabulary BY DESIGN — FA opcodes describe board operations, not
/// battle AI (creature-standalone-plan.md §Refinement). Contracts later layers obedience on the
/// same trait keys.
/// </summary>
public enum TraitBattleMechanism
{
    FunnelRouted,
    EngineBehavior
}

/// <summary>One trait's battle semantics — integer per-mille params; 0 = facet unused.</summary>
public sealed record TraitBattleDef
{
    public string TraitId { get; init; } = "";
    public TraitBattleMechanism Mechanism { get; init; }

    /// <summary>Static derived-channel adjustments merged at compose time (e.g. crit rate).</summary>
    public IReadOnlyList<BattleChannelMod> ChannelMods { get; init; } = Array.Empty<BattleChannelMod>();

    /// <summary>
    /// combat-unification **Wave E1** — statuses an actor with this trait applies to whoever it LANDS
    /// a hit on. Reuses <see cref="BattleStatusSpec"/> unchanged: the wave's own spec says "rider
    /// grammar matches `BattleStatusSpec`", so a rider is the same status grammar attached at a
    /// different moment, not a new vocabulary.
    ///
    /// <para><b>Why riders live on the trait def and not on `BattleActorSetup`.</b> The wave's spec
    /// offers both ("rider specs on setups/traits") and then settles it: "Trait-sourced riders come
    /// from `TraitBattleCatalog` rows, not engine branches." The measured reason to prefer it: a new
    /// property on `BattleActorSetup` lands inside the serialized `BattleSetup` that
    /// `ExpeditionBattlePlan` hashes, so it moves all four expedition tier goldens for a purely
    /// structural reason — verified by trying it, at which point 35 battle goldens stayed green and
    /// only the expedition hash moved. A catalog row is not serialized and moves nothing.</para>
    ///
    /// <para>Empty for every shipped trait, which is the wave's byte-identity invariant.</para>
    /// </summary>
    public IReadOnlyList<BattleStatusSpec> OnHitRiders { get; init; } = Array.Empty<BattleStatusSpec>();

    public int BerserkRampHalfMilli { get; init; }      // extra damage below 50% own HP
    public int BerserkRampQuarterMilli { get; init; }   // extra damage below 25% own HP
    public int RegenPerRoundMilli { get; init; }        // of MaxHp, healed each round
    public int OnKillHealMilli { get; init; }           // of MaxHp, healed per kill
    public int GuardShareMilli { get; init; }           // damage share pulled off an adjacent ally
    public int InitiativeBonusMilli { get; init; }      // subtracted from the initiative roll
    public int DeathRefusalCharges { get; init; }       // survive-at-1 charges per battle
    public int RetreatBelowMilli { get; init; }         // of MaxHp — leave the battle alive below this
    public bool TargetsLowestHp { get; init; }
    public bool GuardsAdjacentAlly { get; init; }       // full redirect of hits aimed at the neighbor
    public int SoulLootBonusMilli { get; init; }        // battle-level Souls multiplier (report)
    public int SpecimenXpBonusMilli { get; init; }      // per-actor XP multiplier (report)
    public int EssenceProcMilli { get; init; }          // rider proc chance per landed hit
    public int EssenceRiderMilli { get; init; }         // rider damage, ‰ of the landed hit
}

public static class TraitBattleMath
{
    /// <summary>Berserker damage multiplier (‰) from own HP — staged, integer-only.</summary>
    public static int BerserkerRampMilli(TraitBattleDef def, long hp, long maxHp)
    {
        if (maxHp <= 0) return 1000;
        if (hp * 4 < maxHp) return 1000 + def.BerserkRampQuarterMilli;
        if (hp * 2 < maxHp) return 1000 + def.BerserkRampHalfMilli;
        return 1000;
    }
}

/// <summary>Config-backed (tunables-ssot.md T1) — gk-core/data/tuning/battle.v1.json's traits map. Trait
/// ids/mechanisms/ChannelMods stay here (schema); every Milli/Charges magnitude is loaded.</summary>
public static class TraitBattleCatalog
{
    static BattleTuning? _tuning;

    public static void Configure(BattleTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static BattleTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "TraitBattleCatalog.Configure(...) has not run. Every trait's magnitudes read " +
        "data/tuning/battle.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");

    static IReadOnlyList<TraitBattleDef>? _all;

    public static IReadOnlyList<TraitBattleDef> All => _all ??= Build();

    static IReadOnlyList<TraitBattleDef> Build()
    {
        TraitBattleDef Of(string id, TraitBattleMechanism mechanism, bool targetsLowestHp = false, bool guardsAdjacentAlly = false) =>
            Merge(id, mechanism, targetsLowestHp, guardsAdjacentAlly);

        return new TraitBattleDef[]
        {
            // Funnel-routed — stat mods + HP mutations through the battle-local EffectFunnel.
            Of("berserker", TraitBattleMechanism.FunnelRouted),
            Of("regenerator", TraitBattleMechanism.FunnelRouted),
            Of("soul-eater", TraitBattleMechanism.FunnelRouted),
            new()
            {
                TraitId = "critical-hunter", Mechanism = TraitBattleMechanism.FunnelRouted,
                // MIGRATED 2026-08-23 (E12). Its magnitude is now a row — `atom.critical-hunter.t1`, a
                // stat.derived on combat.crit.rate.omni — reached through `TraitAtomSource`. The entry
                // stays so the trait id, mechanism and the other thirteen keep their shape; only the
                // magnitude moved. Measured delta: zero, across every battle golden.
                ChannelMods = Array.Empty<BattleChannelMod>()
            },
            Of("guardian", TraitBattleMechanism.FunnelRouted),
            // swift/berserker sit in the Funnel-routed HALF of the plan's locked 7/7 split even
            // though their mechanics run engine-side (initiative math, damage multiplier) — the
            // split classifies which traits the contracts module later layers obedience onto,
            // not the literal code path of every parameter.
            Of("swift", TraitBattleMechanism.FunnelRouted),
            Of("immortal", TraitBattleMechanism.FunnelRouted),

            // Engine-native behaviors — outside the FA vocabulary by design.
            Of("coward", TraitBattleMechanism.EngineBehavior),
            Of("bloodthirsty", TraitBattleMechanism.EngineBehavior, targetsLowestHp: true),
            Of("loyal", TraitBattleMechanism.EngineBehavior, guardsAdjacentAlly: true),
            Of("greedy", TraitBattleMechanism.EngineBehavior),
            Of("genius", TraitBattleMechanism.EngineBehavior),
            Of("void-touched", TraitBattleMechanism.EngineBehavior),
            Of("chaos-marked", TraitBattleMechanism.EngineBehavior)
        };
    }

    static TraitBattleDef Merge(string id, TraitBattleMechanism mechanism, bool targetsLowestHp, bool guardsAdjacentAlly)
    {
        var m = Tuning.TraitOf(id);
        return new TraitBattleDef
        {
            TraitId = id, Mechanism = mechanism,
            BerserkRampHalfMilli = m.BerserkRampHalfMilli, BerserkRampQuarterMilli = m.BerserkRampQuarterMilli,
            RegenPerRoundMilli = m.RegenPerRoundMilli, OnKillHealMilli = m.OnKillHealMilli,
            GuardShareMilli = m.GuardShareMilli, InitiativeBonusMilli = m.InitiativeBonusMilli,
            DeathRefusalCharges = m.DeathRefusalCharges, RetreatBelowMilli = m.RetreatBelowMilli,
            TargetsLowestHp = targetsLowestHp, GuardsAdjacentAlly = guardsAdjacentAlly,
            SoulLootBonusMilli = m.SoulLootBonusMilli, SpecimenXpBonusMilli = m.SpecimenXpBonusMilli,
            EssenceProcMilli = m.EssenceProcMilli, EssenceRiderMilli = m.EssenceRiderMilli,
            OnHitRiders = m.OnHitRiders ?? Array.Empty<BattleStatusSpec>()
        };
    }

    public static bool IsKnown(string? traitId) =>
        traitId != null && All.Any(t => string.Equals(t.TraitId, traitId, StringComparison.Ordinal));

    public static TraitBattleDef Get(string traitId) =>
        All.FirstOrDefault(t => string.Equals(t.TraitId, traitId, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Unknown battle trait id '{traitId}'.");
}
