namespace FusionRpg.Core.Creatures;

/// <summary>
/// Named ordinal arithmetic over <see cref="CreatureRank"/> (spec-species-rank.md §3), mirroring
/// <see cref="CreatureRarityLadder"/> exactly. A bare <c>(int)rank ± n</c> or
/// <c>rank &gt;= CreatureRank.X</c> compiles at any enum width and silently changes what fraction of
/// the ladder it covers when the ladder widens — these helpers exist so the intent survives a width
/// change instead of being re-derived from the ordinal by eye. A guard test forbids the bare forms
/// outside this file.
///
/// <para>Rank is identity, never a magnitude: it gates and displays, it never prices, scales, or
/// contests. Any use of rank in a magnitude path is a bug.</para>
/// </summary>
public static class CreatureRankLadder
{
    /// <summary>DERIVED, never mirrored (tier-propagation-contract T-3). A const here and
    /// <see cref="All"/>'s Enum.GetValues disagree by construction the moment the enum widens, and
    /// OneRungAbove would then throw a message that lies about the cause.</summary>
    public static int RungCount => All.Count;

    /// <summary>The next rung up. Throws at the top — a caller walking upward must check the top
    /// first, matching <see cref="CreatureRarityLadder.OneRungAbove"/>'s own contract.</summary>
    public static CreatureRank OneRungAbove(CreatureRank rank)
    {
        var next = NextOrdinal((int)rank, RungCount);
        return (CreatureRank)next;
    }

    /// <summary>The rung-boundary core OneRungAbove delegates to, over a plain
    /// <c>(ordinal, rungCount)</c> pair rather than the enum itself — so the throw boundary is
    /// provable at a widened width via synthetic counts a test double supplies. C# enums cannot
    /// widen at runtime, so no test can widen <see cref="CreatureRank"/> itself.</summary>
    internal static int NextOrdinal(int ordinal, int rungCount)
    {
        var next = ordinal + 1;
        if (next >= rungCount)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, $"already the top rung of {rungCount}");
        return next;
    }

    /// <summary>Clamped at the bottom (Chaff) rather than throwing — the shape a "N rungs below this
    /// rank" floor search needs: a low-rung request should resolve to Chaff, not fail.</summary>
    public static CreatureRank RungsBelow(CreatureRank rank, int rungs)
    {
        var target = (int)rank - rungs;
        return (CreatureRank)Math.Max(0, target);
    }

    /// <summary>Ordinal-safe "at least this rung" — the direct replacement for
    /// <c>rank &gt;= CreatureRank.X</c>. Named so a reader sees the THRESHOLD, not an inlined
    /// ordinal comparison that silently covers a different fraction of the ladder once it widens.
    /// A gate whose floor is the bottom rung is a pass-through by construction (spec Assumption 3).</summary>
    public static bool AtLeast(CreatureRank rank, CreatureRank threshold) => (int)rank >= (int)threshold;

    /// <summary>Ordinal-safe "at most this rung."</summary>
    public static bool AtMost(CreatureRank rank, CreatureRank threshold) => (int)rank <= (int)threshold;

    /// <summary>Every rung, weakest first — the declaring read of the ladder's own order.</summary>
    public static IReadOnlyList<CreatureRank> All { get; } =
        Enum.GetValues<CreatureRank>().OrderBy(r => (int)r).ToArray();
}
