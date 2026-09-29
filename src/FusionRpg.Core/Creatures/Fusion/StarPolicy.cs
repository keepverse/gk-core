namespace FusionRpg.Core.Creatures.Fusion;

/// <summary>
/// Star-merge rules (spec-creature-fusion.md, owner locks 2026-08-21). Numbers are spec-locked;
/// tuning is ask-first (balance boundary). Per-star bonuses reach battles ONLY as squad-build
/// ChannelMods — never engine changes (battle goldens stay byte-identical).
/// </summary>
public static class StarPolicy
{
    static FusionTuning? _tuning;

    /// <summary>Host-only (Injector/Server startup, or a test's inline construction) — also
    /// configures <see cref="FusionCostTable"/>, which reads the same tuning file.</summary>
    public static void Configure(FusionTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    internal static FusionTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "StarPolicy.Configure(...) has not run. Every fusion rule reads data/tuning/fusion.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.");

    /// <summary>The per-star value at the REFERENCE star (<see cref="ReferenceStar"/>) — the anchor the
    /// curve in <see cref="StarPowerMilli"/> is scaled around, not a flat per-star rate any more.</summary>
    public static int PerStarPowerMilli => Tuning.PerStarPowerMilli;
    public static int PerStarDefenseMilli => Tuning.PerStarDefenseMilli;

    /// <summary>Highest star any rarity may reach. Structural — it is the star system's own range,
    /// not a balance number (the per-rarity cap inside it IS tunable, see <see cref="StarCap"/>).
    /// Raised 5 -> 10 on 2026-09-05 by owner decision, alongside the reward pairing below.</summary>
    public const int MaxStar = 10;

    /// <summary>The star the reward curve is anchored at, so raising <see cref="MaxStar"/> never
    /// silently revalues the stars players already own. Structural, not tunable.</summary>
    public const int ReferenceStar = 5;

    /// <summary>
    /// Per-mille power bonus at <paramref name="star"/>, PAIRED to the triangular sacrifice cost
    /// (effort-power reconciliation M4, owner decision 2026-09-05).
    ///
    /// <para><b>The defect this fixes.</b> Cumulative sacrifices to star n are
    /// <c>C(n) = n(n+3)/2</c> — triangular — while the reward was <c>perStar x n</c>, linear. Reward
    /// per sacrifice was therefore <c>60/(n+3)</c>: star 5 was an exactly 2x worse deal than star 1.
    /// This is the same shape as the passive tree's tier-ladder defect (D26), and the same fix:
    /// index the reward on the cost.</para>
    ///
    /// <para><b>The anchor.</b> <c>R(n) = k*C(n)</c> with <c>k</c> chosen so
    /// <c>R(ReferenceStar)</c> equals what the old linear reading paid there — so no creature at or
    /// below star 5 changes value at its cap. That gives <c>k = perStar/4</c> and
    /// <c>R(n) = perStar*n*(n+3)/(ReferenceStar+3)</c>. That divisor is DERIVED from those two
    /// facts, not a tuned number.</para>
    ///
    /// <para>At <c>perStar = 30</c>: 15 · 38 · 68 · 105 · <b>150</b> · 203 · 263 · 330 · 405 · 488.</para>
    /// </summary>
    public static int StarPowerMilli(int star) => PairedStarMilli(star, Tuning.PerStarPowerMilli);

    /// <summary>Defensive half of <see cref="StarPowerMilli"/>, same curve.</summary>
    public static int StarDefenseMilli(int star) => PairedStarMilli(star, Tuning.PerStarDefenseMilli);

