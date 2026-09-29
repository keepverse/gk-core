using FusionRpg.Core.PassiveTree.Catalog;

namespace FusionRpg.Core.PassiveTree.Resolve;

/// <summary>D5's report shape (spec-tree-resolve.md, task B6) — so I6 has a `gateState` to read.
/// `GateState` is READ FROM THE CATALOG (`TreeRecord.GateQuantity`'s producer state), never inferred
/// from a tier-0 zero: a tree with no aptitude allocated yet and a tree whose gate quantity has no
/// producer are both zero, and only the catalog's own recorded state tells them apart.</summary>
public enum TreeGateState
{
    /// <summary>The gate quantity has a real producer in `src/` — a tier-0 reading means no
    /// allocation yet, an ordinary starting state.</summary>
    Wired,

    /// <summary>The gate quantity has no producer yet (D37) — a tier-0 reading means the content is
    /// waiting, not that the actor has not started.</summary>
    Unproduced,
}

/// <summary>One node excluded by D14/D40's property-keyed rule (spec-tree-resolve.md §13, test 17).
/// `Form` and `IsInert` are both carried so `tree-surface` never has to re-derive "nullification means
/// inert" itself from the enum — `IsInert` IS that already-resolved reading (success criterion 8: no
/// recomputation). `WinnerNodeId` is the OTHER node whose tags fired this exclusion — never a node id
/// authored on the catalog itself (D40: "it keys on a property, never on a node id"), but a plain,
/// already-resolved value once <see cref="ExclusionResolver"/> has run.</summary>
public sealed record ExcludedNodeReport(
    string NodeId,
    ExclusionForm Form,
    string WinnerNodeId,
    bool IsInert);

/// <summary>
/// The projection `tree-surface` renders (spec-tree-resolve.md §3.3, §12 tests 17-18, success
/// criterion 8). Every field here is a plain, already-resolved value — no delegate, no lazy
/// computation the surface would need to invoke.
///
/// <para><c>LenderTreeId</c> — which tree's own aptitude allocation supplied this tree's cross-unlock
/// credit (D28/D4), or `null` when there is no mate (<see cref="CrossUnlock.Lender"/>).</para>
/// <para><c>HerfindahlMilli</c>/<c>FocusMilli</c> — `H` and `F` (D4/D5/D8/D39,
/// <see cref="Concentration"/>), per-mille, computed upstream from the actor's final allocation and
/// handed in here rather than recomputed.</para>
/// <para><c>InvalidNodeIds</c> — owned, non-retired nodes whose tier now sits ABOVE `TierReached`: a
/// gate that closed after the node was bought. D11/D12: this INVALIDATES the node rather than
/// repairing it, and it contributes zero exactly like an excluded node, just for a different reason
/// (§13, test 18).</para>
/// <para><c>ExcludedNodes</c> — owned, tier-valid nodes D14/D40 stops anyway, with the winner named
/// (§13, test 17). A node can be reported as invalid OR excluded, never both — an invalid node's gate
/// problem is reported first and its exclusion state (if any) is moot, since it already contributes
/// zero.</para>
/// </summary>
public sealed record TreeResolveReport(
    string TreeId,
    TreeGateState GateState,
    int TierReached,
    long AptitudePoints,
    IReadOnlyList<string> ContributingNodeIds,
    IReadOnlyList<string> InvalidNodeIds,
    string? LenderTreeId,
    long HerfindahlMilli,
    long FocusMilli,
    IReadOnlyList<ExcludedNodeReport> ExcludedNodes)
{
    /// <summary>Assembles the full projection for one tree from the quantities the resolver already
    /// computed elsewhere (gate state from the catalog, tier/points from <see cref="TierGate"/> and
    /// <see cref="CrossUnlock"/>, `H`/`F` from <see cref="Concentration"/>) plus the actor's ownership.
    /// This is the ONE place that decides which owned node lands in which bucket — contributing,
    /// invalid, or excluded — so a caller building `TreeAtomSource`'s live-atom set and a caller
    /// building this report can never see different answers (the same <see cref="ExclusionResolver"/>
    /// call `TreeAtomSource.BoundAtomsFor` itself makes).</summary>
    public static TreeResolveReport Build(
        LoadedTree tree,
        TreeGateState gateState,
        int tierReached,
        long aptitudePoints,
        IReadOnlySet<string> ownedNodeIds,
        string? lenderTreeId,
        long herfindahlMilli,
        long focusMilli)
    {
        var contributing = new List<string>();
        var invalid = new List<string>();
        var excluded = new List<ExcludedNodeReport>();

        foreach (var node in tree.Nodes)
        {
            if (!node.Enabled) continue;                          // retired: R2, not reported here
            if (!ownedNodeIds.Contains(node.NodeId)) continue;     // never bought: nothing to report

            if (node.Tier > tierReached)
            {
                // D11/D12: a gate that closed invalidates rather than repairing. The node contributes
                // zero (TreeAtomSource's own tier check) and is reported as invalid, never silently
                // dropped and never silently kept at its old value.
                invalid.Add(node.NodeId);
                continue;
            }

            var exclusion = ExclusionResolver.Resolve(tree, node, ownedNodeIds);
            if (exclusion is not null)
            {
                excluded.Add(exclusion);
                continue;
            }

            // A report is the projection of the live tree-derived read, not merely ownership.
            // Keep this predicate shared with TreeAtomSource: a binder-emitted stat.modify or
            // mechanism/status atom is still owned, but it contributes nothing until its owning
            // route exists and must not be reported as a derived contribution here.
            if (!TreeAtomSource.HasReadableAtom(node)) continue;

            contributing.Add(node.NodeId);
        }

        return new TreeResolveReport(tree.Tree.TreeId, gateState, tierReached, aptitudePoints,
            contributing, invalid, lenderTreeId, herfindahlMilli, focusMilli, excluded);
    }
}
