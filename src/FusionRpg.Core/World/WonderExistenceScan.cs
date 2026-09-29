namespace FusionRpg.Core.World;

/// <summary>loam-relics-and-wonders `wonder-build-flow` §Design 5: "how many Unique-rarity Wonders of
/// this WonderScope already exist OR ARE UNDER CONSTRUCTION for this scope-unit" — the half
/// WonderPolicy.ExistenceCapFor's own doc comment names as this module's job
/// (spec-wonder-structure.md §Design 6). Deliberately DOES NOT mirror LoamPhases.EffectiveCapacity's
/// "not under construction" gate — that gate is correct for a capacity/yield axis, wrong for an
/// existence cap (a slot is claimed the moment a build order is accepted, not when it completes;
/// skipping under-construction rows here let two Unique-rarity Wonders of the same scope be built
/// simultaneously, each individually under the cap, both completing over it — found and fixed via
/// strengthen pass, 2026-09-13). Only requirement: a known catalog id on the slot.</summary>
public static class WonderExistenceScan
{
    public static long CountExisting(WorldState world, WorldSector targetSector, WonderScope scope, WonderRarity rarity)
    {
        if (rarity != WonderRarity.Common && rarity != WonderRarity.Unique)
            throw new ArgumentOutOfRangeException(nameof(rarity));
        if (rarity == WonderRarity.Common) return 0;   // Common has no cap -- ExistenceCapFor already
                                                       // returns long.MaxValue; scanning would be wasted work.

        var scopeUnitSectors = scope switch
        {
            WonderScope.Sector => new[] { targetSector },
            WonderScope.Empire => world.Sectors.Where(s =>
                string.Equals(s.OwnerFactionId, targetSector.OwnerFactionId, StringComparison.Ordinal)).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(scope),
                $"WonderScope.{scope} is reserved and refused at StructureCatalog.Validate -- " +
                "this scan should never be called with it."),
        };

        long count = 0;
        foreach (var sector in scopeUnitSectors)
            foreach (var slot in sector.Slots)
            {
                if (slot.StructureId is not { } id) continue;
                if (!StructureCatalog.IsKnown(id)) continue;

                // Deliberately does NOT skip a structure still under construction
                // (slot.ConstructionTurnsRemaining > 0) -- unlike LoamPhases.EffectiveCapacity's
                // identical-looking gate, which is correct for a CAPACITY/YIELD axis (an unfinished
                // structure legitimately contributes zero capacity), an EXISTENCE cap must claim the
                // slot the moment a build order is accepted, not when it completes. Skipping
                // under-construction Wonders here would let two Unique-rarity Wonders of the same
                // scope be under construction simultaneously (each build order individually sees the
                // cap not yet reached) and both complete, exceeding WonderPolicy.ExistenceCapFor --
                // a real, found-and-fixed defect (strengthen pass, 2026-09-13), not a copy-paste
                // simplification of the capacity gate this scan otherwise mirrors.
                var def = StructureCatalog.Get(id);
                if (def.WonderScope == scope && def.WonderRarity == WonderRarity.Unique) count++;
            }
        return checked(count);
    }
}
