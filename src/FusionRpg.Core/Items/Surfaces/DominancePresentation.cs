using FusionRpg.Core.Items.Display;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Items.Surfaces;

/// <summary>
/// One verdict, rendered. <b>A word and a shape, never a colour alone</b> (GG-27, and the same
/// redundancy rule I1 established for rarity, which forbids encoding "better" in hue).
/// </summary>
/// <param name="LabelKey">An i18n key, never English. Every human-readable leaf on this surface is a
/// key — ssot-presentation.md §3.6 — so a screenshot in another locale is still correct.</param>
/// <param name="Shape">The redundant channel a colour-blind or greyscale reader gets instead of hue.
/// One character, and it is deliberately not an emoji: a font that lacks it renders a box, and a box
/// is still a distinguishable shape.</param>
public readonly record struct VerdictBadge(string LabelKey, string Shape);

/// <summary>
/// A sidegrade, spelled out. GG-27 and ssot-presentation.md §4.2 both require it: a verdict word
/// alone tells the player it is a trade without telling them <b>which</b> trade.
/// </summary>
public readonly record struct SidegradeTrade(
    IReadOnlyList<ChannelDelta> YouGain,
    IReadOnlyList<ChannelDelta> YouGiveUp);

/// <summary>One column of the comparison table. The unit lives in the GROUP HEADER, never in the
/// column — that is SC4 expressed as a layout constraint.</summary>
public readonly record struct UnitClassGroup(UnitClass? Unit, IReadOnlyList<ChannelDelta> Deltas);

/// <summary>
/// The comparison screen's presentation rules — the half spec-item-surfaces.md says this module owns,
/// over the payload module 2's <see cref="ArmouryCompare"/> already computes.
///
/// <para>⛔ <b>No synthesized scalar, ever, and the copy that says so is permanent.</b>
/// <see cref="NoSingleScoreFootnoteKey"/> is a footnote, not a dismissible hint:
/// <i>"A player who dismisses it once will read its absence as a missing feature forever."</i> There
/// is deliberately no <c>dismissible</c> flag anywhere in this file — a property that does not exist
/// cannot be flipped by a later component.</para>
///
/// <para><b>When module 9's power read joins</b>, it lands as ONE ROW ABOVE the delta table and the
/// table stays. A single number cannot say <i>what</i> got better; that was I13's argument and it does
/// not stop being true when the number exists.</para>
/// </summary>
public static class DominancePresentation
{
    /// <summary>ssot-presentation.md §4.2's persistent footnote. A key, and there is no API to hide it.</summary>
    public const string NoSingleScoreFootnoteKey = "item.compare.no-single-score";

    /// <summary>The reason line an <c>incomparable</c> verdict must carry —
    /// <i>"an incomparable verdict with no explanation reads as a bug"</i>.</summary>
    public const string IncomparableReasonKey = "item.compare.incomparable-reason";

    /// <summary>
    /// The four verdicts, each a word AND a shape. Total over the enum by construction — the default
    /// arm throws rather than returning a blank badge, because a verdict that rendered as an empty
    /// string would be indistinguishable from "we did not compare these".
    /// </summary>
    public static VerdictBadge Badge(DominanceVerdict verdict) => verdict switch
    {
        DominanceVerdict.StrictlyBetter => new VerdictBadge("item.compare.strictly-better", "▲"),
        DominanceVerdict.StrictlyWorse => new VerdictBadge("item.compare.strictly-worse", "▼"),
        DominanceVerdict.Sidegrade => new VerdictBadge("item.compare.sidegrade", "◆"),
        DominanceVerdict.Incomparable => new VerdictBadge("item.compare.incomparable", "◇"),
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null),
    };

    /// <summary>
    /// The trade, split. Only meaningful for <see cref="DominanceVerdict.Sidegrade"/>, and it is
    /// computed from the same deltas the table renders rather than from a second read, so the two
    /// halves can never disagree with the rows above them.
    /// </summary>
    public static SidegradeTrade Trade(IReadOnlyList<ChannelDelta> deltas)
    {
        if (deltas is null) throw new ArgumentNullException(nameof(deltas));
        return new SidegradeTrade(
            deltas.Where(d => d.Delta > 0).ToList(),
            deltas.Where(d => d.Delta < 0).ToList());
    }

    /// <summary>
    /// SC4 as a layout invariant: deltas grouped by unit class, ordered by the unit class's own
    /// declaration order, then by channel id.
    ///
    /// <para><b>Two unit classes never share a numeric column.</b> <c>+9 hp</c> and
    /// <c>+5 accuracy</c> are not the same currency, and a table that stacks them in one column is
    /// making the claim that they are. A channel whose unit cannot be resolved groups under
    /// <c>null</c> — <b>its own group</b>, never folded into <c>GameUnits</c>, because guessing a unit
    /// is exactly the lie this rule exists to prevent.</para>
    /// </summary>
    public static IReadOnlyList<UnitClassGroup> GroupByUnitClass(
        IReadOnlyList<ChannelDelta> deltas, DerivedStatRegistry? registry = null)
    {
        if (deltas is null) throw new ArgumentNullException(nameof(deltas));
        var resolved = registry ?? DerivedStatRegistry.CreateDefault();

        return deltas
            .Select(d => (Delta: d, Unit: ChannelUnits.For(d.Channel, resolved)))
            .GroupBy(x => x.Unit)
            .OrderBy(g => g.Key is null ? int.MaxValue : (int)g.Key.Value)
            .Select(g => new UnitClassGroup(
                g.Key,
                g.OrderBy(x => x.Delta.Channel, StringComparer.Ordinal).Select(x => x.Delta).ToList()))
            .ToList();
    }

    /// <summary>
    /// GG-47 — anything choosable is comparable. The comparison is the DEFAULT presentation when a
    /// candidate is selected against an occupied role, not a tooltip afterthought and not an extra
    /// click. Expressed as a predicate so a component cannot decide otherwise per screen.
    /// </summary>
    public static bool ComparisonIsDefault(bool candidateSelected, bool roleHasIncumbent) =>
        candidateSelected && roleHasIncumbent;
}
