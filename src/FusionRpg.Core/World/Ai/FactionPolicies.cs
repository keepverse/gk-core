namespace FusionRpg.Core.World.Ai;

/// <summary>
/// Which brains exist (spec-ai-commander.md §The decision layer).
///
/// A catalog like every other one in this module, and for the same reason: a faction's
/// <c>PolicyId</c> is a reference, it is inside the state hash, and a typo in a template must be a
/// startup error rather than a faction that silently never plays. <see cref="WorldValidation"/>
/// checks every world against this list.
/// </summary>
public static class FactionPolicies
{
    static readonly IReadOnlyDictionary<string, IFactionPolicy> ById =
        new Dictionary<string, IFactionPolicy>(StringComparer.Ordinal)
        {
            [StandFastPolicy.Id] = StandFastPolicy.Instance,
            [FrontierRulesPolicy.Id] = FrontierRulesPolicy.Instance
        };

    /// <summary>In ordinal id order, so anything that enumerates policies is reproducible.</summary>
    public static IReadOnlyList<string> All { get; } =
        ById.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();

    public static bool IsKnown(string? policyId) => policyId != null && ById.ContainsKey(policyId);

    /// <summary>
    /// Throws rather than returning null. A null would read as "this faction has no brain", which is
    /// indistinguishable from the human — and a typo would then look like a design decision for the
    /// rest of the campaign.
    /// </summary>
    public static IFactionPolicy Resolve(string policyId) =>
        ById.TryGetValue(policyId, out var policy)
            ? policy
            : throw new KeyNotFoundException($"No faction policy '{policyId}'.");
}
