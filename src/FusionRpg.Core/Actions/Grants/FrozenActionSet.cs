namespace FusionRpg.Core.Actions.Grants;

/// <summary>
/// T24 (spec-grant-seam.md §3, item 6): the one snapshot moment. Assembles once at run start and
/// stays frozen for the run — "a grant that arrives mid-run does not change the assembled set. It
/// applies at the next run start" and "a second assembly call in one run returns the identical set"
/// are the SAME guarantee here, not two separate ones to keep in sync: <see cref="Snapshot"/> simply
/// never re-reads its inputs until <see cref="RefreshAtNextRunStart"/> is called explicitly.
///
/// <para>Removal semantics (spec §4) fall out of this for free rather than needing their own
/// mechanism: the underlying `rpg_action_grant` row can be marked withdrawn at any moment (already
/// shipped — `RpgStore.WithdrawGrantsBySource`, T1), but <see cref="ActionSetAssembler.Assemble"/>
/// only ever sees whatever `liveGrants` list a caller hands it — a withdrawn row simply is not in
/// that list on the NEXT call. Nothing here needs to know about inventory, phases, or the timeline
/// kernel at all, which is exactly what keeps "no inventory type reaches `InterruptCause`" true by
/// construction rather than by policing every call site.</para>
/// </summary>
public sealed class FrozenActionSet
{
    public AssemblyResult Snapshot { get; private set; }

    FrozenActionSet(AssemblyResult snapshot) => Snapshot = snapshot;

    /// <summary>The run-start assembly. Whatever is live right now becomes the frozen set for the
    /// whole run.</summary>
    public static FrozenActionSet FreezeAtRunStart(
        SpeciesBasicsRow basics, IReadOnlyList<ActionGrantRow> liveGrants, Func<string, bool> isDefaultAttackEligible) =>
        new(ActionSetAssembler.Assemble(basics, liveGrants, isDefaultAttackEligible));

    /// <summary>
    /// Explicitly does NOT re-assemble. A grant arriving, or a grant withdrawn, mid-run changes what
    /// <c>liveGrants</c> a caller could pass here — this method deliberately never looks, and always
    /// hands back the tick-zero snapshot. Naming it separately from a plain getter makes "this is a
    /// refusal, not an oversight" visible at every call site.
    /// </summary>
    public AssemblyResult Snapshotted() => Snapshot;

    /// <summary>Only call this at the NEXT run's start. Re-assembles for real and replaces the
    /// frozen snapshot.</summary>
    public AssemblyResult RefreshAtNextRunStart(
        SpeciesBasicsRow basics, IReadOnlyList<ActionGrantRow> liveGrants, Func<string, bool> isDefaultAttackEligible)
    {
        Snapshot = ActionSetAssembler.Assemble(basics, liveGrants, isDefaultAttackEligible);
        return Snapshot;
    }
}
