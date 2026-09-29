namespace FusionRpg.Core.Items.Mutation;

/// <summary>What a tier decision did, so the caller can prove the guarantee shifted no weight.</summary>
/// <param name="Tier">The tier the reroll lands on.</param>
/// <param name="Guaranteed">True when the tier was PLACED, not rolled.</param>
/// <param name="CounterAfter">Reset to zero on a guarantee, otherwise incremented.</param>
public readonly record struct CraftPityDecision(int Tier, bool Guaranteed, int CounterAfter);

/// <summary>
/// ⛔ <b>Bad-luck protection — mandatory, not optional (D7).</b> Craft pity is a separate
/// deterministic mechanism from drop pity, and it touches no weight.
///
/// <para><b>Why it does not break ssot-rarity.md §3.8.</b> Read §3.8's own heading before quoting it
/// as a universal law: every rule under it is about <i>counted drop sources</i> and every lever is a
/// <b>weight shift</b> on a draw, because §3.5's overlap invariant is <i>measured</i> on independent
/// draws (2×10^5 rolls per rung, seed 20260822). This mechanism says nothing about a drop and moves
/// no weight: below the threshold the container's weighted tier draw runs exactly as it always did,
/// and at the threshold <b>the draw is not run at all</b> — the tier is placed at the container's
/// <c>max_tier</c> and the counter resets. Independent draws stay independent, so the measurement
/// stands. Scoped as D31 (module 7 owns the one-line edit to §3.8's text, and it has landed).</para>
///
/// <para><b>The precedent is in-tree and verified:</b> <c>rpg_summon_pity</c> — a persisted per-player
/// counter table read and written inside the pull transaction (<c>RpgStore.cs:529-534</c>,
/// <c>SummonRoller.cs:12</c>). This reuses its shape: durable state, one integer, visible, reset on
/// the guarantee. The one difference is scope — the craft counter lives per
/// <c>(instance, affix group)</c> on the instance's own <c>enhance_pity_counter</c> column, not per
/// player, because a craft is an operation on an item the player already owns.</para>
///
/// <para>⭐ <b>This is I7's own Imprint, corrected.</b> I7 §3.4 reached for a deterministic escape
/// hatch and placed it at the window FLOOR ("deliberately mediocre"), then rejected a counter for
/// needing durable state. Two things changed: D7 makes the CEILING reachable by cost rather than the
/// floor, and the durable state is one integer on a column this module adds anyway.</para>
/// </summary>
public static class CraftPityCounter
{
    /// <summary>True when the next reroll on this group is guaranteed.</summary>
    public static bool IsGuaranteed(int counter, EnhancementTuning t)
    {
        if (counter < 0) throw new ArgumentOutOfRangeException(nameof(counter), counter, "a pity counter cannot be negative");
        return counter >= t.CraftPityThreshold;
    }

    /// <summary>
    /// The tier this reroll lands on. <paramref name="pickTier"/> is the container's ordinary
    /// weighted draw and <b>is not called at all</b> on a guaranteed reroll — that, and not a
    /// re-weighting, is how the guarantee avoids touching §3.5's measured independence.
    /// </summary>
    public static CraftPityDecision TierFor(int counter, int minTier, int maxTier, Func<int, int, int> pickTier, EnhancementTuning t)
    {
        if (pickTier is null) throw new ArgumentNullException(nameof(pickTier));
        if (maxTier < minTier)
            throw new ArgumentOutOfRangeException(nameof(maxTier), maxTier, $"max tier {maxTier} is below min tier {minTier}");

        if (IsGuaranteed(counter, t))
            return new CraftPityDecision(maxTier, Guaranteed: true, CounterAfter: 0);

        var tier = pickTier(minTier, maxTier);
        if (tier < minTier || tier > maxTier)
            throw new InvalidOperationException($"the weighted tier draw returned t{tier}, outside the container window t{minTier}..t{maxTier}");

        // A roll that already reached the ceiling resets the counter too: the counter exists to
        // guarantee max_tier, and it has just been reached. Anything else would keep counting toward
        // a guarantee of something the player already holds.
        var reached = tier == maxTier;
        return new CraftPityDecision(tier, Guaranteed: false, CounterAfter: reached ? 0 : checked(counter + 1));
    }
}
