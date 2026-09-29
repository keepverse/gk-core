namespace FusionRpg.Core.Battle.Siege;

public sealed class SiegeSlotsRejection : Exception
{
    public SiegeSlotsRejection(string message) : base(message) { }
}

/// <summary>
/// base-defense `siege-objective` §3-4: the two orthogonal budgets the central defense area and the
/// board itself each hold — legion slots (who may stand in the Core) and defense slots (how many
/// structures may stand on the board). §5.1: "raising development buys more FORTIFICATION, not more
/// ARMY. Army comes from the empire-wide legion budget, which is scarce for entirely different
/// reasons." Deliberately NOT a single combined config — the two never move together.
/// </summary>
public static class SiegeSlots
{
    /// <summary>
    /// How many legions the central defense area holds PER SIDE. Even by decision 4 (2 v 2, 4 v 4) —
    /// validated LOUDLY at load, matching `StructureCatalog.Validate`'s stance that a bad row is a
    /// startup error, never a runtime surprise: an odd slot count silently breaks the pairing rule the
    /// whole fight's legibility rests on.
    ///
    /// <para><b>"Even" means the CAPACITY is even, not that both sides must fill it</b> (§5.8) — a
    /// caller with fewer legions than this may still assault; requiring a full roster would gate a verb
    /// behind an inventory count, "the shape of rule that produces 'I cannot attack and I do not know
    /// why.'"</para>
    /// </summary>
    public static int LegionSlotsPerSide(int perSide)
    {
        if (perSide <= 0)
            throw new SiegeSlotsRejection($"siege slots: legion.perSide must be > 0; got {perSide}");
        if (perSide % 2 != 0)
            throw new SiegeSlotsRejection($"siege slots: legion.perSide must be even (decision 4); got {perSide}");
        return perSide;
    }

    /// <summary>
    /// §5.1's SECOND budget — how many structures may stand on the board at once, the thing
    /// `DevelopmentLevel` actually buys now that the grid itself is fixed per base tier
    /// (`district-layout` §2). A board cap, exempt from the no-hard-ceilings rule and saying so —
    /// ssot-power-scale.md §11.3: "bounds how much can exist at one moment, not how far you can get."
    /// `MaxLivingPlants = 50` is the named precedent for this exact exemption shape.
    ///
    /// <para><b>The escape valve, §5.1's third stage — this is what makes a fixed board legal under the
    /// no-hard-ceilings rule.</b> Below <paramref name="gridCapacityPoint"/>, each level buys more
    /// defense SLOTS (flat, authored, never `P(Θ)`). At and above it, slot count stops growing —
    /// further development instead buys tower TIER, a magnitude, through `StructureDef.MaxHpOf`'s own
    /// `P(Θ_development)`, which rises forever. The board stops growing; the investment never does.
    /// This method owns stages 1 and 2 (the slot count); `structure-state` owns the magnitude stage 3
    /// rises on.</para>
    /// </summary>
    public static int DefenseSlotsFor(int developmentLevel, int atDevelopmentZero, int perDevelopmentLevel, int gridCapacityPoint)
    {
        var clampedLevel = Math.Min(Math.Max(0, developmentLevel), Math.Max(0, gridCapacityPoint));
        return checked(atDevelopmentZero + perDevelopmentLevel * clampedLevel);
    }
}
