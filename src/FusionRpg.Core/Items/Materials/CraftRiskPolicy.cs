namespace FusionRpg.Core.Items.Materials;

/// <summary>The ladder's stages, in order (spec-craft-risk-ladder.md Tunables: the four stages and
/// their order are STRUCTURAL — a tunable that could reorder them would be a different feature).
/// T10 shipped Stage 1; T24 wires Stage 2's consequence (the decay) and Stage 3 is T11's at-zero
/// filter. The destroy/loss consequence stays repair's (T23), never wear's.</summary>
public enum CraftRiskStage
{
    Assured = 0,
    Exhausted = 1,
}

public static class CraftRiskPolicy
{
    /// <summary>Stage resolution; pure, no I/O. Potential remaining (current &gt; 0) ⇒ Assured;
    /// zero ⇒ Exhausted; <b>null ⇒ Assured</b> — a pre-existing instance whose pair was never derived
    /// is "not yet derived", never mistaken for exhausted (T10/T11's own rule).</summary>
    public static CraftRiskStage StageFor(long? potentialCurrent) =>
        potentialCurrent is not null and <= 0 ? CraftRiskStage.Exhausted : CraftRiskStage.Assured;

    /// <summary>Stage 2 is live as of T24: an exhausted pair means the attempt decays durability.
    /// Pure — the caller owns the read of the stored pair.</summary>
    public static bool CanDecay(long? potentialCurrent) =>
        StageFor(potentialCurrent) == CraftRiskStage.Exhausted;

    /// <summary>Per-mille ceiling-division addend: <c>ceil(x / 1000)</c> is <c>(x + 999) / 1000</c>.
    /// STRUCTURAL, not a balance number — a balance pass changes the wear RATE (a tuning row), never
    /// the division shape (tunables-ssot.md), so this stays a `const` here rather than moving to config.
    /// Any operand pair whose product overflows `long` throws via the caller's `checked`, never wraps.</summary>
    private const long PerMilleCeilAddend = 999;

    /// <summary>
    /// Stage 2's decrement: <c>ceil(durability_max × craftWearPerAttemptMilli / 1000)</c> — the
    /// <b>same unit</b> battle wear uses (`spec-item-durability-repair.md` §3), so the two decay
    /// sources stay comparable (R-G1: one pool, one unit, two formulas). It reads the craft-wear key
    /// only, never <c>wearPerBattleMilli</c>. <c>checked</c>, widen-first, ONE divide, last.
    /// </summary>
    public static long WearFor(long durabilityMax, long craftWearPerAttemptMilli)
    {
        if (durabilityMax < 0)
            throw new ArgumentOutOfRangeException(nameof(durabilityMax), durabilityMax, "a durability max cannot be negative");
        if (craftWearPerAttemptMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(craftWearPerAttemptMilli), craftWearPerAttemptMilli,
                "a per-mille wear rate cannot be negative");
        return checked((durabilityMax * craftWearPerAttemptMilli + PerMilleCeilAddend) / 1000);
    }

    /// <summary>Stage 2's result: floor at zero, never below. Zero is "unusable" (T11's at-zero filter
    /// excludes it from the next materialize), and D1 is unamended — wear never destroys.</summary>
    public static long AfterWear(long durabilityCurrent, long wear) =>
        Math.Max(0L, durabilityCurrent - wear);
}
