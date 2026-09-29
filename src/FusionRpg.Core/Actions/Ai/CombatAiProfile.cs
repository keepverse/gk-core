using FusionRpg.Core.Actions;

namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `profile-schema` (module 2, CAI1.6, spec-profile-schema.md §2): one place x role policy.
/// The ROWS are data (content grows them); `AiVocabulary.cs`'s closed vocabularies are declarations.
/// That distinction is what the tests assert and what they refuse to assert.
/// </summary>
public sealed record CombatAiProfile(
    string ProfileId,                       // "siege/default" -- the key, echoed for provenance/trace
    AiPlace Place,
    AiRole Role,
    AiTier? TierOverride,                   // null => the tier-by-actor-class map below decides
    IReadOnlyDictionary<AiActorClass, AiTier> TierByActorClass,
    IReadOnlyList<AiProfileRow> Rows,       // ordered; index IS the rank, lower wins
    AiScoringBlock Scoring,
    AiSelectionBlock Selection,
    IReadOnlyList<AiReserveFloor> Reserves, // per resource id; empty = no floor
    AiWasteGuards Guards,
    AiAntiRepeat AntiRepeat,
    AiTriggerBlock? Trigger,                // real-time places only; null for turn places
    AiPersonalityBounds Personality);

/// <summary>Rank is the row's INDEX, not a stored int. Two rows cannot tie, and a rank cannot be
/// skipped — both are classes of authoring bug that a stored rank invites and a list forbids by
/// construction.</summary>
public sealed record AiProfileRow(
    TargetSelector Selector,
    AiRowCondition Condition, int ConditionArgMilli, string ConditionArgId,
    AiCensusCondition Census, int CensusArg,
    AiActionFilter Actions);

/// <summary>"by tag, family or rung" (ideal §6.1 Layer C). All three are optional; an empty filter
/// admits every held action, which is `StubIntentSource`'s behaviour and therefore the identity. Pure
/// data: interpreting it into the `Func&lt;CompiledAction,bool&gt;` seam `ActionStage.TryPick` takes
/// is `ai-tiers-personality`'s (module 3) `CoreIntentPolicy`, the first caller that holds both a real
/// profile row and the action stage.
///
/// <para><b>Named `RungAtLeast`/`RungAtMost`, deliberately not the "min/max rung" spelling</b>
/// spec-profile-schema.md's own code sample uses: `RungSemanticsTests` (action-skill-tiers
/// `rung-semantics`, A-U1) pins that a specific reserved rung-floor spelling exists nowhere in
/// `src/`/`data/` except one unrelated aura-ladder constant, specifically to catch a DIFFERENT
/// rung-window defect class (author/holder/guard disagreeing about what a rung window means). This
/// filter is a different concept (admit a held action by ITS rung range, never a content row's
/// authored window) and matches this same module's own `EnemiesAtLeast`/`EnemiesAtMost` naming, so
/// the rename costs nothing and keeps that guard meaningful.</para></summary>
public sealed record AiActionFilter(
    IReadOnlyList<ActionTag>? Tags,
    IReadOnlyList<string>? Families,
    int? RungAtLeast, int? RungAtMost);

/// <summary>The scoring half of a profile — projects one-to-one onto module 1's `ScoringWeights`. The
/// projection is positional, so a reviewer can check it by eye — the same property module 1 relies on
/// for the `AiCandidate` -> `TargetCandidate` copy.</summary>
public sealed record AiScoringBlock(
    int WeightHitChance, int WeightObjective, int WeightKill, int WeightLowHp,
    int WeightCannotCounter, int WeightRound, int WeightRisk,
    int AggressionRange,        // STRUCTURAL: the range IS the taunt/stealth/decoy vocabulary
    int MaxCandidatesScored)    // STRUCTURAL: a per-decision work bound, not a progression ceiling
{
    /// <summary>The positional projection CAI1.8's production wiring (`BattleRunState`) reads.</summary>
    public ScoringWeights ToScoringWeights() => new(
        WeightHitChance, WeightObjective, WeightKill, WeightLowHp, WeightCannotCounter, WeightRound,
        WeightRisk, AggressionRange, MaxCandidatesScored);
}

/// <summary>Projects one-to-one onto module 1's `SelectionPolicy`.</summary>
public sealed record AiSelectionBlock(SelectionMode Mode, int KeepPctMilli, string RngStreamName)
{
    public SelectionPolicy ToSelectionPolicy() => new(Mode, KeepPctMilli, RngStreamName);
}

public sealed record AiWasteGuards(int MinTargetsForArea, int KillMarginMilli, int FightEndingLiveCount)
{
    /// <summary>The positional projection onto module 1's `WasteGuardThresholds` — same field shape,
    /// same "off" sentinels (`spec-core-scorer.md`'s own identity, `WasteGuardThresholds.Off`), read by
    /// `ai-tiers-personality`'s (module 3) `CoreIntentPolicy` at gate 3 of the action stage.</summary>
    public WasteGuardThresholds ToWasteGuardThresholds() => new(MinTargetsForArea, KillMarginMilli, FightEndingLiveCount);
}

public sealed record AiAntiRepeat(long RetargetLatencyTicks, long CommitmentBonus, int RepeatDecayHalfLifeTicks);

/// <summary>Real-time places only. THREE fields, not five: the per-frame decision budget and the
/// cast-token pool are per-frame/runtime work caps and live as code `const`s in `lawn-cast-trigger`
/// (module 19, `spec-lawn-cast-trigger.md` §Tunables) — a value on the balance surface is editable by
/// a balance pass whatever a table calls it, and a schema field for a number that lives in code is the
/// dead-config shape the ideal §8 forbids, so neither is reserved here (plan correction 2).</summary>
public sealed record AiTriggerBlock(int SwingsN, long TicksT, long PostCastLockL);

public sealed record AiPersonalityBounds(IReadOnlyDictionary<PersonalityAxis, int> BoundByAxis);

/// <summary>The whole file, parsed. `SchemaVersion` is the SHAPE (a reader change); `Version` is the
/// publish counter `publish.py` bumps (a balance change). `replay-identity` (module 8) stamps
/// `Version` into the match row, which is why the two are separate fields and not one number.</summary>
public sealed record CombatAiTuning(
    int SchemaVersion,
    int Version,
    IReadOnlyDictionary<string, CombatAiProfile> Profiles,   // keyed "<place>/<role>"; "*/default" required
    AiRouterBlock Router);                                   // module 4's two keys

/// <summary>Owned by `intent-router` (module 4) — it is the only reader — but DECLARED here, because
/// this module owns the file's shape and a parser cannot parse a block no record names.
/// `MaxLiveOrdersPerActor` is deliberately NOT here — module 4 keeps it a code `const` because a
/// second live order is a queue, a design change, not a number.</summary>
public sealed record AiRouterBlock(long OrderTimeoutTicks, int ReactionsPerRoundExpected);
