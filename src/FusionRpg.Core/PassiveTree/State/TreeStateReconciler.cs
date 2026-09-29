namespace FusionRpg.Core.PassiveTree.State;

/// <summary>What the catalog currently says about ONE node id — the input `TreeStateReconciler`
/// classifies from, never re-derives (task C9).</summary>
public enum TreeNodeCatalogStatus
{
    /// <summary>The catalog has this id, enabled right now.</summary>
    Live,

    /// <summary>The catalog HAS had this id (it is in the historical known-id set, C4's
    /// `rpg_tree_catalog_known_node_id`), but it is not currently enabled/present in the active
    /// corpus.</summary>
    Retired,

    /// <summary>No catalog revision has ever had this id.</summary>
    NeverKnown,
}

/// <summary>The three-way result the surface renders (spec-tree-state.md §4, task C9) — one row per
/// owned node, never a throw.</summary>
public enum TreeNodeStatus { Live, Retired, Unknown }

public readonly record struct ClassifiedTreeNode(string NodeId, long SoulLevel, TreeNodeStatus Status);

/// <summary>
/// Classifies every stored row live/retired/unknown IN MEMORY, after the load (spec-tree-state.md §4)
/// — so `LoadTreeState` keeps returning rows and never throws on an unknown id, the way
/// `AptitudeAllocation.cs:39` throws per-row inside a reader loop today (unloadable at 1,560 node ids
/// per actor). Classification happens exactly ONCE per load, over the already-loaded dictionary — pure,
/// no I/O of its own; the catalog lookup is a caller-supplied delegate so this stays testable without a
/// database (the same `boundFor`-delegate shape `AtomDerivedSubsystem` and `AptitudeSubsystem` already
/// use for the identical reason: this module classifies, it does not own where the catalog lives).
/// </summary>
public static class TreeStateReconciler
{
    /// <summary>Never throws for any node id, regardless of what <paramref name="catalogLookup"/>
    /// reports — an unknown id classifies as <see cref="TreeNodeStatus.Unknown"/>, not an
    /// exception.</summary>
    public static IReadOnlyList<ClassifiedTreeNode> Classify(
        IReadOnlyDictionary<string, long> ownedNodeIdToSoulLevel,
        Func<string, TreeNodeCatalogStatus> catalogLookup)
    {
        if (ownedNodeIdToSoulLevel is null) throw new ArgumentNullException(nameof(ownedNodeIdToSoulLevel));
        if (catalogLookup is null) throw new ArgumentNullException(nameof(catalogLookup));

        var result = new List<ClassifiedTreeNode>(ownedNodeIdToSoulLevel.Count);
        foreach (var (nodeId, soulLevel) in ownedNodeIdToSoulLevel)
        {
            var status = catalogLookup(nodeId) switch
            {
                TreeNodeCatalogStatus.Live => TreeNodeStatus.Live,
                TreeNodeCatalogStatus.Retired => TreeNodeStatus.Retired,
                TreeNodeCatalogStatus.NeverKnown => TreeNodeStatus.Unknown,
                var other => throw new ArgumentOutOfRangeException(nameof(catalogLookup), other, "unknown TreeNodeCatalogStatus"),
            };
            result.Add(new ClassifiedTreeNode(nodeId, soulLevel, status));
        }
        return result;
    }

    /// <summary>
    /// R3 (spec-tree-catalog.md §4): an allocation naming a retired node "grants nothing" and "costs
    /// nothing to hold." Neither half is silent repair — the row itself is untouched, this only
    /// changes what a RESOLVE-TIME reader sees. Projects a classified list back down to the
    /// node-id-to-soul-level shape <c>TreeNodeSet.SelfSpent</c> (task C7) already reads, keeping only
    /// <see cref="TreeNodeStatus.Live"/> rows — <c>Retired</c> and <c>Unknown</c> rows are dropped, so
    /// a caller that resolves budget/spend through <c>TreeNodeSet.SelfSpent(TreeStateReconciler.
    /// LiveOnly(classified))</c> instead of the raw owned dictionary gets R3's "contributes zero" for
    /// free. <c>SelfSpent</c> itself is untouched and keeps applying no filter of its own — its own
    /// stated contract (spec-tree-state.md §2.4's rule 4 note) — the filtering lives here, in the
    /// module that already owns classification, not there.
    /// </summary>
    public static IReadOnlyDictionary<string, long> LiveOnly(IReadOnlyList<ClassifiedTreeNode> classified)
    {
        if (classified is null) throw new ArgumentNullException(nameof(classified));

        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var c in classified)
            if (c.Status == TreeNodeStatus.Live)
                result[c.NodeId] = c.SoulLevel;
        return result;
    }
}
