namespace FusionRpg.Core.Items.Surfaces;

/// <summary>
/// What the armoury header reports about review pressure. Every field is a COUNT of rows the player
/// already owns — nothing here is compared against a budget the player may not exceed.
/// </summary>
/// <param name="Unseen">I13's inbox: <c>seen = 0</c>. <i>"An inbox can be emptied; a stash cannot."</i></param>
/// <param name="OverReviewPressure">The unreviewed count has passed
/// <c>reviewPressurePerContentEvent</c> — the point ssot-inventory.md measured players stopping
/// reading. ⛔ Advisory. Nothing refuses, nothing throttles, nothing stops dropping.</param>
public readonly record struct ArmouryInbox(int Unseen, int Total, bool OverReviewPressure);

/// <summary>
/// A saved view rule over the armoury. Every field is optional and an unset field imposes no
/// constraint, matching <see cref="ArmouryFilter"/>'s own shape — this is the player's persistent
/// *filter preset*, where <c>ArmouryFilter</c> is one query's ad-hoc terms.
/// </summary>
/// <param name="HideBelowRarityOrdinal">The "salvage everything below X" line, as a VIEW rule.
/// <c>0</c> hides nothing.</param>
/// <param name="HideAssigned">Hide what is already equipped, so the list is what could change.</param>
/// <param name="HideStale">Hide rows a content edit has touched, so a migration does not flood the list.</param>
/// <param name="UnseenOnly">The inbox view.</param>
public readonly record struct LootFilterRule(
    int HideBelowRarityOrdinal = 0,
    bool HideAssigned = false,
    bool HideStale = false,
    bool UnseenOnly = false)
{
    public static LootFilterRule Default(ItemSurfaceTuning tuning) =>
        tuning is null
            ? throw new ArgumentNullException(nameof(tuning))
            : new LootFilterRule(HideBelowRarityOrdinal: tuning.DefaultHideBelowRarityOrdinal);
}

/// <summary>
/// The loot filter — <b>a client-side view rule over rows the player already owns</b>.
///
/// <para>⛔ <b>I12's <c>40/day</c> is not a drop cap and this file is where that is enforced by
/// construction.</b> D26 forbids metering the player, and spec-item-surfaces.md is explicit: the
/// <c>40/day</c> line is I12 asking for <i>a loot filter — an interface requirement, not a cap</i>.
/// Every method here takes an already-materialised row list and returns a subset of it. There is no
/// path from this file to generation: it references no drop table, no pipeline, no pity counter and
/// no store, and a guard test asserts exactly that by reading its own source. <b>Hiding a row changes
/// no drop.</b></para>
///
/// <para>⚠ <b>And I12's axis is restated.</b> <i>"I12's 20–30 items/day imports a wall-clock axis the
/// game does not have. Restate per CONTENT EVENT, not per day."</i> So the watch numbers in
/// <c>gk-core/data/tuning/item-surfaces.v1.json</c> are per content event, and this file never reads a clock —
/// it has no parameter that could carry one.</para>
///
/// <para><b>The four pressures I13 names instead of a bag cap</b> are the inbox
/// (<see cref="Inbox"/>), the gap board (module 2's <c>ArmouryQuery</c> plus the equip surface),
/// auto-salvage at the drop boundary (module 14's <c>SalvagePolicy</c>, not a view rule and
/// deliberately not here), and the two-grade split that keeps stock as counters (module 2's
/// <see cref="StorageGrading"/>). This file owns the first, and names the other three so nobody
/// re-implements one of them here.</para>
/// </summary>
public static class LootFilterView
{
    /// <summary>
    /// Apply the view rule. Pure over the sequence — the caller has already filtered, sorted and
    /// paged with <see cref="ArmouryQuery"/>, and this narrows what is DRAWN.
    /// </summary>
    public static IEnumerable<ArmouryEntry> Apply(IEnumerable<ArmouryEntry> entries, LootFilterRule rule)
    {
        if (entries is null) throw new ArgumentNullException(nameof(entries));

        // ⛔ A locked row is NEVER hidden, and the exemption is first so it cannot be reordered away.
        // The player marked it keep; a filter that could hide it is a filter that can lose it, and
        // "where did my locked item go" is an inventory bug report. Written as one predicate rather
        // than a union so the caller's sort order survives untouched.
        return entries.Where(e => e.Locked || Passes(e, rule));
    }

    static bool Passes(ArmouryEntry e, LootFilterRule rule)
    {
        if (rule.HideBelowRarityOrdinal > 0 && e.RarityOrdinal < rule.HideBelowRarityOrdinal) return false;
        if (rule.HideAssigned && e.Assigned) return false;
        if (rule.HideStale && e.Stale) return false;
        if (rule.UnseenOnly && !e.Unseen) return false;
        return true;
    }

    /// <summary>
    /// The inbox count and the review-pressure flag. Counted over the WHOLE armoury, never over the
    /// filtered view — an inbox you can empty by hiding it is not an inbox.
    /// </summary>
    public static ArmouryInbox Inbox(IReadOnlyList<ArmouryEntry> allEntries, ItemSurfaceTuning tuning)
    {
        if (allEntries is null) throw new ArgumentNullException(nameof(allEntries));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        var unseen = allEntries.Count(e => e.Unseen);
        return new ArmouryInbox(unseen, allEntries.Count, unseen > tuning.ReviewPressurePerContentEvent);
    }
}
