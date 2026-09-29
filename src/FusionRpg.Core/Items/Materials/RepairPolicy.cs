using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Items.Materials;

/// <summary>What one repair attempt decided (spec-item-durability-repair.md §5).</summary>
public readonly record struct RepairOutcome(long RestoredCurrent, bool Destroyed);

/// <summary>
/// species-gear-chain T23 — the workbench repair's one shared restore function
/// (`spec-item-durability-repair.md` §5, D1/D2/D5). Pure: no I/O, no tuning read — the coverage, the
/// tier cap and the destroy chance all arrive as arguments, and the RNG is the attempt's own named
/// stream, so the caller owns every decision this file does not make.
/// </summary>
public static class RepairPolicy
{
    /// <summary>Certainty. A bounded ratio, so it is exempt from the no-hard-ceiling rule — a fraction
    /// of the missing durability cannot exceed the whole of it.</summary>
    public const long FullRestoreMilli = 1000;

    /// <summary>
    /// Roll the destroy chance <b>first</b>, then — only if the item survives — restore a share of the
    /// missing durability.
    ///
    /// <para><b>D1:</b> every attempt carries a tunable chance to destroy the item outright. That is
    /// the one sink that removes an item from the economy, and it is why wear itself never destroys
    /// (T24). <b>D2:</b> the restore is <i>graded</i>, never a refusal — the material on hand sets the
    /// coverage and the tier's cap bounds it, so a short material set yields the partial/eroding
    /// result rather than an error. Both ratios are bounded per-mille.</para>
    /// </summary>
    public static RepairOutcome Resolve(
        long current, long max, long materialCoverageMilli, long tierCapMilli,
        long destructionChanceMilli, SeededRng rng)
    {
        if (current < 0)
            throw new ArgumentOutOfRangeException(nameof(current), current, "durability cannot be negative");
        if (max < 0)
            throw new ArgumentOutOfRangeException(nameof(max), max, "a durability max cannot be negative");
        if (current > max)
            throw new ArgumentOutOfRangeException(nameof(current), current, "durability cannot exceed its own max");
        if (destructionChanceMilli is < 0 or > FullRestoreMilli)
            throw new ArgumentOutOfRangeException(nameof(destructionChanceMilli), destructionChanceMilli,
                "the destroy chance is a bounded per-mille ratio");
        ArgumentNullException.ThrowIfNull(rng);

        // ⭐ D1 — the destroy roll comes FIRST, on this attempt's own named stream and before any
        // restore is computed, so a destroyed item never pays for a restore it did not receive.
        if (destructionChanceMilli > 0 && rng.NextPerMille() < destructionChanceMilli)
            return new RepairOutcome(current, Destroyed: true);

        var missing = max - current;
        if (missing <= 0) return new RepairOutcome(current, Destroyed: false);

        // D2 — graded. `min(coverage, cap)` because a field touch-up's cap is below full by design
        // (D2's "partial/eroding") and a short material set can only lower it further.
        var coverage = Math.Clamp(materialCoverageMilli, 0, FullRestoreMilli);
        var cap = Math.Clamp(tierCapMilli, 0, FullRestoreMilli);
        var share = Math.Min(coverage, cap);
        var restored = checked(missing * share / FullRestoreMilli);
        return new RepairOutcome(Math.Min(max, current + restored), Destroyed: false);
    }

    /// <summary>
    /// species-gear-chain T44 (`craft-assurance` f) — each loaded `assurance.repair` charge adds
    /// <c>repairCoverageBonusMilli</c> to the base material coverage, computed HERE (pure, widened
    /// before multiplying) so the addition is testable independent of whatever base value a caller
    /// happens to pass. <see cref="Resolve"/>'s own <c>Math.Clamp(materialCoverageMilli, 0,
    /// FullRestoreMilli)</c> still bounds the result — a bonus can never push coverage past
    /// certainty, so an already-maxed baseline (the workbench's own current
    /// <see cref="FullRestoreMilli"/> call) is genuinely unaffected, which is the honest state of a
    /// bonus with nothing left to raise, not a bug in the wiring. R10: this feeds
    /// <paramref name="baseCoverageMilli"/> only — never <c>destructionChanceMilli</c>, which no
    /// assurance effect may touch.
    /// </summary>
    public static long CoverageWithAssurance(long baseCoverageMilli, long repairLoaded, long repairCoverageBonusMilli)
    {
        if (repairLoaded < 0)
            throw new ArgumentOutOfRangeException(nameof(repairLoaded), repairLoaded, "a loaded charge count cannot be negative");
        if (repairCoverageBonusMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(repairCoverageBonusMilli), repairCoverageBonusMilli,
                "a per-charge coverage bonus cannot be negative");
        return checked(baseCoverageMilli + repairLoaded * repairCoverageBonusMilli);
    }
}
