namespace FusionRpg.Core.Actions;

/// <summary>
/// T34's preference key (spec-action-selection.md §3): "tag preference (`offensive` before
/// `utility`), then `action_id` ordinal. Never catalog or dictionary order." A26 T67 added the rung
/// between the two: **tag rank, then `Rung` DESCENDING, then `action_id`**. The holder's own ladder
/// decides which of two same-tag actions is the one it wants — a rung-9 skill now outranks a rung-1
/// one regardless of id, which is the tiebreak's whole point (the stronger held action wins). The
/// spec names one example pair, not a full order — the rest of the ranking below is a decided-now
/// placeholder (same posture as T20's discard-tax coefficient): the RULE is fixed (full order,
/// offensive first, no two tags tie), the exact ranking is content to rebalance once a real stub AI
/// plays real matches.
/// <b>Explicitly not <see cref="FusionRpg.Core.Battle.Timeline.ActionEnvelope.PriorityBand"/></b> —
/// that field is a scheduling override baked into the event queue's own sort key.
/// </summary>
public static class ActionTagPreference
{
    static readonly IReadOnlyDictionary<ActionTag, int> Rank = new Dictionary<ActionTag, int>
    {
        [ActionTag.Offensive] = 0,
        [ActionTag.Debuff] = 1,
        [ActionTag.Buff] = 2,
        [ActionTag.Heal] = 3,
        [ActionTag.Summon] = 4,
        [ActionTag.Defensive] = 5,
        [ActionTag.Movement] = 6,
        [ActionTag.Utility] = 7,
        // Ranked last: placing a structure is the least urgent choice for a stub AI to default to
        // mid-fight — a real construction order comes from a deliberate caller, not from this
        // fallback preference (base-defense siege-construction, 2026-09-06).
        [ActionTag.Construct] = 8,
    };

    /// <summary>An action's own rank is its BEST (lowest) tag rank — an offensive-tagged heal is
    /// still preferred over a pure utility action. An untagged action ranks last of all; it is no
    /// longer "tied only by <c>action_id</c>" (T67): same-rank actions break by <c>Rung</c> descending
    /// first, and <c>action_id</c> orders only a same-rung pair.</summary>
    public static int RankOf(CompiledAction action)
    {
        var best = int.MaxValue;
        foreach (var tag in action.Tags)
            if (Rank.TryGetValue(tag, out var r) && r < best)
                best = r;
        return best;
    }

    /// <summary>Total order: tag rank, then <c>Rung</c> DESCENDING, then <c>action_id</c> ordinal —
    /// never catalog/dictionary iteration order. The rung step is A26 T67: `Rung` is the action's own
    /// authored ladder index, so the stronger of two same-tag actions is preferred whichever id it
    /// happens to carry. Every caller that sorts a loadout shares this one comparison
    /// (<c>BattleRunState</c>'s held-action sort and <c>SiegeAiIntentSource</c>'s own), so the ladder
    /// reaches both without a second ordering rule.</summary>
    public static int Compare(CompiledAction a, CompiledAction b)
    {
        var byRank = RankOf(a).CompareTo(RankOf(b));
        if (byRank != 0) return byRank;

        var byRung = b.Rung.CompareTo(a.Rung);
        return byRung != 0 ? byRung : string.CompareOrdinal(a.ActionId, b.ActionId);
    }
}
