using System.Text.Json;
using FusionRpg.Core.PassiveTree.Catalog;

namespace FusionRpg.Core.PassiveTree.Resolve;

/// <summary>
/// D14/D40's property-keyed exclusion (spec-tree-catalog.md §2.2, §6; spec-tree-resolve.md §13, test
/// 17; spec-tree-language.md §5). `NodeRecord.ExcludeProps` names PROPERTY KEYS, never a node id — the
/// catalog's own load-path refuses "an `excludeProps` entry that parses as a node id rather than a
/// property key" — so this module's whole job is a membership check over data the catalog and the
/// actor's ownership already state: does the actor own another, live node whose own tags carry one of
/// THIS node's `excludeProps` keys? If so, this node is excluded and that other node is the winner.
/// That is a lookup, not conflict-resolution logic — "this module only stops the contribution"
/// (spec-tree-resolve.md's D40 row).
///
/// <para><b>Two ambiguities the specs leave open, resolved here with the simplest reading and stated
/// rather than guessed silently (task D5):</b></para>
/// <list type="number">
/// <item>Whether an exclusion pair may span two different trees. Every existing resolve-side
/// signature (<see cref="TierGate"/>, <see cref="Concentration"/>, <see cref="TreeAtomSource"/>)
/// operates on one <see cref="LoadedTree"/> at a time, and D5's own file scope is
/// <c>TreeResolveReport.cs</c> — not a new cross-tree actor-state parameter. This resolver matches
/// only within the SAME tree passed in.</item>
/// <item>The tie-break when more than one owned node's tags match. D14 targets ~2% of nodes as
/// exclusions, so a real corpus has at most one such pair per property; this resolver takes the first
/// match in the tree's own authored node order, which is deterministic for a fixed catalog.</item>
/// </list>
///
/// <para>Tags are read from <see cref="NodeRecord.TagsJson"/>'s top-level object KEYS (values
/// ignored) — the bare-key convention `EligibilityRule.RequireTags` already uses for atom tags
/// (spec-tree-language.md §5.2's "keys on a property, never a node id"). A null/blank `TagsJson`
/// carries no tags.</para>
/// </summary>
public static class ExclusionResolver
{
    /// <summary>Resolves ONE node's exclusion outcome against the rest of `tree`'s owned, live
    /// nodes. Returns `null` when the node is not excluded — no `ExclusionForm`, or no owned node
    /// currently carries a matching tag (whether it fires is a per-actor, per-resolve property of the
    /// build, never a bake-time fact — spec-tree-binder.md §7.3).</summary>
    public static ExcludedNodeReport? Resolve(LoadedTree tree, NodeRecord node, IReadOnlySet<string> ownedNodeIds)
    {
        if (node.ExclusionForm == ExclusionForm.None || node.ExcludeProps.Count == 0)
            return null;

        foreach (var other in tree.Nodes)
        {
            if (string.Equals(other.NodeId, node.NodeId, StringComparison.Ordinal)) continue; // never self-exclude
            if (!other.Enabled) continue;              // a retired node's tags no longer hold (R2)
            if (!ownedNodeIds.Contains(other.NodeId)) continue; // must actually be held, not merely authored

            var otherTags = TagsOf(other);
            var fires = false;
            foreach (var prop in node.ExcludeProps)
            {
                if (otherTags.Contains(prop)) { fires = true; break; }
            }
            if (!fires) continue;

            return new ExcludedNodeReport(node.NodeId, node.ExclusionForm, other.NodeId,
                IsInert: node.ExclusionForm == ExclusionForm.Nullification);
        }
        return null;
    }

    static readonly HashSet<string> EmptyTags = new(StringComparer.Ordinal);

    static HashSet<string> TagsOf(NodeRecord node)
    {
        if (string.IsNullOrWhiteSpace(node.TagsJson)) return EmptyTags;

        using var doc = JsonDocument.Parse(node.TagsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return EmptyTags;

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
            set.Add(prop.Name);
        return set;
    }
}
