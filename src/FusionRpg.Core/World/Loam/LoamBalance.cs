namespace FusionRpg.Core.World.Loam;

/// <summary>
/// production − upkeep, per sector, summed per component, summed per faction (spec-loam-calc.md
/// #4). Trivial arithmetic and deliberately its own named thing: ideal §12.4's central claim is
/// that *most sectors lose money*, and a design whose most important property has no function to
/// ask it about will never have that property tested.
/// </summary>
public static class LoamBalance
{
    /// <summary>The truth side, one sector. Reads the owning faction's stored
    /// <c>ScopeModifierMilli</c> (wonder-effect-empire §Design 4) — never rescans.</summary>
    public static long PerSector(WorldState world, WorldSector sector)
    {
        var scopeModifierMilli = world.Factions
            .FirstOrDefault(f => f.FactionId == sector.OwnerFactionId)?.ScopeModifierMilli ?? 1000;
        return PerSector(LoamProduction.For(sector, scopeModifierMilli), LoamUpkeep.For(world, sector));
    }

    /// <summary>The belief side, one sector: production and upkeep already computed elsewhere.</summary>
    public static long PerSector(long production, long upkeep) => production - upkeep;

    /// <summary>The truth side, one component — the total a connected block can actually draw on.</summary>
    public static long PerComponent(WorldState world, IReadOnlyList<string> componentSectorIds)
    {
        var byId = world.Sectors.ToDictionary(s => s.SectorId, StringComparer.Ordinal);

        long total = 0;
        foreach (var sectorId in componentSectorIds)
            total += PerSector(world, byId[sectorId]);

        return total;
    }

    /// <summary>The truth side, one faction — every component's balance, added.</summary>
    public static long PerFaction(WorldState world, string factionId)
    {
        long total = 0;
        foreach (var component in TerritoryComponents.For(world, factionId))
            total += PerComponent(world, component);

        return total;
    }
}
