namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `profile-schema` (module 2, CAI1.6, spec-profile-schema.md §1): the closed vocabularies a
/// profile's rows are built from. Each is an `enum` with a stable string form, parsed by name in
/// <see cref="CombatAiTuningLoader"/>. An unknown string throws, naming the value AND the key it
/// appeared under — never a silent skip (the failure mode `spec-mode-profile.md` calls out for mode
/// ids, and `SiegeTuningLoader` already practises it for `stanceDefault`).
/// </summary>
public sealed class CombatAiTuningRejection : Exception
{
    public CombatAiTuningRejection(string message) : base(message) { }
}

/// <summary>Ideal §6.1 step 1. Adding a ninth selector is a REVIEWED change: each member names a
/// concrete read through `IBattleView`, and a selector with no reader is a promise, not a vocabulary
/// entry. Eight members, pinned: `TargetSelector_has_eight_members`.</summary>
public enum TargetSelector
{
    Nearest = 0,           // PositionOf + GridDistance.Chebyshev -- StubIntentSource.cs:104-131
    SameLaneThenAdjacent,  // PositionOf row/col; the lawn's own shape, inert where PositionOf is null
    LowestHp,              // FactsOf(key).HpMilli
    HighestThreat,         // the scorer's IncomingThreatMilli term, inverted
    Objective,             // IBattleView.ObjectivePositionOf -- IBattleView.cs:73
    Self,                  // the deciding actor
    AllyLowestHp,          // SideOf(key) == mySide, then HpMilli
    AllyDowned,            // SideOf(key) == mySide and the downed predicate the place supplies
}

/// <summary>Two tiers today. A third ("dumb"/vanilla) is reserved by the ideal and deliberately NOT
/// registered: D6 defers vanilla PvZ unit control to a later program, and an unused enum member reads
/// as a promise. Two members, pinned: `AiTier_has_two`.</summary>
public enum AiTier { Smart = 0, Performance = 1 }

/// <summary>D5's key axis. `sim` is absent on purpose: the sim runtime (`RuntimeId.Sim`) resolves
/// effects, it declares no intents. Adding a place is a reviewed change. Four members, pinned:
/// `AiPlace_has_four`.</summary>
public enum AiPlace { Lawn = 0, Battle = 1, Delve = 2, Siege = 3 }

/// <summary>D5's second key axis. NOTHING IN THE REPO SUPPLIES A COMBAT ROLE TODAY, so `Default` is
/// the only role any actor resolves to in v1; the other three are the ideal's own named delve rows
/// (§6.2), reserved so a role source can land without a schema version bump. Four members, pinned:
/// `AiRole_has_four`.</summary>
public enum AiRole { Default = 0, Frontliner = 1, Support = 2, Striker = 3 }

/// <summary>Conditions about ONE actor's own facts — exactly what the shipped compiled predicate
/// already answers through `FactReader` at gate 5. A profile row that wants a broader condition names
/// an action filter instead and lets gate 5 do the work; this enum holds only what a row needs BEFORE
/// an action is chosen. Six members, pinned: `AiRowCondition_has_six`. Revisit trigger, stated rather
/// than silently deferred: if this reaches eight members, fold the fact half into
/// `ICompiledPredicate`.</summary>
public enum AiRowCondition
{
    Always = 0,
    SelfHpBelowMilli,        // FactsOf(self).HpMilli
    SelfResourceBelowMilli,  // the pool read the reserve floor already uses
    TargetHpBelowMilli,      // FactsOf(target).HpMilli
    HasStatus,               // FactsOf(self).StatusMask
    TargetHasStatus,         // FactsOf(target).StatusMask
}

/// <summary>Census conditions — questions about the BOARD, which `FactReader` structurally cannot
/// answer because it is constructed per (self, target) pair. Exactly four real conditions plus `None`,
/// each answered from `IBattleView.LiveActorKeys` + `SideOf` at O(live), gathered ONCE per decision by
/// the caller, never per row. Five members, pinned: `AiCensusCondition_has_five`.</summary>
public enum AiCensusCondition
{
    None = 0,
    EnemiesAtLeast,
    EnemiesAtMost,
    AlliesDownedAtLeast,
    RoundAtLeast,
}

/// <summary>Module 3 (`ai-tiers-personality`) owns what each axis DOES; this module owns that the
/// list is closed and append-only. Append-only matters for determinism: module 3 draws one value per
/// axis in declaration order from one seeded stream, so inserting a member in the middle would
/// reshuffle every actor's personality. Four members, pinned: `PersonalityAxis_has_four`.</summary>
public enum PersonalityAxis { Aggression = 0, Recklessness = 1, Focus = 2, Thrift = 3 }

/// <summary>D6's actor-class axis (unique creature vs. general creature). Declared here (not in
/// `ai-tiers-personality`, module 3) because <see cref="CombatAiProfile"/>'s `TierByActorClass` map
/// needs the type to compile now — module 2 depends on module 1 only, and module 3 depends on both, so
/// the type has to live at or before module 2. Module 3 owns what a tier DOES with an actor's class
/// (`AiTierResolver.For`) and asserts this enum's own member count as ITS acceptance
/// (`AiActorClass_has_two_members`), not this module's — this module only needs the map to exist and
/// be total over it.</summary>
public enum AiActorClass { Unique = 0, General = 1 }

/// <summary>Parse-by-name helpers shared by <see cref="CombatAiTuningLoader"/>: an unknown string
/// throws naming the value and the key it appeared under, never a silent `TryParse` shrug.</summary>
static class AiVocabularyParse
{
    public static TEnum Enum<TEnum>(string key, string value) where TEnum : struct, Enum
    {
        if (System.Enum.TryParse<TEnum>(value, ignoreCase: true, out var result) &&
            System.Enum.IsDefined(typeof(TEnum), result))
            return result;
        throw new CombatAiTuningRejection(
            $"combat-ai tuning: '{key}' has unknown {typeof(TEnum).Name} value '{value}'");
    }
}
