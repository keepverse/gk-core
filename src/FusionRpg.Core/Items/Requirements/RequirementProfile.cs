namespace FusionRpg.Core.Items.Requirements;

/// <summary>The closed profile-kind vocabulary (`item/spec-requirement-profiles.md` contract) — a new
/// kind is a reviewed change to the resolver, the evaluator, and the matrix together, never one of
/// the three alone.</summary>
public enum RequirementProfileKind
{
    None = 0,
    Level = 1,
    Fixed = 2,
    Ratio = 3,
    Sustained = 4,
    Jackpot = 5,
}

/// <summary>One aptitude trial clause — fixed XOR ratio, never both, never neither when present.
/// Points are `long` magnitudes; the share is per-mille with the structural denominator 1000.</summary>
public sealed record BuildTrialClause(string AptitudeId, long? MinimumPoints, long? MinimumShareMilli);

/// <summary>Durable upkeep data (module 24 reads it; nothing here charges it). Amounts are `long`
/// magnitudes; the period is ticks.</summary>
public sealed record UpkeepClause(string ResourceId, long Reserve, long Cost, long PeriodTicks);

/// <summary>The frozen profile: an instance fact resolved once at mint, replayable from its inputs,
/// never re-resolved on move, reload, or re-evaluation. `levelTrial`, `buildTrial`, and `upkeep`
/// are composable fields, not competing runtime modes; version 1 carries at most one aptitude id.</summary>
public sealed record RequirementProfile(
    int? MinimumLevel,
    BuildTrialClause? BuildTrial,
    UpkeepClause? Upkeep,
    RequirementProfileKind ProfileKind);

/// <summary>A trial reports a route to activation; it is never an equip refusal.</summary>
public enum TrialUnmetClause
{
    None = 0,
    Level = 1,
    FixedAptitude = 2,
    RatioAptitude = 3,
}

public sealed record TrialEvaluation(bool Ready, TrialUnmetClause Unmet);

/// <summary>Named rejections — a failed resolution returns one of these, never a fallback profile
/// and never a partial persistence.</summary>
public sealed class RequirementProfileRejection : Exception
{
    public RequirementProfileRejection(string rule, string detail)
        : base($"requirement profile rejected — {rule}: {detail}")
    {
        Rule = rule;
    }

    public string Rule { get; }
}
