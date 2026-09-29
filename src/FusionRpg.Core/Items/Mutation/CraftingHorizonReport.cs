using FusionRpg.Core.Power;

namespace FusionRpg.Core.Items.Mutation;

/// <summary>
/// One depth's answer to "what is a full crafting investment worth, in realms".
/// </summary>
/// <param name="ThetaContent">The content depth Θc the item dropped at.</param>
/// <param name="ItemLevel">The item's level — what sets its own enhancement reach.</param>
/// <param name="MaxLevel">`ilvl_cap(ilvl)` — the top of the track for THIS item.</param>
/// <param name="GainMilli">The whole track's gain in per-mille (0 = no gain, 240 = ×1.24).</param>
/// <param name="ThetaPrimeMilli">The Θ×1000 at which a fresh drop matches the crafted item.</param>
/// <param name="DeltaThetaMilli">ThetaPrimeMilli − Θc×1000.</param>
/// <param name="RealmsMilli">N×1000 — the headline number. 190 means "about a fifth of a realm".</param>
public readonly record struct CraftingHorizonRow(
    int ThetaContent, int ItemLevel, int MaxLevel, long GainMilli,
    long ThetaPrimeMilli, long DeltaThetaMilli, long RealmsMilli);

/// <summary>
/// ⛔ spec-enhance-reroll.md §4b — <b>the sinks have an expiry date, and it is inside D26's scope.</b>
/// Every spec in this program pushed this out as content pacing; it is not. D26 draws the line at
/// "the item system balances items against each other; it does not balance the game", and "is a
/// crafted item still worth more than a fresh drop" is an item-versus-item comparison.
///
/// <para><b>Computed, never authored.</b> Every input is a shipped constant read at runtime: the
/// curve from <see cref="PowerTuning"/> (<c>gk-core/data/tuning/power-scale.v2.json</c>), one realm from the
/// same file's <c>WfMilli</c>, the enhancement reach from
/// <see cref="EnhancePolicy.MaxLevelForItemLevel"/> and the gain from either the naive linear track
/// or §4a's asymptote with module 7's seeded <c>enhance_cap</c>. <b>So the figure moves when the
/// dials move</b>, instead of being a number in a document that silently goes stale.</para>
///
/// <para>⛔ The recorded conclusion, reproduced by <see cref="V1Reach"/>: <b>N ≈ 0.19 at everything
/// the game currently ships.</b> §2h.3's threshold is 2, and N does not reach it until Θc ≈ 123 —
/// five realms deep into a content ladder that stops at level 10 today. <b>Do not size §4's risk
/// bands or §5's pity threshold as if they were a progression choice at v1 depth.</b> They are not;
/// the player advances a realm instead.</para>
/// </summary>
public static class CraftingHorizonReport
{
    /// <summary>
    /// §4b's headline row. <b>Θc is not a constant here</b> — it is the power ladder's own
    /// <c>pinIndex</c>, read from the loaded tuning, so v1's reach cannot drift apart from the curve
    /// it is measured against. The item level is the caller's, because it is D4's content decision
    /// and not this module's to hardcode.
    /// </summary>
    public static CraftingHorizonRow V1Reach(EnhancementTuning t, PowerTuning power, int itemLevel) =>
        LinearRow(power.Curve.PinIndex, itemLevel, t, power);

    /// <summary>The naive linear track (I6 §3.3) at this depth — §4b's own table is computed on it.</summary>
    public static CraftingHorizonRow LinearRow(int thetaContent, int itemLevel, EnhancementTuning t, PowerTuning power)
    {
        var maxLevel = EnhancePolicy.MaxLevelForItemLevel(itemLevel, t);
        return Row(thetaContent, itemLevel, maxLevel, EnhancePolicy.LinearGainMilli(maxLevel, t), power);
    }

