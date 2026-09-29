using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Sockets;

/// <summary>
/// The combination's container, built from content already shipped (spec-combo-bind §1). A
/// combination entry carries <c>grants</c> — atom FAMILY ids, a closed enum the generator writes —
/// and the evaluator produces a <c>GrantedTier</c>, so a combination container is a pure function of
/// <c>(comboId, grants, tier)</c>. That is the same build-at-use-time shape
/// <c>GemContainerBuild.TryBuildOne</c> already proves for a gem: family × tier →
/// <see cref="AtomRow.DeriveId"/>, one FIXED atom per grant, no variance pool, no rarity, no roll.
///
/// <para>⛔ <b>Content, never a mutation.</b> A combination container is an ordinary
/// <see cref="ContainerKind.Combo"/> row: grants are stored as container atoms, so this needs no new
/// table and no new column. The instance is <c>TryInstantiate</c> on this row at bind time.</para>
///
/// <para>⚠ <b>Refused by name, never guessed.</b> A grant family with no real atom at the requested
/// tier is a content gap; substituting another atom would ship a word that visibly fires and silently
/// does nothing, which is the defect the one-acceptance-set rule exists to close.</para>
/// </summary>
public static class ComboContainerBuild
{
    /// <summary>The real atom catalog, injected — Core reads no file and holds no copy of it, matching
    /// <c>GemContainerBuild.GemContainerLookups</c>'s identical idiom.</summary>
    public sealed record ComboContainerLookups(Func<string, AtomRow?> LookupAtom);

    /// <summary>
    /// The container id for one recipe at one tier: <c>{comboId}-t{tier}</c> — one segment after
    /// <c>combo.</c>, and legal under <c>ContainerValidator</c>'s own id pattern.
    /// </summary>
    public static string ContainerId(string comboId, int tier)
    {
        if (string.IsNullOrWhiteSpace(comboId))
            throw new ArgumentException("comboId must be non-empty", nameof(comboId));
        if (tier < 1)
            throw new ArgumentOutOfRangeException(nameof(tier), tier, "a granted tier starts at 1");
        return $"{comboId}-t{tier}";
    }

    /// <summary>
    /// Build one combination's container, or refuse by name. A grant whose derived atom is absent from
    /// the real catalog is reported with the family AND the derived id, so the fix (author the atom, or
    /// drop the grant) is unambiguous.
    /// </summary>
    public static ContainerRow? TryBuild(
        string comboId, IReadOnlyList<string> grants, int tier,
        ComboContainerLookups lookups, out string? refusalReason)
    {
        if (grants is null) throw new ArgumentNullException(nameof(grants));
        if (lookups is null) throw new ArgumentNullException(nameof(lookups));

        var containerId = ContainerId(comboId, tier);
        if (grants.Count == 0)
        {
            refusalReason = $"'{containerId}' grants no family, so its container would carry no atom";
            return null;
        }

        var atoms = new List<ContainerAtomRow>(grants.Count);
        var seq = 0;
        foreach (var family in grants)
        {
            var atomId = AtomRow.DeriveId(family, "", tier);
            if (lookups.LookupAtom(atomId) is null)
            {
                refusalReason =
                    $"'{containerId}' grants family '{family}', which resolves to atom '{atomId}' " +
                    $"(variant '', tier {tier}); that atom is not in the real generated atom catalog";
                return null;
            }
            atoms.Add(new ContainerAtomRow(seq++, atomId));
        }

        refusalReason = null;
        return new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Combo,
            Atoms = atoms,
            PrefixRolls = 0,
            SuffixRolls = 0,
            Pool = Array.Empty<ContainerPoolRow>(),
        };
    }
}
