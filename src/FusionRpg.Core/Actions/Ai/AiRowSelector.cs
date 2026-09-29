namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `profile-schema` (module 2, CAI1.6, spec-profile-schema.md §4): everything a row
/// condition can ask, gathered ONCE before the walk. Four census numbers, not a board:
/// <see cref="AiCensusCondition"/> is exactly five members (including `None`), so this record is total
/// over it and a sixth member fails the build here rather than defaulting to "condition false".
///
/// <para><b>"No target held" convention</b> (§4 rule 3): when the actor has no held target (no prior
/// decision's `RetargetLedger` entry), the caller passes <see cref="TargetHpMilli"/> = `int.MaxValue`
/// and <see cref="TargetStatusMask"/> = `0`, so `TargetHpBelowMilli` and `TargetHasStatus` are
/// mechanically false for ANY authored threshold — the walk needs no separate "is there a target"
/// flag, and a row that cannot be evaluated has not matched rather than throwing.</para>
///
/// <para><b>Status conditions</b> (`HasStatus`/`TargetHasStatus`): `ConditionArgMilli` is read as the
/// status bit index (0..63) to test in the mask; `ConditionArgId` carries the human-readable status id
/// for tracing only — the walk itself never resolves a string id to a bit (that registry, if one is
/// ever needed, is a future caller's, matching this type's own "no view read" contract).</para>
///
/// <para><b>The two masks are <c>ulong</c> — the type their source actually is.</b> They come from
/// <c>EntityFacts.StatusMask</c> (<c>Effects/Atoms/FactReader.cs</c>), which is <c>ulong</c>, and a status
/// mask is a **bitfield**, not a magnitude: reinterpreting it as <c>long</c> preserves every bit but loses
/// the type and forces an <c>unchecked</c> cast at the one construction site
/// (<c>CoreIntentPolicy.BuildRowFacts</c>) — precisely what the overflow audit's A6 rule reads as a wrap on
/// a magnitude path. Carrying the real type costs nothing: the bit tests at <c>ConditionHolds</c> shift
/// <c>1UL</c>, and AND-then-<c>!= 0</c> is bit-identical for every shift count, because C# masks a 64-bit
/// shift by 63 either way.</para>
/// </summary>
public readonly record struct AiRowFacts(
    int SelfHpMilli, int SelfResourceMilli, string SelfResourceId,
    int TargetHpMilli, ulong SelfStatusMask, ulong TargetStatusMask,
    int EnemiesLive, int AlliesDowned, int Round)
{
    /// <summary>The "no target held" sentinel for <see cref="TargetHpMilli"/> — see this type's own
    /// summary. Never a magic literal at a call site: every caller reads this field.</summary>
    public const int NoTargetHpMilli = int.MaxValue;
}

/// <summary>
/// combat-ai `profile-schema` (module 2, CAI1.6, spec-profile-schema.md §4): FF12's gambit rule, and
/// the ONLY place it is expressed — walk `Rows` in index order (the rank), take the FIRST row whose
/// `Condition` AND `Census` both hold, and hand its `Selector` + `Actions` to module 1's
/// `TargetStage`/`ActionStage`. Pure: the two census counts and the two fact reads are PASSED IN via
/// <see cref="AiRowFacts"/>, so this type never touches `IBattleView` and a caller cannot accidentally
/// make the walk O(rows x live).
///
/// <para>This is the ONLY rank walk in the repo — `core-scorer` (module 1) deliberately punts it
/// ("this module only guarantees one pass over held actions", spec-core-scorer.md §5), and a rank
/// vocabulary with no evaluator is a data shape nobody can execute.</para>
/// </summary>
public static class AiRowSelector
{
    public static bool TryPick(
        CombatAiProfile profile,
        in AiRowFacts facts,
        out TargetSelector selector,
        out AiActionFilter actions,
        out int rankIndex)
    {
        var rows = profile.Rows;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (ConditionHolds(row, facts) && CensusHolds(row, facts))
            {
                selector = row.Selector;
                actions = row.Actions;
                rankIndex = i;
                return true;
            }
        }

        selector = default;
        actions = EmptyFilter;
        rankIndex = -1;
        return false;
    }

    static readonly AiActionFilter EmptyFilter = new(null, null, null, null);

    static bool ConditionHolds(AiProfileRow row, in AiRowFacts facts) => row.Condition switch
    {
        AiRowCondition.Always => true,
        AiRowCondition.SelfHpBelowMilli => facts.SelfHpMilli < row.ConditionArgMilli,
        // False, not thrown, when the row asks about a different resource than the one supplied --
        // rule 3's "a row that cannot be evaluated has not matched" applies here too.
        AiRowCondition.SelfResourceBelowMilli =>
            string.Equals(facts.SelfResourceId, row.ConditionArgId, StringComparison.Ordinal) &&
            facts.SelfResourceMilli < row.ConditionArgMilli,
        AiRowCondition.TargetHpBelowMilli => facts.TargetHpMilli < row.ConditionArgMilli,
        AiRowCondition.HasStatus => (facts.SelfStatusMask & (1UL << row.ConditionArgMilli)) != 0,
        AiRowCondition.TargetHasStatus => (facts.TargetStatusMask & (1UL << row.ConditionArgMilli)) != 0,
        _ => throw new ArgumentOutOfRangeException(nameof(row), row.Condition, "unknown AiRowCondition"),
    };

    static bool CensusHolds(AiProfileRow row, in AiRowFacts facts) => row.Census switch
    {
        AiCensusCondition.None => true,
        AiCensusCondition.EnemiesAtLeast => facts.EnemiesLive >= row.CensusArg,
        AiCensusCondition.EnemiesAtMost => facts.EnemiesLive <= row.CensusArg,
        AiCensusCondition.AlliesDownedAtLeast => facts.AlliesDowned >= row.CensusArg,
        AiCensusCondition.RoundAtLeast => facts.Round >= row.CensusArg,
        _ => throw new ArgumentOutOfRangeException(nameof(row), row.Census, "unknown AiCensusCondition"),
    };
}
