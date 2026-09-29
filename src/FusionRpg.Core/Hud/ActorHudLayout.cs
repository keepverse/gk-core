namespace FusionRpg.Core.Hud;

/// <summary>Status strip priority and overflow — structural order, not tunable shuffle.</summary>
public static class ActorHudLayout
{
    /// <summary>
    /// Visible status tokens for the HUD strip. Priority: CC first, then stable id order.
    /// Identity row slot order when rows compete (unity/phaser): CC glyph, unique pip, shield,
    /// top N statuses, level badge — render modules consume the same visible list.
    /// </summary>
    public static (IReadOnlyList<ActorHudStatusToken> Visible, int OverflowCount) Prioritize(
        IEnumerable<ActorHudStatusToken> statuses,
        int maxVisible)
    {
        if (maxVisible < 0)
            throw new ArgumentOutOfRangeException(nameof(maxVisible));

        var ordered = statuses
            .OrderByDescending(s => s.Cc)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();

        var visibleCount = Math.Min(maxVisible, ordered.Count);
        var visible = ordered.Take(visibleCount).ToList();
        var overflow = Math.Max(0, ordered.Count - maxVisible);
        return (visible, overflow);
    }
}
