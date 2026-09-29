namespace FusionRpg.Core.Items.Uniques;

/// <summary>
/// The **affix-equivalent (AE)** reckoner ssot-uniques.md §3.7 device 2 prices against, and §9.1's
/// missing publication — *"the rolled baseline in AE per rung, which §3.7's budget check divides by
/// and which does not exist in any document yet."*
///
/// <para><b>AE is denominated in the rolled ladder's own units, not a new scale.</b> One AE is one
/// rolled affix at the middle of the rung's tier window, which is exactly
/// <see cref="RarityOverlapSimulator.TierMidpoint"/> of that window's middle tier — the same table the
/// overlap harness rolls from. Nothing here declares a magnitude of its own; a second table would put
/// the unique budget and the rarity ladder on two scales that drift apart silently.</para>
///
/// <para><b>Every magnitude is <c>long</c>, widened before multiplying, and divided last</b>
/// (AGENTS.md). AE is carried as <b>AE × 100</b> throughout — SC4 forbids floats in content, and
/// `item_unique.budget_ae` is that integer.</para>
///
/// <para>⚠ <b>The baseline reads the count-band FLOOR, and that is a documented understatement.</b>
/// `gk-data/packs/fusion/data/seed/rarity/ladder.v1.json` carries <c>prefixRolls</c>/<c>suffixRolls</c> as the floor of
/// ssot-rarity §3.3's published half-ranges because the shipped schema has no <c>_max</c> column
/// (module 7's own recorded ask-first). A floor baseline makes the allowance <i>smaller</i>, so a
/// budget refusal computed from it is conservative in the direction that <b>over</b>-refuses. Every
/// caller that reports rather than refuses says so.</para>
/// </summary>
public static class UniqueBudget
{
    /// <summary>AE is carried × 100. One place, so no call site invents a second scale factor.</summary>
    public const long AeScale = 100;

    /// <summary>
    /// `bands.v1.json powerBand.tierMap` — what an author writes instead of a magnitude. Five bands,
    /// one per tier the atom layer already has; a sixth would need a `.t6` row on every family, which
    /// is an atom-layer change, not a registry one.
    /// </summary>
    public static int TierOfPowerBand(string powerBand) => powerBand switch
    {
        "trivial" => 1,
        "low" => 2,
        "medium" => 3,
        "high" => 4,
        "extreme" => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(powerBand),
            $"'{powerBand}' is not one of bands.v1.json's five powerBand values"),
    };

    /// <summary>
    /// The tier one AE is measured at for a rung: the middle of that rung's authored tier window.
    /// Rounded down, so an even window (t2–t5) prices against the lower of its two middles — the
    /// conservative half, and stated because rounding either way is defensible and only one can ship.
    /// </summary>
    public static int ReferenceTier(RarityRungWindow rung) => (rung.MinTier + rung.MaxTier) / 2;

    /// <summary>The magnitude one AE is worth at this rung, in the harness's own hp units.</summary>
    public static long ReferenceMagnitude(RarityRungWindow rung) =>
        RarityOverlapSimulator.TierMidpoint(ReferenceTier(rung));

    /// <summary>
    /// The rung's rolled baseline in AE × 100 — §9.1's missing number, published here as
    /// <c>affixCount × 1 AE</c>. The count is the seeded floor; see the class remark.
    /// </summary>
    public static long RungBaselineAeHundredths(RarityRungWindow rung) =>
        (long)rung.AffixCount * AeScale;

    /// <summary>§3.7 device 2: the rung's baseline plus the shared 1.5 AE premium.</summary>
    public static long AllowanceAeHundredths(RarityRungWindow rung, UniqueTuning tuning) =>
        RungBaselineAeHundredths(rung) + tuning.BudgetPremiumAeHundredths;

    /// <summary>
    /// Price one magnitude, in the harness's hp units, as AE × 100 at this rung.
    /// <b>Widen before multiplying, divide last</b> — the magnitude is already <c>long</c> and the
    /// ×100 happens before the single division, so no intermediate is a per-mille-scaled product of
    /// two already-scaled numbers.
    /// </summary>
    public static long AeHundredthsOf(long magnitude, RarityRungWindow rung)
    {
        var reference = ReferenceMagnitude(rung);
        if (reference <= 0)
            throw new InvalidOperationException(
                $"rung '{rung.RarityId}' prices one AE at {reference}, so no content on it can be priced " +
                "at all — a zero reference is a ladder defect, not a rounding case");

        return checked(magnitude * AeScale) / reference;
    }

    /// <summary>
    /// Price one authored band at a rung. The seed corpus writes a <c>powerBand</c>, never a number
    /// (seed-contract.md §3), so this is the whole conversion from what an author may write to what
    /// the budget check may read.
    /// </summary>
    public static long AeHundredthsOfBand(string powerBand, RarityRungWindow rung) =>
        AeHundredthsOf(RarityOverlapSimulator.TierMidpoint(TierOfPowerBand(powerBand)), rung);
}
