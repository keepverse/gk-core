namespace FusionRpg.Core.World;

/// <summary>scoped-inventory `sector-storage`: mirrors LoamPhases.EffectiveCapacity's exact shape
/// (same active-slot gate, same additive sum) for the ITEM capacity axis instead of loam's. Does
/// NOT include LoamPolicy.LoamCapacity's base term or StructurePolicy.CapacityGrowthFor's
/// development-level growth — those are loam-specific; item storage has no base allowance (zero
/// structures, zero slots, by design) and no named growth term today (real gap, not built here).
///
/// <para>Structural per-sector limit, exempt from the no-hard-ceilings rule (like loam's own cap).</para></summary>
public static class SectorItemCapacity
{
    // Mirrors LoamPhases.EffectiveCapacity's exact shape — same active-slot gate, same additive sum,
    // a different capacity axis and a different catalog field, never the same one.
    public static long EffectiveCapacity(WorldSector sector)
    {
        long bonus = 0;
        foreach (var slot in sector.Slots)
        {
            if (slot.StructureId is not { } id) continue;
            if (slot.ConstructionTurnsRemaining is > 0) continue;
            if (!StructureCatalog.IsKnown(id)) continue;

            var structure = StructureCatalog.Get(id);
            if (structure.Kind == StructureKind.ItemStorage) bonus += structure.ItemStorageCapacityBonus;
        }
        return checked(bonus);
    }
}
