namespace FusionRpg.Core.World.Loam;

/// <summary>
/// loam-relics-and-wonders `wonder-effect-empire` §Design 3: the per-faction SUM of every built
/// `Empire`-scope Wonder's `LoamGenerationRate` `ValueMilli`, recomputed fresh every Production
/// phase from the already-loaded <see cref="WorldState"/> — never a live DB read mid-step
/// (`LoamPhases` purity). SUM is a structural constant (decisions.md, Loam relics and wonders
/// SSOT), not a tunable. Persistence of the computed value is Task 2.3b's job; this file only
/// computes in memory.
/// </summary>
public static class WonderEmpireEffects
{
    /// <summary>
    /// The per-sector half of the scan: the sum of every active Empire-scope
    /// `LoamGenerationRate` effect hosted on this one sector. Factored out (rather than inlined
    /// in <see cref="ComputeScopeModifierMilli"/>) because §Design 6's upkeep term needs exactly
    /// this number — one scan shape, two callers.
    /// </summary>
    public static long EmpireLoamGenerationValueMilliFor(WorldSector sector)
    {
        long sum = 0;
        foreach (var slot in sector.Slots)
        {
            if (slot.StructureId is not { } id) continue;
            if (slot.ConstructionTurnsRemaining is > 0) continue;
            if (!StructureCatalog.IsKnown(id)) continue;

            var structure = StructureCatalog.Get(id);
            if (structure.WonderScope != WonderScope.Empire) continue;

            foreach (var effect in structure.WonderEffects)
                if (effect.Kind == WonderEffectKind.LoamGenerationRate)
                    sum = checked(sum + effect.ValueMilli);
        }

        return sum;
    }

    /// <summary>
    /// `1000 + Σ(ValueMilli)` over every sector owned by <paramref name="factionId"/>.
    /// `effect.Scope` is trusted to equal the row's `WonderScope` (`StructureCatalog.Validate`
    /// enforces it) and is not re-checked here. A faction with no built Empire-scope Wonder gets
    /// exactly `1000` — the field's existing default, so Wonder-free worlds are byte-identical.
    /// The narrowing cast is `checked`: <see cref="WorldFaction.ScopeModifierMilli"/> is an
    /// inherited pre-existing `int`, so an out-of-range sum throws rather than wrapping.
    /// </summary>
    public static int ComputeScopeModifierMilli(IReadOnlyList<WorldSector> sectors, string factionId)
    {
        long sum = 0;
        foreach (var sector in sectors)
        {
            if (!string.Equals(sector.OwnerFactionId, factionId, StringComparison.Ordinal)) continue;
            sum = checked(sum + EmpireLoamGenerationValueMilliFor(sector));
        }

        return checked((int)(1000 + sum));
    }
}
