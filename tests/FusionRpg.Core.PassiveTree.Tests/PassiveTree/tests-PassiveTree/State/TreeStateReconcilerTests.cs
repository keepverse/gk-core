using FusionRpg.Core.PassiveTree.State;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.State;

/// <summary>Task C9 — `TreeStateReconciler` (spec-tree-state.md §4). Classifies live/retired/unknown
/// IN MEMORY after the load; never throws for an unknown id.</summary>
public class TreeStateReconcilerTests
{
    [Fact] // an_unknown_node_id_does_not_throw_on_actor_load
    public void An_unknown_node_id_does_not_throw_on_actor_load()
    {
        var owned = new Dictionary<string, long> { ["skill.ghost-off-t1-n0"] = 0 };

        var result = TreeStateReconciler.Classify(owned, _ => TreeNodeCatalogStatus.NeverKnown);

        var one = Assert.Single(result);
        Assert.Equal(TreeNodeStatus.Unknown, one.Status);
    }

    [Fact]
    public void An_unknown_node_id_does_not_throw_at_the_AptitudeAllocation_scale()
    {
        // The AptitudeAllocation.cs:39 defect this exists to not repeat: 1,560 ids per actor, all
        // unknown, must classify without a single throw.
        const int ownedCount = 1560;
        var owned = new Dictionary<string, long>();
        for (var i = 0; i < ownedCount; i++)
            owned[$"skill.ghost-off-t1-n{i}"] = 0;

        var result = TreeStateReconciler.Classify(owned, _ => TreeNodeCatalogStatus.NeverKnown);

        // Self-referential (population-pin SE3.3, 2026-09-19): this test constructed ownedCount ids
        // itself, so it proves none were lost, not a fact about shipped content.
        Assert.Equal(ownedCount, result.Count);
        Assert.All(result, r => Assert.Equal(TreeNodeStatus.Unknown, r.Status));
    }

    [Fact] // a_retired_node_loads_as_invalid_and_grants_nothing (classification half)
    public void A_retired_node_classifies_as_retired_not_unknown()
    {
        var owned = new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 5 };

        var result = TreeStateReconciler.Classify(owned, _ => TreeNodeCatalogStatus.Retired);

