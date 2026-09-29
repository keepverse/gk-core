using System.Linq;
using FusionRpg.Core.Status;

namespace FusionRpg.Core.Actions.Duration;

public sealed class TurnAuthoredDurationRejection : Exception
{
    public TurnAuthoredDurationRejection(string statusId)
        : base($"status '{statusId}' is not control-family (StatusL2bCategory.Cc) — only control may author a duration in victim turns; DoT and buff families resolve in ticks") { }
}

/// <summary>
/// T28 (spec-duration-resolver.md §2): "only control uses the relative form, because only control
/// removes agency." DoT/debuff and buff/stance families get no relative-turn authoring at all — "no
/// tick constant" would be machinery for two families that never had the failure-mode a stolen turn
/// creates. Enforced here rather than left as a convention: a status whose categories do not include
/// <see cref="StatusL2bCategory.Cc"/> is rejected outright if anything tries to author its duration in
/// victim turns.
/// </summary>
public static class DurationAuthoringGuard
{
    public static void RequireControlFamily(string statusId, IReadOnlyList<string> categories)
    {
        if (!categories.Contains(StatusL2bCategory.Cc, StringComparer.Ordinal))
            throw new TurnAuthoredDurationRejection(statusId);
    }
}
