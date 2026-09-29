using FusionRpg.Core.PassiveTree.State;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.State;

/// <summary>Task C7 — the `selfSpent` projection (spec-tree-state.md §2.4; read by
/// spec-tree-resolve.md §5.2). Named tests match the spec's own §Testing table exactly.</summary>
public class TreeNodeSetTests
{
    [Fact] // self_spent_projection_is_the_final_allocation_not_the_purchase_order
    public void Self_spent_projection_is_the_final_allocation_not_the_purchase_order()
    {
        // Two "different purchase orders" that land on the SAME final owned set must project to the
        // SAME (n_i, s_i) vector -- the projection has no parameter through which an order could
        // even enter (it reads a dictionary, not a sequence), so this also proves order-independence
        // by construction, not merely by coincidence of these two fixtures.
        var orderA = new Dictionary<string, long>
        {
            ["skill.might-off-t1-n0"] = 0,
            ["skill.might-off-t2-n0"] = 3,
            ["skill.might-def-t1-n0"] = 1,
        };
        var orderB = new Dictionary<string, long>
        {
            ["skill.might-def-t1-n0"] = 1,
            ["skill.might-off-t2-n0"] = 3,
            ["skill.might-off-t1-n0"] = 0,
        };

        var a = TreeNodeSet.SelfSpent(orderA);
        var b = TreeNodeSet.SelfSpent(orderB);

        Assert.Equal(a["might"], b["might"]);
        Assert.Equal(new TreeSelfSpent(3, 4), a["might"]);
    }

    [Fact] // a_tree_with_no_self_bought_node_is_absent_from_the_vector
    public void A_tree_with_no_self_bought_node_is_absent_from_the_vector()
    {
        var owned = new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 };
        var result = TreeNodeSet.SelfSpent(owned);

        Assert.True(result.ContainsKey("might"));
        Assert.False(result.ContainsKey("fortitude")); // never present at zero
        Assert.Empty(TreeNodeSet.SelfSpent(new Dictionary<string, long>()));
    }

    [Fact] // exclusion_of_granted_unlocks_from_self_spent_is_a_stated_rule
    public void Exclusion_of_granted_unlocks_from_self_spent_is_a_stated_rule()
    {
        // spec-tree-state.md §2.4's own note: no source other than the player's own spend can add a
        // tree node today, so RpgStore's owned-node dictionary IS already the self-spent set by
        // construction -- rule 4 is a rule with nothing yet to exclude. This test pins that fact: the
        // projection's signature accepts exactly the store's owned-node shape and applies no further
        // filter, which is the correct behavior ONLY because no granted-unlock source exists. The day
        // one ships, its own module owes the provenance flag this test would then need to change to
        // exercise -- this test is what makes that change visible as a moved golden, not a silent gap.
        var owned = new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 };
        var result = TreeNodeSet.SelfSpent(owned);
        Assert.Equal(1, result["might"].NodeCount); // the one node counts -- nothing is excluded today
    }

    [Fact] // rule 2 -- a node counts once, at 1, never weighted by soul level or anything else
    public void A_node_counts_once_never_weighted_by_what_it_cost()
    {
        var owned = new Dictionary<string, long>
        {
            ["skill.might-off-t1-n0"] = 0,
            ["skill.might-off-t2-n0"] = 500, // a very expensive soul-levelled node
        };
        var result = TreeNodeSet.SelfSpent(owned);
        Assert.Equal(2, result["might"].NodeCount); // still exactly 2 -- not weighted by the 500
        Assert.Equal(500, result["might"].SoulLevels); // soul levels DO sum -- a different axis entirely
    }

    [Fact]
    public void Multiple_trees_project_independently()
    {
        var owned = new Dictionary<string, long>
        {
            ["skill.might-off-t1-n0"] = 0,
            ["skill.might-off-t2-n0"] = 0,
            ["skill.fortitude-def-t1-n0"] = 2,
        };
        var result = TreeNodeSet.SelfSpent(owned);
        Assert.Equal(new TreeSelfSpent(2, 0), result["might"]);
        Assert.Equal(new TreeSelfSpent(1, 2), result["fortitude"]);
    }

    [Fact]
    public void A_negative_soul_level_is_refused()
    {
        var owned = new Dictionary<string, long> { ["skill.might-off-t1-n0"] = -1 };
        Assert.Throws<ArgumentException>(() => TreeNodeSet.SelfSpent(owned));
    }

    [Fact]
    public void A_node_id_missing_the_skill_prefix_is_refused()
    {
        var owned = new Dictionary<string, long> { ["might-off-t1-n0"] = 0 };
        Assert.Throws<ArgumentException>(() => TreeNodeSet.SelfSpent(owned));
    }

    [Fact]
    public void Null_input_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => TreeNodeSet.SelfSpent(null!));
    }
}
