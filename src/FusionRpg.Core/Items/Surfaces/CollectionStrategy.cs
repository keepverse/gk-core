namespace FusionRpg.Core.Items.Surfaces;

/// <summary>GG-50's three bands. Closed — a collection has no fourth answer to "what do you do at
/// a thousand rows".</summary>
public enum RenderStrategy
{
    /// <summary>Draw every row. The only band where a scroll position is a real position.</summary>
    RenderAll = 0,

    /// <summary>Draw a window. The row count is still exact; only the DOM is bounded.</summary>
    Virtualize,

    /// <summary>Draw nothing until the player narrows it. The list is still complete underneath —
    /// this band changes the entry point, never the contents.</summary>
    SearchFirst,
}

/// <summary>
/// GG-50 — "every collection declares its behaviour at 10, 100 and 1000" — as a pure function of the
/// row count and the tuning, rather than as a paragraph in a component nobody re-reads.
///
/// <para>⛔ <b>No band refuses a row.</b> Every strategy renders the same complete collection; they
/// differ only in how much of it reaches the DOM at once. A "cap" here would be a bag cap wearing a
/// layout name, and §2.5 forbids one outright ("a bag cap here would be pure friction in a browser
/// tab"). <c>RpgStore.InventoryCeiling</c> is a different thing — module 2's structural abuse guard —
/// and it says so in its own comment.</para>
///
/// <para>The paperdoll and the compendium are deliberately absent: 15 cells is bounded by
/// construction, and 127 is the compendium's whole population, so both are
/// <see cref="RenderStrategy.RenderAll"/> at every magnitude. Saying that here, rather than passing
/// them through a threshold that can never fire, keeps the thresholds meaningful.</para>
/// </summary>
public static class CollectionStrategy
{
    public static RenderStrategy For(int rowCount, ItemSurfaceTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (rowCount < 0) throw new ArgumentOutOfRangeException(nameof(rowCount), rowCount, "a collection cannot hold fewer than zero rows");

        if (rowCount <= tuning.RenderAllThrough) return RenderStrategy.RenderAll;
        if (rowCount <= tuning.VirtualizeThrough) return RenderStrategy.Virtualize;
        return RenderStrategy.SearchFirst;
    }
}
