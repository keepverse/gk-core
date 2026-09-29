using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Surfaces;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Items.Display;

/// <summary>
/// The <b>Compare</b> level of spec-item-card.md's three (Line → Card → Compare): two rendered cards
/// plus I13's comparison payload, joined into one <see cref="CompareModel"/>.
///
/// <para>⛔ <b>It computes almost nothing.</b> The deltas, the four-valued verdict and the roll
/// qualities are <c>ArmouryCompare.Compare</c>'s (module 13's, already shipped); the badge, the
/// sidegrade trade and the unit-class grouping are <c>DominancePresentation</c>'s (module 20's,
/// already shipped). The one thing this file owns is the LINE DIFF — which is exactly what the spec
/// means by "comparison diffs rendered lines, so the server must be able to call the same function the
/// tooltip calls". A second delta table here would be the two-implementations defect, and the reason
/// the level exists at all is to make sure there is only one.</para>
///
/// <para>⛔ <b>No synthesized scalar (SC9), and the copy that says why is not optional.</b>
/// <see cref="CompareModel.FootnoteKey"/> is always populated and there is no flag anywhere in this
/// file or in the model that could hide it.</para>
/// </summary>
public static class ItemCardCompare
{
    /// <summary>
    /// Compare an incumbent (<paramref name="left"/>) against a candidate (<paramref name="right"/>).
    ///
    /// <para>The atom pairs are the instances' OWN frozen rows — the same
    /// <c>(AtomRow, values_json)</c> pair the cards rendered from — so the delta table and the lines
    /// above it are reading one set of numbers, not two.</para>
    /// </summary>
    public static CompareModel Compare(
        DisplayModel left, DisplayModel right,
        IReadOnlyList<CompareAtom> leftAtoms, IReadOnlyList<CompareAtom> rightAtoms,
        DerivedStatRegistry? registry = null)
    {
        if (leftAtoms is null) throw new ArgumentNullException(nameof(leftAtoms));
        if (rightAtoms is null) throw new ArgumentNullException(nameof(rightAtoms));

        var resolved = registry ?? DerivedStatRegistry.CreateDefault();

        // ⛔ ONE call into module 13's payload. Never a second delta pass.
        // The registry goes in so the per-delta unit and the group header below resolve against the
        // same one -- two registries would be two answers to the unit question all over again.
        var payload = ArmouryCompare.Compare(leftAtoms, rightAtoms, resolved);
        var leftQuality = ArmouryCompare.Compare(rightAtoms, leftAtoms, resolved).MeanRollQualityMilli;

        return new CompareModel(
            Left: left,
            Right: right,
            DifferingLineIndexes: DifferingLines(left, right),
            Deltas: payload.Deltas,
            Dominance: payload.Dominance,
            Badge: DominancePresentation.Badge(payload.Dominance),
            // The trade is only meaningful for a sidegrade, but computing it from the SAME deltas the
            // table renders (rather than gating it here) is what keeps the two halves from disagreeing
            // with the rows above them -- DominancePresentation's own stated reason.
            Trade: DominancePresentation.Trade(payload.Deltas),
            UnitGroups: DominancePresentation.GroupByUnitClass(payload.Deltas, resolved),
            MeanRollQualityMilliLeft: leftQuality,
            MeanRollQualityMilliRight: payload.MeanRollQualityMilli,
            FootnoteKey: DominancePresentation.NoSingleScoreFootnoteKey,
            IncomparableReasonKey: payload.Dominance == DominanceVerdict.Incomparable
                ? DominancePresentation.IncomparableReasonKey
                : null);
    }

    /// <summary>
    /// Which flattened line positions differ. Two lines are the same when their whole rendered
    /// identity is — key, unit, source kind, bar, roll quality and every arg — which is exactly
    /// <see cref="DisplayModel.Fingerprint"/>'s own notion of identity, read per line so the diff and
    /// the determinism claim cannot drift apart.
    ///
    /// <para>Positions past the shorter card's end count as differing: a line the candidate has and the
    /// incumbent does not is the single most important thing on a comparison screen, and reporting the
    /// shorter length would hide it.</para>
    /// </summary>
    public static IReadOnlyList<int> DifferingLines(DisplayModel left, DisplayModel right)
    {
        var a = left.Lines.ToList();
        var b = right.Lines.ToList();
        var differing = new List<int>();

        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            if (i >= a.Count || i >= b.Count) { differing.Add(i); continue; }
            if (!SameLine(a[i], b[i])) differing.Add(i);
        }

        return differing;
    }

    static bool SameLine(DisplayLine a, DisplayLine b)
    {
        if (!string.Equals(a.Key, b.Key, StringComparison.Ordinal)) return false;
        if (a.Unit != b.Unit || a.SourceKind != b.SourceKind) return false;
        if (a.RollBar != b.RollBar || a.RollQualityPerMille != b.RollQualityPerMille) return false;
        if (!string.Equals(a.ContextRead, b.ContextRead, StringComparison.Ordinal)) return false;
        if (a.Args.Count != b.Args.Count) return false;

        foreach (var (k, v) in a.Args)
            if (!b.Args.TryGetValue(k, out var other) || !string.Equals(v, other, StringComparison.Ordinal))
                return false;

        return true;
    }

    /// <summary>
    /// Every atom on one rendered instance, paired with its frozen values — the input
    /// <see cref="Compare"/> wants, built from the same instance the card rendered.
    /// </summary>
    public static IReadOnlyList<CompareAtom> AtomsOf(InstanceRow instance, Func<string, AtomRow?> lookupAtom)
    {
        if (instance is null) throw new ArgumentNullException(nameof(instance));
        if (lookupAtom is null) throw new ArgumentNullException(nameof(lookupAtom));

        var atoms = new List<CompareAtom>(instance.Atoms.Count);
        foreach (var row in instance.Atoms.OrderBy(a => a.Seq))
        {
            var atom = lookupAtom(row.AtomId);
            if (atom is null) continue;   // an unresolvable atom is the card renderer's rejection, not a delta
            atoms.Add(new CompareAtom(atom, row.ValuesJson));
        }
        return atoms;
    }
}
