using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Grants;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Grants;

/// <summary>
/// ⭐ <b>Wiring gap (b), closed.</b> <c>RpgStore.UpsertGrant</c> has been shipped and callable since T1
/// with <b>zero production callers</b>; <c>RpgStore.ListGrants</c> has been live in production the
/// whole time (<c>WebMatchService.EquippedActionIdsFor</c>). The pipe was connected at the far end and
/// nothing fed it. This is what feeds it.
///
/// <para><b>The scope is not a choice — it is about lifetime, not the loadout-preference reason this
/// comment used to give.</b> <c>WebMatchService.EquippedActionIdsFor</c> merges TWO owner scopes as of
/// 2026-09-07 (a side effect of `action-grant-owner-kind-durability`, which moved the UNLOCK-LADDER
/// grant read to <see cref="OwnerKind.UniqueActor"/> for its own, unrelated durability reason): that
/// scope for the durable unlock-ladder grant, and <see cref="OwnerKind.Entity"/> for this one. An
/// item's granted action must disappear the moment the item is unequipped, and <c>Entity</c> is
/// exactly the session-scoped kind the boot sweep's <c>ClearSessionScopedBindings()</c> already clears
/// on a session boundary — writing this at <c>UniqueActor</c> would make an item's grant outlive
/// unequipping it. <c>source</c> is the item's container id, so unassign is a delete-by-source against
/// the index that already exists (<c>ix_rpg_action_grant_source</c>).</para>
///
/// <para><b>Pure.</b> No persistence, no clock, no ambient state — the DAL half is
/// <c>RpgStore.ApplyEquippedGrants</c>, which does nothing but run this and write the result.</para>
/// </summary>
public static class EquippedGrantProjection
{
    /// <summary>
    /// The grant rows one assignment produces. A rebuild, never a delta — the same shape
    /// <see cref="EquipProjector"/> already chose, and it is what makes unequip atomic.
    /// </summary>
    /// <param name="assignment">One durable equip decision (module 4).</param>
    /// <param name="containerIdOf">Resolves the assignment's <c>ref_id</c> to the BASE TYPE's container
    /// id. Supplied by the caller because <c>ref_kind</c> is <c>"rolled"</c> (an
    /// <c>effect_instance.instance_id</c>, which must be walked back to its container) or
    /// <c>"stock"</c> (already a container id). A <c>null</c> return means "this assignment resolves to
    /// no base type" and yields no grants rather than a guess.</param>
    /// <param name="grantsOf">The enabled <c>item_granted_action</c> rows on that container.</param>
    public static IReadOnlyList<ActionGrantRow> GrantsFor(
        EquipAssignment assignment,
        Func<EquipAssignment, string?> containerIdOf,
        Func<string, IReadOnlyList<ItemGrantedActionRow>> grantsOf)
    {
        if (assignment is null) throw new ArgumentNullException(nameof(assignment));
        if (containerIdOf is null) throw new ArgumentNullException(nameof(containerIdOf));
        if (grantsOf is null) throw new ArgumentNullException(nameof(grantsOf));

        var containerId = containerIdOf(assignment);
        if (string.IsNullOrWhiteSpace(containerId)) return Array.Empty<ActionGrantRow>();

        var rows = grantsOf(containerId!) ?? Array.Empty<ItemGrantedActionRow>();
        var result = new List<ActionGrantRow>(rows.Count);

        foreach (var row in rows)
        {
            if (!row.Enabled) continue; // content is disabled, never deleted (definitions §6)
            result.Add(GrantFor(assignment, containerId!, row));
        }

        // Ordinal, content-derived, never a generated id (§3.7 / definitions §5).
        result.Sort((a, b) => string.CompareOrdinal(a.ActionId, b.ActionId));
        return result;
    }

    /// <summary>One row. The whole seam is these five fields, and four of them are already decided by
    /// the shipped reader.</summary>
    public static ActionGrantRow GrantFor(EquipAssignment assignment, string containerId, ItemGrantedActionRow row) =>
        new(OwnerKind.Entity, assignment.SpecimenId, row.ActionId,
            Source: containerId, GrantRole: row.RoleWire);

    /// <summary>
    /// The <c>grant_id</c> primary key, <b>derived and stable</b> rather than a fresh
    /// <c>Guid.NewGuid()</c>. A projection is a full rebuild, so a random id would insert a duplicate
    /// row on every re-apply instead of upserting the one that already exists — the same reason
    /// definitions §5 refuses sorting on a generated id, arriving at the write side.
    /// </summary>
    public static string GrantIdFor(string specimenId, string containerId, string actionId) =>
        $"{specimenId}|{containerId}|{actionId}";

    /// <summary>
    /// The whole specimen, as the two lists a caller writes: every grant to upsert, and every
    /// <c>source</c> whose rows must go first. Withdrawing by source before writing is what makes an
    /// item whose grant rows CHANGED (a content edit) converge rather than accumulate.
    /// </summary>
    public static (IReadOnlyList<ActionGrantRow> Grants, IReadOnlyList<string> SourcesToWithdraw) ForSpecimen(
        string specimenId,
        IReadOnlyList<EquipAssignment> assignments,
        Func<EquipAssignment, string?> containerIdOf,
        Func<string, IReadOnlyList<ItemGrantedActionRow>> grantsOf)
    {
        if (assignments is null) throw new ArgumentNullException(nameof(assignments));
        if (containerIdOf is null) throw new ArgumentNullException(nameof(containerIdOf));

        var grants = new List<ActionGrantRow>();
        var sources = new List<string>();

        foreach (var assignment in assignments)
        {
            if (!string.Equals(assignment.SpecimenId, specimenId, StringComparison.Ordinal)) continue;

            var containerId = containerIdOf(assignment);
            if (string.IsNullOrWhiteSpace(containerId)) continue;
            if (!sources.Contains(containerId!, StringComparer.Ordinal)) sources.Add(containerId!);

            grants.AddRange(GrantsFor(assignment, containerIdOf, grantsOf));
        }

        sources.Sort(StringComparer.Ordinal);
        grants.Sort((a, b) =>
        {
            var bySource = string.CompareOrdinal(a.Source, b.Source);
            return bySource != 0 ? bySource : string.CompareOrdinal(a.ActionId, b.ActionId);
        });

        return (grants, sources);
    }
}
