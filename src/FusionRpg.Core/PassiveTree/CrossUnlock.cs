namespace FusionRpg.Core.PassiveTree;

/// <summary>
/// D28's cross-unlock (spec-tree-resolve.md §4, task D4). `credit(i) = max{base(j) : j != i,
/// stanceGroup(j) == stanceGroup(i)}` — EXACTLY ONE LENDER, never a sum. `base(i)` is tree `i`'s own
/// aptitude allocation, the same quantity `req(t)` reads (`TierGate.Reached`'s own input).
///
/// <para><b>Why max, never sum (§4.1).</b> One mate is `O(1)`, and no k-way build can compound it —
/// the property `full` (a sum) lacks. `full` also compresses every build into a narrow band
/// (48.6-51.8% per the measured sweep): "a rule that gives everyone everything stops
/// discriminating."</para>
///
/// <para><b>`stanceGroup` is a catalog property, read never re-declared</b> — for the twelve primary
/// trees it is the shipped `Posture`, "READ… never stored" display/grouping vocabulary by design. A
/// tree the catalog gives no stance group gets `credit = 0` — the safe default, and the only one the
/// measurement covers.</para>
/// </summary>
public static class CrossUnlock
{
    /// <summary><paramref name="stanceGroupByTree"/> maps a tree id to its stance group, or omits/maps
    /// to `null` for a tree the catalog gives none — such a tree always credits 0 and lends to
    /// nobody (it has no group to match against).</summary>
    public static long Credit(
        string treeId,
        IReadOnlyDictionary<string, long> baseByTree,
        IReadOnlyDictionary<string, string?> stanceGroupByTree)
    {
        if (baseByTree is null) throw new ArgumentNullException(nameof(baseByTree));
        if (stanceGroupByTree is null) throw new ArgumentNullException(nameof(stanceGroupByTree));

        if (!stanceGroupByTree.TryGetValue(treeId, out var myGroup) || myGroup is null)
            return 0; // no stance group -> credit = 0 (the safe default, §4.1)

        long best = 0;
        foreach (var (otherId, otherBase) in baseByTree)
        {
            if (string.Equals(otherId, treeId, StringComparison.Ordinal)) continue;
            if (!stanceGroupByTree.TryGetValue(otherId, out var otherGroup)) continue;
            if (!string.Equals(otherGroup, myGroup, StringComparison.Ordinal)) continue;
            if (otherBase > best) best = otherBase; // MAX, never a running sum
        }
        return best;
    }

    /// <summary>`gate(i) = base(i) + credit(i)` — the quantity `TierGate.Reached` should be called
    /// with once a tree has a stance-mate, in place of the tree's own bare `base(i)`.</summary>
    public static long Gate(
        string treeId,
        IReadOnlyDictionary<string, long> baseByTree,
        IReadOnlyDictionary<string, string?> stanceGroupByTree)
    {
        if (baseByTree is null) throw new ArgumentNullException(nameof(baseByTree));

        var myBase = baseByTree.TryGetValue(treeId, out var b) ? b : 0;
        checked
        {
            return myBase + Credit(treeId, baseByTree, stanceGroupByTree);
        }
    }

    /// <summary>D5's projection need: WHICH tree lent the credit, not just its numeric amount —
    /// `tree-surface` §9.1 renders the lender without recomputing anything, so `TreeResolveReport`
    /// needs the id, not a second call into <see cref="Credit"/> that only returns the number.
    ///
    /// <para>Same rule as <see cref="Credit"/>, restated as an id: the largest posture-mate, never a
    /// sum, never a second lender. Returns `null` exactly when <see cref="Credit"/> would return `0`
    /// — no stance group, no mate, or every mate at `base = 0` — so the two can never disagree about
    /// WHETHER a mate exists.</para></summary>
    public static string? Lender(
        string treeId,
        IReadOnlyDictionary<string, long> baseByTree,
        IReadOnlyDictionary<string, string?> stanceGroupByTree)
    {
        if (baseByTree is null) throw new ArgumentNullException(nameof(baseByTree));
        if (stanceGroupByTree is null) throw new ArgumentNullException(nameof(stanceGroupByTree));

        if (!stanceGroupByTree.TryGetValue(treeId, out var myGroup) || myGroup is null)
            return null; // no stance group -> no lender, matching Credit's 0 (§4.1)

        string? lenderId = null;
        long best = 0;
        foreach (var (otherId, otherBase) in baseByTree)
        {
            if (string.Equals(otherId, treeId, StringComparison.Ordinal)) continue;
            if (!stanceGroupByTree.TryGetValue(otherId, out var otherGroup)) continue;
            if (!string.Equals(otherGroup, myGroup, StringComparison.Ordinal)) continue;
            if (otherBase > best) { best = otherBase; lenderId = otherId; } // MAX, never a running sum
        }
        return lenderId;
    }
}
