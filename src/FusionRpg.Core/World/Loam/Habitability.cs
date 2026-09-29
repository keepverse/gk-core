namespace FusionRpg.Core.World.Loam;

/// <summary>
/// habitable(sector) ⇔ it holds at least one loam source (spec-loam-calc.md #6) — the owner's
/// settlement rule (ideal §8.10). The wording grows at `loam-structures` (wave 4): *"any slot is a
/// Rootbed, or holds an active `LoamSource` structure."* "Active" excludes a structure still under
/// construction — a barren Seat with a waystation mid-build is not yet real ground, which is the
/// whole tension G1 names.
/// </summary>
public static class Habitability
{
    /// <summary>The truth side.</summary>
    public static bool For(WorldSector sector) =>
        For(sector.Slots.Select(sl => (sl.SlotTypeId, sl.StructureId, sl.ConstructionTurnsRemaining)));

    /// <summary>
    /// The belief side. A structure and its construction state are visible on the same terms as the
    /// slot itself (terrain-like, visible once scouted, the same precedent `FractureIntensityMilli`
    /// already set) — not owner-only, so this never needs ownership or fog beyond what the caller
    /// already scoped by only passing slots it actually believes.
    /// </summary>
    public static bool For(IEnumerable<(string SlotTypeId, string? StructureId, int? ConstructionTurnsRemaining)> slots) =>
        slots.Any(s => IsSource(s.SlotTypeId, s.StructureId, s.ConstructionTurnsRemaining));

    static bool IsSource(string slotTypeId, string? structureId, int? constructionTurnsRemaining)
    {
        if (SlotTypeCatalog.Get(slotTypeId).Kind == SlotKind.Rootbed) return true;
        if (structureId is not { } id || constructionTurnsRemaining is > 0) return false;
        return StructureCatalog.IsKnown(id) && StructureCatalog.Get(id).Kind == StructureKind.LoamSource;
    }
}