        var one = Assert.Single(result);
        Assert.Equal(TreeNodeStatus.Retired, one.Status);
        Assert.Equal(5, one.SoulLevel); // the row itself is preserved -- classification never drops data
    }

    [Fact]
    public void A_live_node_classifies_as_live()
    {
        var owned = new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 };
        var result = TreeStateReconciler.Classify(owned, _ => TreeNodeCatalogStatus.Live);
        Assert.Equal(TreeNodeStatus.Live, Assert.Single(result).Status);
    }

    [Fact] // "the three-way result is what the surface renders" -- one save with all three at once
    public void A_mixed_set_of_live_retired_and_unknown_nodes_all_classify_correctly_in_one_pass()
    {
        var owned = new Dictionary<string, long>
        {
            ["skill.might-off-t1-n0"] = 0,   // live
            ["skill.might-off-t2-n0"] = 3,   // retired
            ["skill.ghost-off-t1-n0"] = 0,   // unknown
        };
        var catalog = new Dictionary<string, TreeNodeCatalogStatus>
        {
            ["skill.might-off-t1-n0"] = TreeNodeCatalogStatus.Live,
            ["skill.might-off-t2-n0"] = TreeNodeCatalogStatus.Retired,
        };

        var result = TreeStateReconciler.Classify(owned,
            id => catalog.TryGetValue(id, out var s) ? s : TreeNodeCatalogStatus.NeverKnown);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, r => r.NodeId == "skill.might-off-t1-n0" && r.Status == TreeNodeStatus.Live);
        Assert.Contains(result, r => r.NodeId == "skill.might-off-t2-n0" && r.Status == TreeNodeStatus.Retired);
        Assert.Contains(result, r => r.NodeId == "skill.ghost-off-t1-n0" && r.Status == TreeNodeStatus.Unknown);
    }

    [Fact]
    public void Classification_calls_the_lookup_exactly_once_per_owned_node()
    {
        // "classification happens once per load" -- pinned as a call-count property, not just
        // asserted in prose.
        var owned = new Dictionary<string, long> { ["a"] = 0, ["b"] = 0, ["c"] = 0 };
        var calls = new List<string>();

        TreeStateReconciler.Classify(owned, id => { calls.Add(id); return TreeNodeCatalogStatus.Live; });

        Assert.Equal(3, calls.Count);
        Assert.Equal(owned.Keys.OrderBy(k => k), calls.OrderBy(k => k));
    }

    [Fact]
    public void An_empty_owned_set_classifies_to_an_empty_result_without_calling_the_lookup()
    {
        var called = false;
        var result = TreeStateReconciler.Classify(
            new Dictionary<string, long>(), _ => { called = true; return TreeNodeCatalogStatus.Live; });
        Assert.Empty(result);
        Assert.False(called);
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() =>
            TreeStateReconciler.Classify(null!, _ => TreeNodeCatalogStatus.Live));
        Assert.Throws<ArgumentNullException>(() =>
            TreeStateReconciler.Classify(new Dictionary<string, long>(), null!));
    }

    // ---- R3 (task C5, spec-tree-catalog.md §4): "costs nothing to hold" -- LiveOnly ----------

    [Fact] // a_retired_node_loads_as_invalid_and_grants_nothing (the "grants nothing" half)
    public void LiveOnly_drops_retired_and_unknown_rows_keeping_only_live_ones()
    {
        var owned = new Dictionary<string, long>
        {
            ["skill.might-off-t1-n0"] = 0,   // live
            ["skill.might-off-t2-n0"] = 3,   // retired
            ["skill.ghost-off-t1-n0"] = 7,   // unknown
        };
        var catalog = new Dictionary<string, TreeNodeCatalogStatus>
        {
            ["skill.might-off-t1-n0"] = TreeNodeCatalogStatus.Live,
            ["skill.might-off-t2-n0"] = TreeNodeCatalogStatus.Retired,
        };
        var classified = TreeStateReconciler.Classify(owned,
            id => catalog.TryGetValue(id, out var s) ? s : TreeNodeCatalogStatus.NeverKnown);

        var liveOnly = TreeStateReconciler.LiveOnly(classified);

        var one = Assert.Single(liveOnly);
        Assert.Equal("skill.might-off-t1-n0", one.Key);
        Assert.Equal(0, one.Value);
    }

    [Fact] // composing LiveOnly with TreeNodeSet.SelfSpent proves a retired node contributes ZERO
    public void A_retired_node_contributes_zero_to_the_selfSpent_projection_via_LiveOnly()
    {
        var owned = new Dictionary<string, long>
        {
            ["skill.might-off-t1-n0"] = 2,  // live, self-bought
            ["skill.might-off-t2-n0"] = 9,  // retired -- must cost nothing to hold
        };
        var classified = TreeStateReconciler.Classify(owned, id =>
            id == "skill.might-off-t1-n0" ? TreeNodeCatalogStatus.Live : TreeNodeCatalogStatus.Retired);

        var selfSpent = TreeNodeSet.SelfSpent(TreeStateReconciler.LiveOnly(classified));

        Assert.Equal(new TreeSelfSpent(1, 2), selfSpent["might"]); // 1 node, 2 soul levels -- the retired one contributes nothing
    }

    [Fact]
    public void LiveOnly_of_an_all_retired_set_is_empty()
    {
        var owned = new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 5 };
        var classified = TreeStateReconciler.Classify(owned, _ => TreeNodeCatalogStatus.Retired);

        Assert.Empty(TreeStateReconciler.LiveOnly(classified));
    }

    [Fact]
    public void LiveOnly_null_argument_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => TreeStateReconciler.LiveOnly(null!));
    }
}
