using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Drops;

namespace FusionRpg.Core.Items;

/// <summary>Everything <see cref="EquipmentContainerBuild.From"/> needs beyond the grant itself — the
/// derived role/family legality table (<see cref="RoleFamilyTable.Derive"/>'s own output, item module
/// 8) and the real atom catalog this module holds no copy of, the identical "read model owned
/// elsewhere" idiom <see cref="FusionRpg.Core.Items.Uniques.UniqueContainerLookups"/> already uses for
/// the sibling unique-container build.</summary>
public sealed record EquipmentContainerLookups(
    IReadOnlyList<RoleFamilyCell> RoleFamilyCells,
    Func<string, IReadOnlyList<AtomRow>> AtomsInFamily);

/// <summary>
/// `mintAt`'s Equipment-kind arm (party-dungeon-todo.md D4.12/D3.11, 2026-09-07): assembles an
/// EPHEMERAL <see cref="ContainerRow"/> for one drawn <see cref="LootGrant"/> — never persisted as its
/// own row, unlike a unique's pre-authored container (<see cref="Uniques.UniqueContainerBuild"/> builds
/// once at import time; this builds fresh on every draw). Mirrors that sibling's own shape — a pure
/// function taking a lookups record — for the SAME "role/frame narrows which atoms are legal"
/// question `spec-affix-legality.md` (item module 8) already answers via
/// <see cref="RoleFamilyTable.Derive"/> + the shipped <c>family-overrides.v1.json</c> /
/// <c>role-relocation.v1.json</c> — real, pure, and already tested against the real 100+-family shipped
/// corpus (<c>RoleFamilyTableTests.cs</c>), but with zero production callers before this one (confirmed
/// by a dedicated search).
///
/// <para><b>What this function deliberately does NOT do:</b> it does not judge side or runtime
/// legality (<see cref="AffixFilters.SideAllows"/> / <see cref="AffixFilters.RuntimeAllows"/>) — both
/// are, by that module's own doc comment, "checked at bind/import time," and
/// <see cref="Instantiator.TryInstantiate"/> already runs <see cref="ContainerValidator.Validate"/>
/// against whatever this returns — the same safety net every other dynamically-built container in this
/// codebase already relies on, never a second, private copy of that check. It does not weight
/// candidates unevenly: <c>role-affix-weights.v1.json</c>, module 8's own generator-input matrix, is
/// explicitly "generator input, not a table… nothing at runtime reads it," so a flat weight of 1 per
/// candidate is the honest default here — the identical reasoning
/// <see cref="Uniques.UniqueContainerBuild.From"/> already gives its own variance pool ("nothing in the
/// spec or the shipped tuning names a per-candidate weighting rule").</para>
/// </summary>
public static class EquipmentContainerBuild
{
    /// <summary>The built container, plus the pool's own backing affix map — the pool references ids
    /// <see cref="AffixLibraryGenerator"/> just synthesized on the fly, which no store table carries
    /// (the identical resolution a unique's own variance pool needs at instantiate time). A caller's
    /// `lookupAffix` should check this map first, falling back to a store lookup for anything else.</summary>
    public sealed record BuildResult(ContainerRow Container, IReadOnlyDictionary<string, AffixRow> Affixes);

    public static BuildResult From(LootGrant grant, EquipmentContainerLookups lookups)
    {
        if (grant is null) throw new ArgumentNullException(nameof(grant));
        if (lookups is null) throw new ArgumentNullException(nameof(lookups));
        if (lookups.RoleFamilyCells is null) throw new ArgumentNullException(nameof(lookups.RoleFamilyCells));
        if (lookups.AtomsInFamily is null) throw new ArgumentNullException(nameof(lookups.AtomsInFamily));
        if (grant.Kind != DropEntryKind.Equipment)
            throw new ArgumentException($"grant {grant.Index} is '{grant.Kind}', not Equipment", nameof(grant));
        if (grant.BaseTypeId is not { Length: > 0 } baseTypeId)
            throw new ArgumentException($"grant {grant.Index} carries no BaseTypeId — MintEquipment must populate it before calling Mint", nameof(grant));
        if (grant.Frame is not { Length: > 0 } frame)
            throw new ArgumentException($"grant {grant.Index} carries no Frame — MintEquipment must populate it before calling Mint", nameof(grant));
        if (grant.Role is not { Length: > 0 } role)
            throw new ArgumentException($"grant {grant.Index} carries no Role — MintEquipment must populate it before calling Mint", nameof(grant));

        var floor = grant.MinTier > 0 ? grant.MinTier : 1;
        var grantCeiling = grant.MaxTier > 0 ? grant.MaxTier : floor;

        var pool = new List<ContainerPoolRow>();
        var affixes = new Dictionary<string, AffixRow>(StringComparer.Ordinal);

        foreach (var cell in lookups.RoleFamilyCells)
        {
            if (!string.Equals(cell.RoleId, role, StringComparison.Ordinal)) continue;
            if (!string.Equals(cell.ItemFrame, frame, StringComparison.Ordinal)) continue;

            var ceiling = Math.Min(grantCeiling, cell.MaxTier);
            if (floor > ceiling) continue;

            foreach (var atom in lookups.AtomsInFamily(cell.FamilyId))
            {
                if (atom.Tier < floor || atom.Tier > ceiling) continue;

                var affix = AffixLibraryGenerator.SingleAtomAffix(atom);
                if (affixes.TryAdd(affix.AffixId, affix))
                    pool.Add(new ContainerPoolRow(affix.AffixId, Weight: 1));
            }
        }

        var container = new ContainerRow
        {
            ContainerId = EphemeralContainerId(grant, baseTypeId),
            Kind = ContainerKind.Item,
            Frame = frame,
            BaseTypeId = baseTypeId,
            MinTier = floor,
            MaxTier = grantCeiling,
            PrefixRolls = grant.PrefixRolls,
            SuffixRolls = grant.SuffixRolls,
            Pool = pool,
        };
        return new BuildResult(container, affixes);
    }

    /// <summary>Never persisted as its own <c>effect_container</c> row (see class doc) — only readable
    /// enough to be traceable in a support question. <c>effect_instance.container_id</c> carries no
    /// foreign key to <c>effect_container</c> (confirmed by reading the schema directly), so an id with
    /// no matching row is a safe, already-anticipated state, not a dangling reference.</summary>
    static string EphemeralContainerId(LootGrant grant, string baseTypeId)
    {
        var slug = baseTypeId.StartsWith("item.", StringComparison.Ordinal) ? baseTypeId["item.".Length..] : baseTypeId;
        return $"item.drop-{slug}-{grant.Index}";
    }
}
