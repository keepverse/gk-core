using FusionRpg.Core.Items;

namespace FusionRpg.Core.Items.Activation;

/// <summary>
/// item/spec-equipment-activation.md §"Deployment-run status": the key one activation status is held
/// under — <c>(deploymentKey, specimenId, role, refKind, refId)</c>. It is exactly the durable
/// assignment's own identity (<see cref="EquipAssignment"/>, module 4) plus the host's opaque
/// deployment key, so an activation row can never be read against a different assignment or a
/// different run.
///
/// <para><b>The deployment key is opaque to Core.</b> Nothing here parses it, prefixes it, or infers
/// a runtime from its shape: it identifies a lawn board by <c>MatchKey</c>, a standalone battle by its
/// run key, a Delve deployment by its run key across all rooms, or a siege engagement by its
/// resolver-issued key. Core only requires that it is non-empty and that two different runs never
/// share one.</para>
///
/// <para>Equality and ordering are ordinal throughout, so the canonical processing order
/// (specimenId, role, refKind, refId) is stable across hosts and locales — a culture-sensitive
/// comparison here would reorder contention and silently change which item pays first.</para>
/// </summary>
public readonly struct EquipmentAssignmentIdentity : IEquatable<EquipmentAssignmentIdentity>
{
    public EquipmentAssignmentIdentity(
        string deploymentKey, string specimenId, string role, string refKind, string refId)
    {
        DeploymentKey = Require(deploymentKey, nameof(deploymentKey));
        SpecimenId = Require(specimenId, nameof(specimenId));
        Role = Require(role, nameof(role));
        RefKind = Require(refKind, nameof(refKind));
        RefId = Require(refId, nameof(refId));
    }

    /// <summary>The host's opaque deployment key — never parsed by Core.</summary>
    public string DeploymentKey { get; }

    /// <summary>The wearing specimen's stable <c>instance_id</c> (the durable actor key).</summary>
    public string SpecimenId { get; }

    /// <summary>The item role's kebab-case registry id (<c>ItemRoles.Id</c>), never an enum name.</summary>
    public string Role { get; }

    /// <summary><c>"rolled"</c> or <c>"stock"</c> (<see cref="EquipRefKinds"/>).</summary>
    public string RefKind { get; }

    /// <summary>The pinned copy's instance id (<c>rolled</c>) or the catalog container id
    /// (<c>stock</c>).</summary>
    public string RefId { get; }

    /// <summary>The durable assignment's own identity inside a deployment. This is the ONLY
    /// construction path a host needs — it never hand-assembles the five parts.</summary>
    public static EquipmentAssignmentIdentity Of(string deploymentKey, EquipAssignment assignment)
    {
        if (assignment is null) throw new ArgumentNullException(nameof(assignment));
        return new EquipmentAssignmentIdentity(
            deploymentKey, assignment.SpecimenId, ItemRoles.Id(assignment.Role), assignment.RefKind, assignment.RefId);
    }

    /// <summary>
    /// The canonical contention order the scheduler processes due payments in: specimen, then role,
    /// then ref kind, then ref id, all ordinal. The deployment key is deliberately NOT part of the
    /// comparison — one scheduler call is scoped to one deployment, so comparing it would only add a
    /// term that never varies.
    /// </summary>
    public static readonly IComparer<EquipmentAssignmentIdentity> CanonicalOrder = Comparer<EquipmentAssignmentIdentity>.Create(
        (a, b) =>
        {
            var bySpecimen = string.CompareOrdinal(a.SpecimenId, b.SpecimenId);
            if (bySpecimen != 0) return bySpecimen;
            var byRole = string.CompareOrdinal(a.Role, b.Role);
            if (byRole != 0) return byRole;
            var byKind = string.CompareOrdinal(a.RefKind, b.RefKind);
            if (byKind != 0) return byKind;
            return string.CompareOrdinal(a.RefId, b.RefId);
        });

    public bool Equals(EquipmentAssignmentIdentity other) =>
        string.Equals(DeploymentKey, other.DeploymentKey, StringComparison.Ordinal)
        && string.Equals(SpecimenId, other.SpecimenId, StringComparison.Ordinal)
        && string.Equals(Role, other.Role, StringComparison.Ordinal)
        && string.Equals(RefKind, other.RefKind, StringComparison.Ordinal)
        && string.Equals(RefId, other.RefId, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is EquipmentAssignmentIdentity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(DeploymentKey),
        StringComparer.Ordinal.GetHashCode(SpecimenId),
        StringComparer.Ordinal.GetHashCode(Role),
        StringComparer.Ordinal.GetHashCode(RefKind),
        StringComparer.Ordinal.GetHashCode(RefId));

    public static bool operator ==(EquipmentAssignmentIdentity a, EquipmentAssignmentIdentity b) => a.Equals(b);

    public static bool operator !=(EquipmentAssignmentIdentity a, EquipmentAssignmentIdentity b) => !a.Equals(b);

    public override string ToString() =>
        $"{DeploymentKey}/{SpecimenId}/{Role}/{RefKind}/{RefId}";

    static string Require(string value, string what) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"an assignment identity part is required ('{what}')", what)
            : value;
}
