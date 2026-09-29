namespace FusionRpg.Core.World.Loam;

/// <summary>
/// One place both the engine and the player-facing projection call for "which sector takes the
/// fade" and "did that actually zero it out" (spec-loam-fe.md's abandonment surface) — so
/// <see cref="LoamPhases.Pressure"/>, which runs the rule, and the server's forecast, which warns
/// about it a turn early, share the same selection instead of risking two copies drifting apart.
/// </summary>
public static class LoamForecast
{
    /// <summary>
    /// The weakest contributor in a component whose available stock cannot cover its upkeep — worst
    /// per-sector balance, ordinal tiebreak. Every member is a candidate. A sector with a
    /// <c>WardenBindingId</c> is no longer excluded: the owner withdrew the warden freeze on
    /// 2026-09-13 (warden-mortality-ideal.md), and warden-freeze-fix (RulesetVersion 13) removed the
    /// exclusion here and the matching recovery skip in <see cref="LoamPhases.Pressure"/> together,
    /// so this forecast and the phase still make the same pick. Null only when the pool covers its
    /// own upkeep in full.
    ///
    /// <paramref name="ceded"/> is the faction's own deliberate choice (world-stage W24/W25's `cede`
    /// order) — an *input* to this one selection, never a second code path: it wins only when it is
    /// already a legal candidate (a member of this component). Ceding someone else's ground, or a
    /// sector in another component, is simply not a candidate here, so the default worst-balance
    /// ordering answers exactly as if no preference had been filed at all.
    /// </summary>
    public static string? Weakest(
        WorldState world, IReadOnlyList<string> component, long available, long upkeep, string? ceded = null)
    {
        if (available >= upkeep) return null;

        if (ceded is not null && component.Contains(ceded, StringComparer.Ordinal))
            return ceded;

        return component
            .OrderBy(id => LoamBalance.PerSector(world, world.Sectors.First(s => s.SectorId == id)))
            .ThenBy(id => id, StringComparer.Ordinal)
            .First();
    }

    /// <summary>
    /// One turn ahead of <see cref="LoamPhases.Pressure"/>: the stock this component will hold once
    /// this turn's capped accrual lands — the same throttle <see cref="LoamPhases.Production"/>
    /// applies, so this never over-counts a sector already sitting at capacity.
    /// </summary>
    public static long ProjectedStock(IReadOnlyList<string> component, WorldState world)
    {
        long total = 0;
        foreach (var id in component)
        {
            var sector = world.Sectors.First(s => string.Equals(s.SectorId, id, StringComparison.Ordinal));
            // wonder-effect-empire §Design 4: read the stored modifier Production already computed,
            // never rescan — the same one-turn staleness every other projection here already carries.
            var scopeModifierMilli = world.Factions
                .FirstOrDefault(f => f.FactionId == sector.OwnerFactionId)?.ScopeModifierMilli ?? 1000;
            var yield = LoamProduction.For(sector, scopeModifierMilli);
            var room = Math.Max(0, LoamPhases.EffectiveCapacity(sector) - sector.LoamStock);
            total += sector.LoamStock + Math.Min(room, yield);
        }

        return total;
    }

    /// <summary>
    /// The sector the engine will actually release (stability to zero, ground lost) next turn if
    /// nothing changes between now and then — not merely fade further, release outright. This is the
    /// marker spec-loam-fe.md's abandonment surface asks for: visible before it happens, not after.
    ///
    /// <paramref name="ceded"/> forwards straight to <see cref="Weakest"/> so a caller who already
    /// knows the faction's pending `cede` order (world-stage W26's `/state` route) predicts the same
    /// sector the engine will actually release, instead of the two silently disagreeing.
    /// </summary>
    public static string? WillRelease(WorldState world, IReadOnlyList<string> component, string? ceded = null)
    {
        var upkeep = component.Sum(id => LoamUpkeep.For(world, world.Sectors.First(s => s.SectorId == id)));
        var projected = ProjectedStock(component, world);
        var weakest = Weakest(world, component, projected, upkeep, ceded);
        if (weakest is null) return null;

        var shortfall = upkeep - projected;
        var weakestSector = world.Sectors.First(s => s.SectorId == weakest);
        return FadePolicy.Apply(weakestSector.StabilityMilli, -shortfall) == 0 ? weakest : null;
    }
}
