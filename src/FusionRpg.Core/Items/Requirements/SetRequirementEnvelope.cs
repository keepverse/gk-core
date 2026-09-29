using FusionRpg.Core.Items.Thresholds;

namespace FusionRpg.Core.Items.Requirements;

/// <summary>One member's frozen profile plus the role it was resolved for — the envelope's own
/// <c>memberProfiles</c> row, kept as a list so the canonical (role-ordinal) order is preserved and
/// assertable rather than lost to a dictionary's hash order.</summary>
public readonly record struct SetMemberProfile(ItemRole Role, RequirementProfile Profile);

/// <summary>
/// item/spec-set-requirement-reconciliation.md §"Frozen set envelope": the one frozen contract a set's
/// members are minted against. Everything here is catalog-time data — a player's inventory, drop
/// order, currently equipped pieces and model availability are never inputs.
///
/// <para><b>One direction per set.</b> <see cref="FavoredAptitudeId"/> and
/// <see cref="UpkeepResourceId"/> are singular by construction: every non-empty build clause in
/// <see cref="Members"/> names the same aptitude and every upkeep clause the same resource and
/// period. That is the whole reconciliation — independently generated member requirements can no
/// longer demand two incompatible builds or two maintenance pools.</para>
///
/// <para><see cref="UpkeepCostTotal"/> is the tuning-owned ceiling the members' own costs sum within;
/// <see cref="MemberUpkeepTotal"/> is that sum, computed rather than stored so the two can never
/// disagree.</para>
/// </summary>
public sealed record SetRequirementEnvelope(
    string SetId,
    int ResolverRevision,
    long CatalogRevision,
    int TuningRevision,
    SetIdentityRequirement IdentityRequirement,
    IReadOnlyList<SetMemberProfile> Members,
    string? FavoredAptitudeId,
    long? FixedCeiling,
    long? RatioCeilingMilli,
    string? UpkeepResourceId,
    long UpkeepCostTotal,
    long UpkeepReserve,
    long UpkeepPeriodTicks)
{
    /// <summary>Every member's role, ordinally sorted — the canonical order the resolver drew in and
    /// the order an input shuffle must not be able to change.</summary>
    public IReadOnlyList<ItemRole> Roles => Members.Select(m => m.Role).ToList();

    /// <summary>The members' declared upkeep costs, summed in <c>checked</c> `long` arithmetic.
    /// Overflow throws rather than wrapping.</summary>
    public long MemberUpkeepTotal
    {
        get
        {
            long total = 0;
            foreach (var member in Members)
                if (member.Profile.Upkeep is { } upkeep)
                    checked { total += upkeep.Cost; }
            return total;
        }
    }

    public RequirementProfile? ProfileFor(ItemRole role)
    {
        foreach (var member in Members)
            if (member.Role == role) return member.Profile;
        return null;
    }
}

/// <summary>
/// Rule 3 of the spec's §Validation: no base type may be a member of two restrictive sets with
/// different identity requirements — "this prevents an arbitrary set-membership ordering from
/// changing who may equip one item". Blocked before member seeds are written, never repaired by
/// picking one set.
/// </summary>
public static class SetIdentityMembership
{
    public static void RequireNoConflict(
        string containerId,
        IReadOnlyList<(string SetId, SetIdentityRequirement Requirement)> memberships)
    {
        if (memberships is null) throw new ArgumentNullException(nameof(memberships));
        for (var i = 0; i < memberships.Count; i++)
            for (var j = i + 1; j < memberships.Count; j++)
            {
                var a = memberships[i];
                var b = memberships[j];
                if (a.Requirement.Class == SetClass.General || b.Requirement.Class == SetClass.General) continue;
                if (Same(a.Requirement, b.Requirement)) continue;
                throw new SetRequirementUnsatisfiable("set-identity-membership-conflict",
                    $"base type '{containerId}' is a member of both '{a.SetId}' and '{b.SetId}', which declare different restrictive identity requirements");
            }
    }

    static bool Same(in SetIdentityRequirement a, in SetIdentityRequirement b) =>
        a.Class == b.Class
        && string.Equals(a.RequiredFamilyId, b.RequiredFamilyId, StringComparison.Ordinal)
        && string.Equals(a.RequiredSpeciesId, b.RequiredSpeciesId, StringComparison.Ordinal)
        && a.RequiresUniqueCreature == b.RequiresUniqueCreature
        && a.HybridEligibility == b.HybridEligibility;
}
