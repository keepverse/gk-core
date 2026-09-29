namespace FusionRpg.Core.Actions;

/// <summary>
/// Gate 3's seam (spec-usability-conditions.md §1a): "a no-op until `A3` lands." The build order is
/// `A1 → A2 → A4 → A5 → A3`, so this module ships before the cost table exists. `AlwaysAffordable`
/// is the seam implementation; `A3`'s `ResourceLedger` supplies the real one behind this interface.
///
/// <para>Reordering `A3` earlier would be worse: it would put the cost system inside `A5`'s
/// byte-identity gate, for an action that has no costs. The seam is the cheaper answer.</para>
/// </summary>
public interface IAffordabilityCheck
{
    UsabilityResult Check(string actorKey, string actionId);
}

/// <summary>Affordable until `A3` lands. Never used once the real cost reader exists.</summary>
public sealed class AlwaysAffordable : IAffordabilityCheck
{
    public static readonly AlwaysAffordable Instance = new();
    public UsabilityResult Check(string actorKey, string actionId) => UsabilityResult.Usable;
}

/// <summary>
/// Gate 0's seam (spec-usability-conditions.md §1a): whether <paramref name="actorKey"/> is
/// mid-stance and, if so, whether <paramref name="actionId"/> is the valid release. `A8` (Phase 7)
/// supplies the real implementation; until then no actor is ever mid-stance.
/// </summary>
public interface IStanceCheck
{
    /// <summary>Null means "gate 0 does not refuse" — proceed to gate 1.</summary>
    UsabilityResult? Check(string actorKey, string actionId);
}

/// <summary>No stance system yet — gate 0 never refuses. Never used once `A8` ships.</summary>
public sealed class NoStanceHeld : IStanceCheck
{
    public static readonly NoStanceHeld Instance = new();
    public UsabilityResult? Check(string actorKey, string actionId) => null;
}