    static int PairedStarMilli(int star, int perStarMilli)
    {
        if (star <= 0) return 0;
        // Widen before multiplying; divide once, half away from zero.
        // The divisor is DERIVED, not chosen: R(n) = k*C(n) with C(n) = n(n+3)/2, anchored so
        // R(ReferenceStar) = perStar*ReferenceStar. Solving gives k = 2*perStar/(ReferenceStar+3),
        // hence R(n) = perStar*n*(n+3)/(ReferenceStar+3) -- the divisor is simply ReferenceStar+3.
        long numerator = checked((long)perStarMilli * star * (star + 3));
        const long divisor = ReferenceStar + 3;
        const long half = divisor / 2;
        return checked((int)((numerator + (numerator >= 0 ? half : -half)) / divisor));
    }

    public static int StarCap(CreatureRarity rarity) =>
        Tuning.StarCap.TryGetValue(rarity, out var v) ? v : Tuning.StarCap[CreatureRarity.Almanac];

    /// <summary>Same-rarity sacrifices consumed to reach star n (n+1 curve, a 2026-08-21 owner lock —
    /// unchanged; the REWARD was re-indexed onto it instead, see <see cref="StarPowerMilli"/>).</summary>
    public static int SacrificesForStar(int targetStar)
    {
        if (targetStar < 1 || targetStar > MaxStar)
            throw new ArgumentOutOfRangeException(
                nameof(targetStar), targetStar, $"stars run 1..{MaxStar}");
        return targetStar + 1;
    }

    /// <summary>Promotion: once per specimen, only at the star cap, never above the top rung (Almanac).</summary>
    public static bool CanPromote(CreatureRarity rarity, int star, bool promoted) =>
        !promoted && !CreatureRarityLadder.IsTopRung(rarity) && star >= StarCap(rarity);
}

/// <summary>One fusion's price: Souls fee + rarity-banded shards + result-element essences.</summary>
public sealed record FusionCost(long Souls, CreatureRarity ShardRarity, int ShardCount, int EssenceCount);

public static class FusionCostTable
{
    public static FusionCost StarMerge(CreatureRarity baseRarity)
    {
        var c = StarPolicy.Tuning.StarMergeCost;
        return new FusionCost(c.Souls, baseRarity, c.ShardCount, c.EssenceCount);
    }

    /// <summary>Promotion price, per rung (effort-power M5, owner decision 2026-09-05). It was a flat
    /// 200 souls at every rung while <see cref="Recipe"/> escalated 150 -> 1000 in the same file, so
    /// promoting at the top of the ladder cost the same as at the bottom. It now escalates on the same
    /// shape. Promotion is once per specimen, so this cannot compound.</summary>
    public static FusionCost Promotion(CreatureRarity newRarity)
    {
        var c = StarPolicy.Tuning.PromotionCostByRarity.TryGetValue(newRarity, out var byRarity)
            ? byRarity
            : StarPolicy.Tuning.PromotionCost;
        return new FusionCost(c.Souls, newRarity, c.ShardCount, c.EssenceCount);
    }

    public static FusionCost Recipe(CreatureRarity resultRarity)
    {
        if (!StarPolicy.Tuning.RecipeCost.TryGetValue(resultRarity, out var c))
            throw new ArgumentOutOfRangeException(nameof(resultRarity), resultRarity,
                "recipes only produce rare and above");
        return new FusionCost(c.Souls, c.ShardRarity, c.ShardCount, c.EssenceCount);
    }

    /// <summary>
    /// WAVE F2.3/F2.4: an inherited pick's own cost, keyed by the PICK'S OWN source specimen's
    /// rarity — never the fusion output's own rarity (that's <see cref="Recipe"/>, a different
    /// lookup key over the identical rung set). Souls only: F2.4 only ever sums souls for a pick set,
    /// never shards/essences.
    /// </summary>
    public static long InheritPick(CreatureRarity sourceRarity)
    {
        if (!StarPolicy.Tuning.InheritCostByRarity.TryGetValue(sourceRarity, out var souls))
            throw new ArgumentOutOfRangeException(nameof(sourceRarity), sourceRarity,
                "inherited picks only come from rare-and-above sacrifices — see recipeCost's own floor");
        return souls;
    }
}
