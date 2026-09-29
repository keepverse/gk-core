namespace FusionRpg.Core.Creatures;

/// <summary>
/// Named ordinal arithmetic over <see cref="CreatureRarity"/> (spec-rarity-migration.md §3, §7 step 3).
/// A bare <c>(int)r Â± n</c> or <c>r &gt;= CreatureRarity.X</c> compiles at any enum width and silently
/// changes what fraction of the ladder it covers when the ladder widens — these helpers exist so that
/// intent survives a width change instead of being re-derived from the ordinal by eye. A guard test
/// forbids the bare forms outside this file.
/// </summary>
public static class CreatureRarityLadder
{
    /// <summary>DERIVED, never mirrored (tier-propagation-contract T-3). A const here and
    /// <see cref="All"/>'s Enum.GetValues disagree by construction the moment the enum widens, and
    /// OneRungAbove would then throw a message that lies about the cause.</summary>
    public static int RungCount => All.Count;

    /// <summary>The next rung up. Throws at the top — promotion logic must check
    /// <see cref="IsTopRung"/> first, matching every existing call site's own guard.</summary>
    public static CreatureRarity OneRungAbove(CreatureRarity rarity)
    {
        var next = NextOrdinal((int)rarity, RungCount);
        return (CreatureRarity)next;
    }

    /// <summary>The rung-boundary core OneRungAbove delegates to, over a plain
    /// <c>(ordinal, rungCount)</c> pair rather than the enum itself — so the throw boundary is
    /// provable at a widened width via synthetic counts a test double supplies. C# enums cannot
    /// widen at runtime, so no test can widen <see cref="CreatureRarity"/> itself.</summary>
    internal static int NextOrdinal(int ordinal, int rungCount)
    {
        var next = ordinal + 1;
        if (next >= rungCount)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, $"already the top rung of {rungCount}");
        return next;
    }

    /// <summary>The rung directly below. Throws at the bottom.</summary>
    public static CreatureRarity OneRungBelow(CreatureRarity rarity) => RungsBelow(rarity, 1);

    /// <summary>Clamped at the bottom (Chaff) rather than throwing — the shape
    /// <c>CreatureRecipeCatalog</c>'s "eligible input rarity" search needs: a recipe for a low-rung
    /// output should still resolve to Chaff, not fail, when asking for "N rungs below."</summary>
    public static CreatureRarity RungsBelow(CreatureRarity rarity, int rungs)
    {
        var target = (int)rarity - rungs;
        return (CreatureRarity)Math.Max(0, target);
    }

    /// <summary>True at the top of the ladder (Almanac) — the replacement for every
    /// <c>== CreatureRarity.Legendary</c>/<c>!= CreatureRarity.Legendary</c> "is this the cap" check.</summary>
    public static bool IsTopRung(CreatureRarity rarity) => rarity == CreatureRarity.Almanac;

    /// <summary>True at the bottom of the ladder (Chaff).</summary>
    public static bool IsBottomRung(CreatureRarity rarity) => rarity == CreatureRarity.Chaff;

    /// <summary>Ordinal-safe "at least this rung" — the direct replacement for
    /// <c>rarity &gt;= CreatureRarity.X</c>. Named so a reader sees the THRESHOLD, not an inlined
    /// ordinal comparison that silently covers a different fraction of the ladder once it widens.</summary>
    public static bool AtLeast(CreatureRarity rarity, CreatureRarity threshold) => (int)rarity >= (int)threshold;

    /// <summary>Ordinal-safe "at most this rung."</summary>
    public static bool AtMost(CreatureRarity rarity, CreatureRarity threshold) => (int)rarity <= (int)threshold;

    /// <summary>The rungs of a window, weakest first (species-gear-chain T6, wave-species-roll) —
    /// the only sanctioned place that walks the enum by ordinal besides <see cref="All"/> itself.
    /// Empty when <c>to</c> precedes <c>from</c>; callers treat empty as an inverted window, never a
    /// silent skip. Bare int↔rung casts anywhere else trip the ladder guard.</summary>
    public static IReadOnlyList<CreatureRarity> RungsBetween(CreatureRarity from, CreatureRarity to)
    {
        var list = new List<CreatureRarity>();
        for (var r = (int)from; r <= (int)to; r++)
            list.Add(All[r]);
        return list;
    }

    public static IReadOnlyList<CreatureRarity> All { get; } =
        Enum.GetValues<CreatureRarity>().OrderBy(r => (int)r).ToArray();
}