    /// <summary>
    /// §4a's shipped asymptotic track at this depth, for one rung's seeded <c>enhance_cap</c>. It is
    /// deliberately SMALLER than <see cref="LinearRow"/>: the alternative is a gain that inverts the
    /// rarity ladder, which §4a exists to prevent.
    /// </summary>
    public static CraftingHorizonRow CappedRow(int thetaContent, int itemLevel, int enhanceCapMilli, EnhancementTuning t, PowerTuning power)
    {
        var maxLevel = EnhancePolicy.MaxLevelForItemLevel(itemLevel, t);
        return Row(thetaContent, itemLevel, maxLevel, EnhancePolicy.GainMilli(maxLevel, enhanceCapMilli, t), power);
    }

    /// <summary>
    /// The asymptote itself — the most a rung can EVER gain, at any n. On `almanac`
    /// (<c>enhance_cap</c> 200‰) this is ×1.20 and N ≤ 0.16.
    /// </summary>
    public static CraftingHorizonRow AsymptoteRow(int thetaContent, int itemLevel, int enhanceCapMilli, PowerTuning power) =>
        Row(thetaContent, itemLevel, int.MaxValue, enhanceCapMilli, power);

    /// <summary>
    /// The core computation. <c>N(Θc) = (Θc′ − Θc) / realm</c> where <c>P(Θc′) = gain × P(Θc)</c>.
    ///
    /// <para>Θc′ is solved by <b>linear interpolation between the two integer Θ that bracket the
    /// target</b>, on the ladder's own exact per-mille values — no floating point anywhere, and no
    /// bracketing-and-reporting-the-integer, which would round a 0.19 to 0.</para>
    /// </summary>
    public static CraftingHorizonRow Row(int thetaContent, int itemLevel, int maxLevel, long gainMilli, PowerTuning power)
    {
        if (power is null) throw new ArgumentNullException(nameof(power));
        var ladder = new PowerLadder(power);

        var here = ladder.ValueMilli(thetaContent);
        // Widen before multiplying; divide by 1000 exactly once, at the end.
        var target = checked(here * (1000L + gainMilli)) / 1000L;

        var thetaPrimeMilli = SolveThetaMilli(ladder, target, thetaContent);
        var deltaMilli = thetaPrimeMilli - (long)thetaContent * 1000L;
        var realmMilli = power.Weights.WfMilli;
        if (realmMilli <= 0)
            throw new InvalidOperationException("power tuning: one realm (WfMilli) must be positive to report a crafting horizon");

        var realmsMilli = checked(deltaMilli * 1000L) / realmMilli;
        return new CraftingHorizonRow(thetaContent, itemLevel, maxLevel, gainMilli, thetaPrimeMilli, deltaMilli, realmsMilli);
    }

    /// <summary>
    /// The first content depth at which a full crafting investment is worth
    /// <paramref name="realmsMilli"/> realms — §2h.3's threshold of 2 lands at Θc ≈ 123 on the naive
    /// track. Searched, not authored; it moves when <c>bMilli</c> or the enhancement dials move.
    /// </summary>
    public static int FirstThetaReachingRealms(long realmsMilli, EnhancementTuning t, PowerTuning power, int searchTo = 1000)
    {
        for (var theta = 1; theta <= searchTo; theta++)
        {
            // ilvl = Θc on the search rows, matching §4b's own table for every row but the first.
            if (LinearRow(theta, theta, t, power).RealmsMilli >= realmsMilli) return theta;
        }

        throw new InvalidOperationException(
            $"no content depth up to Θ={searchTo} reaches {realmsMilli / 1000.0:0.00} realms of crafting value");
    }

    static long SolveThetaMilli(PowerLadder ladder, long targetMilli, int from)
    {
        var theta = from;
        var lower = ladder.ValueMilli(theta);
        if (lower >= targetMilli) return (long)theta * 1000L;

        while (true)
        {
            var upper = ladder.ValueMilli(theta + 1);
            if (upper >= targetMilli)
            {
                var span = upper - lower;
                var into = span == 0 ? 0 : checked((targetMilli - lower) * 1000L) / span;
                return checked((long)theta * 1000L + into);
            }

            theta++;
            lower = upper;
        }
    }
}
