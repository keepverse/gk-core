namespace FusionRpg.Core.World.Loam;

/// <summary>
/// production(sector) = Σ over slots that are loam sources of seepPerTurn(slotType)
/// (spec-loam-calc.md #1). Wave 1's only source is the rootbed slot itself — untended ground seeps,
/// unconditionally: there is no chain gate here (the owner's S3 resolution). Connectivity does not
/// decide whether loam appears, only who can spend it (<see cref="TerritoryComponents"/>).
/// </summary>
public static class LoamProduction
{
    /// <summary>
    /// The truth side. Extends additively over the belief-side sum: each Rootbed slot's own base
    /// yield, multiplied by its own active structure's <c>YieldMultiplierMilli</c> if one is present
    /// (spec-loam-structures.md — a Well multiplies, it does not replace) — 1000 (unchanged) for a
    /// slot with no structure or one still under construction, so a rootbed with no well behaves
    /// exactly as it does today.
    ///
    /// <para>loam-relics-and-wonders `wonder-effect-empire` §Design 2: <paramref name="scopeModifierMilli"/>
    /// is the owning faction's already-resolved <c>WorldFaction.ScopeModifierMilli</c> (1000 = no
    /// modifier), applied once at the end over the sector's whole output — never per-loop. The
    /// default keeps the multiply an identity for isolated callers with no faction context.</para>
    /// </summary>
    public static long For(WorldSector sector, int scopeModifierMilli = 1000)
    {
        if (sector.OwnerFactionId is null) return 0; // G-B

        long total = 0;
        foreach (var slot in sector.Slots)
        {
            if (SlotTypeCatalog.Get(slot.SlotTypeId).Kind != SlotKind.Rootbed) continue;

            var multiplierMilli = 1000;
            if (slot.StructureId is { } structureId
                && slot.ConstructionTurnsRemaining is null or <= 0
                && StructureCatalog.IsKnown(structureId))
            {
                multiplierMilli = StructureCatalog.Get(structureId).YieldMultiplierMilli;
            }

            total += LoamPolicy.SeepPerTurn * multiplierMilli / 1000;
        }

        // world-map W56 (spec-sector-development.md §3): a flat, structure-only yield add — unlike
        // the Rootbed loop above, this reads *every* slot's own active structure regardless of slot
        // kind (a soul conduit sits on an EssenceDeposit, an extractor on a ShardVein, neither a
        // Rootbed), additive to the sum, defaulting to 0 so every structure minted before this task
        // is untouched.
        foreach (var slot in sector.Slots)
        {
            if (slot.StructureId is not { } flatStructureId
                || slot.ConstructionTurnsRemaining is > 0
                || !StructureCatalog.IsKnown(flatStructureId))
                continue;

            total += StructureCatalog.Get(flatStructureId).FlatYieldPerTurn;
        }

        // world-map W55 (empire-economy-ssot.md A8): the yield half of "development must pay" —
        // additive to the Rootbed sum above, the same way a well's own multiplier is additive to it,
        // never a replacement. Ships real (not identity) the moment a sector's DevelopmentLevel is
        // nonzero, but every shipped template and every existing golden starts every sector at
        // DevelopmentLevel 0 and no scripted turn sequence in an existing test ever orders `develop`,
        // so this moves no golden — proven by running them, not merely argued.
        total += Growth.DevelopmentYield.For(sector.DevelopmentLevel, LoamPolicy.DevelopmentYieldPerLevel);

        // wonder-effect-empire §Design 2: the faction-wide modifier over the whole sector output.
        // `total` is already `long` (widen-before-multiply needs no cast); divide-by-1000 happens
        // exactly once, last; `checked` so overflow throws, never wraps.
        return checked(total * scopeModifierMilli) / 1000;
    }

    /// <summary>
    /// The belief side: all a caller needs is whether the sector is owned (never fogged — you
    /// always know what you hold) and which slot types it carries (terrain, remembered once
    /// scouted). Ordering-invariant: production is a sum, and a sum does not care what order it is
    /// added in.
    /// </summary>
    public static long For(string? ownerFactionId, IEnumerable<string> slotTypeIds)
    {
        // G-B: unowned sectors have no economy. A rootbed sitting in neutral ground does not
        // quietly fill up while nobody holds it — otherwise the optimal play is to wait and take
        // the windfall, which rewards doing nothing.
        if (ownerFactionId is null) return 0;

        long total = 0;
        foreach (var slotTypeId in slotTypeIds)
            if (SlotTypeCatalog.Get(slotTypeId).Kind == SlotKind.Rootbed)
                total += LoamPolicy.SeepPerTurn;

        return total;
    }
}
