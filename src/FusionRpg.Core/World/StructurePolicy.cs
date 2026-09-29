using FusionRpg.Core.Battle.Board;

namespace FusionRpg.Core.World;

/// <summary>
/// base-defense `structure-state` (spec-structure-state.md) — every structure balance rule in one
/// place, reading `gk-core/data/tuning/siege.v1.json`'s `structure`/`storage` blocks via
/// <see cref="SiegeTuningPolicy"/> (the same tuning file `siege-board`/`district-layout` already
/// share — one program, one config surface). A Policy file, so it carries named tunables and no bare
/// literals (tunables-ssot.md).
/// </summary>
public static class StructurePolicy
{
    /// <summary>Decision 32's tier ladder. Throws for an unauthored tier — a bad structure row is a
    /// startup error, never a runtime surprise (<see cref="StructureCatalog.Validate"/> calls this for
    /// every tier &gt; 0 at load, so a live call here can only ever be hitting an already-validated
    /// value).</summary>
    public static int TierMultiplierMilli(int tier)
    {
        if (SiegeTuningPolicy.Structure.TierMultiplierMilli.TryGetValue(tier, out var milli)) return milli;
        throw new InvalidOperationException(
            $"structure tuning: material tier {tier} has no tierMultiplierMilli row — decision 32's ladder is useless without one.");
    }

    /// <summary>
    /// What it costs to repair a structure to full — proportional to what is missing. Takes the
    /// already-resolved <paramref name="maxHp"/> (from <see cref="StructureDef.MaxHpOf"/>) rather than
    /// a <see cref="StructureDef"/> plus a development level, since <c>MaxHp</c> is itself a function
    /// of the SECTOR's `DevelopmentLevel`, not the structure alone — resolving it once at the call site
    /// and passing the number in keeps this method a pure function of the three numbers it actually
    /// needs, with no ambient world lookup hidden inside it.
    ///
    /// <para><b>Divide by 1000 exactly once, last</b> (docs/architecture/numeric-types.md rule 4): the per-mille intermediate is
    /// 1000× closer to the ceiling than the answer is. <b>Widen before multiplying</b> (rule 3): the
    /// cast binds to the RESULT, so an early narrowing would have already overflowed.</para>
    /// </summary>
    public static long RepairCost(long cost, long maxHp, long currentHp)
    {
        if (maxHp <= 0) return 0; // indestructible: nothing to repair
        var missing = maxHp - Math.Max(0, currentHp);
        if (missing <= 0) return 0;

        // long * long * long, ONE divide by maxHp (turns "missing" into a fraction of the building),
        // then ONE divide by 1000 (turns the per-mille ratio into whole loam) — never combined into a
        // single `/ (maxHp * 1000)`, which could itself overflow before either fraction is taken.
        return checked(cost * missing * SiegeTuningPolicy.Structure.RepairCostRatioMilli / maxHp / 1000);
    }

    /// <summary>F12: the effective per-sector storage cap grows with `DevelopmentLevel`, alongside
    /// decision 21's slot growth — otherwise a new rootbed slot's whole output is wasted overflow.
    /// Additive to whatever base/granary capacity the caller already computed (`LoamPhases.EffectiveCapacity`),
    /// never a replacement.</summary>
    public static long CapacityGrowthFor(int developmentLevel) =>
        checked((long)Math.Max(0, developmentLevel) * SiegeTuningPolicy.Structure.StorageCapacityPerDevelopmentLevel);

    /// <summary>Decision 22: at capacity, production STOPS rather than overflowing — reversible the
    /// moment more storage is built, unlike depletion below.</summary>
    public static bool IsHaltedByCapacity(long stock, long effectiveCapacity) => stock >= effectiveCapacity;

    /// <summary>Per-harvest depletion added to a slot's own `SlotDepletionMilli` (audit F10) — per-mille,
    /// bounded 0..1000, never touching `WorldSector.DepletionMilli` (the loam program's own field).</summary>
    public static int DepletionPerHarvestMilli => SiegeTuningPolicy.Structure.DepletionPerHarvestMilli;

    /// <summary>Whether a slot's deposit is fully spent. Irreversible, unlike a capacity halt — no
    /// amount of storage brings a depleted deposit back.</summary>
    public static bool IsExhausted(int slotDepletionMilli) => slotDepletionMilli >= 1000;
}
