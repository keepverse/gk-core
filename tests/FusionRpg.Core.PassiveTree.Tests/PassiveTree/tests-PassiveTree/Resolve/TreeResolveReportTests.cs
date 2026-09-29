using System.Linq;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.PassiveTree.Resolve;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.Resolve;

/// <summary>Task B6 acceptance (d) (spec-tree-resolve.md §3.3): `TreeResolveReport` exists and carries
/// `gateState` read from the catalog, never inferred from a zero. Task D5 extends the shape below with
/// the full projection — lender, `H`, `F`, excluded nodes and which-kind-of-invalid — so `tree-surface`
/// can render it without recomputing anything (§12 tests 17-18, success criterion 8).</summary>
public class TreeResolveReportTests
{
    [Fact]
    public void A_tier_zero_wired_tree_and_a_tier_zero_unproduced_tree_are_distinguishable()
    {
        // Same TierReached (0), same AptitudePoints (0) -- the ONLY difference is the catalog-read
        // GateState. If GateState were ever inferred from the zero instead of read from the catalog,
        // these two reports would collapse to the same thing; they must not.
        var wired = new TreeResolveReport("might", TreeGateState.Wired, TierReached: 0,
            AptitudePoints: 0, ContributingNodeIds: Array.Empty<string>(), InvalidNodeIds: Array.Empty<string>(),
            LenderTreeId: null, HerfindahlMilli: 0, FocusMilli: 1000, ExcludedNodes: Array.Empty<ExcludedNodeReport>());
        var unproduced = new TreeResolveReport("element_mastery", TreeGateState.Unproduced, TierReached: 0,
            AptitudePoints: 0, ContributingNodeIds: Array.Empty<string>(), InvalidNodeIds: Array.Empty<string>(),
            LenderTreeId: null, HerfindahlMilli: 0, FocusMilli: 1000, ExcludedNodes: Array.Empty<ExcludedNodeReport>());

        Assert.NotEqual(wired.GateState, unproduced.GateState);
        Assert.Equal(wired.TierReached, unproduced.TierReached);
        Assert.Equal(wired.AptitudePoints, unproduced.AptitudePoints);
    }

    [Fact]
    public void GateState_has_exactly_the_two_documented_values()
    {
        var values = Enum.GetValues<TreeGateState>();
        Assert.Equal(new[] { TreeGateState.Wired, TreeGateState.Unproduced }, values);
    }

    [Fact]
    public void ContributingNodeIds_names_the_nodes_a_report_can_point_at_them_directly()
    {
        var report = new TreeResolveReport("might", TreeGateState.Wired, TierReached: 3,
            AptitudePoints: 45, ContributingNodeIds: new[] { "skill.might-off-t1-n0", "skill.might-off-t2-n0" },
            InvalidNodeIds: Array.Empty<string>(), LenderTreeId: null, HerfindahlMilli: 0, FocusMilli: 1000,
            ExcludedNodes: Array.Empty<ExcludedNodeReport>());

        Assert.Equal(2, report.ContributingNodeIds.Count);
    }

    // ---- D5: the full projection ---------------------------------------------------------------

    static NodeRecord Node(string nodeId, int tier, string? tagsJson = null,
        ExclusionForm form = ExclusionForm.None, string[]? excludeProps = null, bool enabled = true) =>
        new(nodeId, "might", TreeBranch.Off, tier, nodeId, Array.Empty<string>(), NodeClass.Magnitude,
            new[] { "affix.a" }, 45,
            new[]
            {
                new NodeAtom("stat.derived", AttachPoint.Stat, DerivedStatChannels.CombatPowerOmni,
                    NodeAtomOp.Flat, null, null, 3038, ScaleAxis.PTheta, UnitClass.GameUnits),
            },
            excludeProps ?? Array.Empty<string>(), form, tagsJson, enabled, null);

    static LoadedTree Tree(params NodeRecord[] nodes) =>
        new(new TreeRecord("might", TreeCategory.Primary, "aptitude.Might@Commander", "broad-and-flat",
                10, 2, new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 }, 1, true),
            nodes);

    [Fact] // spec-tree-resolve.md §12 test 17, D14/D40: reroute/precedence contribute zero, winner named
    public void An_excluded_node_contributes_zero_and_is_reported()
    {
        var loser = Node("skill.might-off-t1-n0", tier: 1, form: ExclusionForm.Precedence,
            excludeProps: new[] { "posture" });
        var winner = Node("skill.might-off-t2-n0", tier: 2, tagsJson: "{\"posture\":true}");
        var tree = Tree(loser, winner);
        var owned = new HashSet<string> { loser.NodeId, winner.NodeId };

        var report = TreeResolveReport.Build(tree, TreeGateState.Wired, tierReached: 10, aptitudePoints: 45,
            owned, lenderTreeId: null, herfindahlMilli: 0, focusMilli: 1000);

        Assert.DoesNotContain(loser.NodeId, report.ContributingNodeIds);
        Assert.Contains(winner.NodeId, report.ContributingNodeIds);
        var excluded = Assert.Single(report.ExcludedNodes);
        Assert.Equal(loser.NodeId, excluded.NodeId);
        Assert.Equal(winner.NodeId, excluded.WinnerNodeId);
        Assert.Equal(ExclusionForm.Precedence, excluded.Form);
        Assert.False(excluded.IsInert, "precedence is excluded-and-zero, never inert -- that word is reserved for nullification");
    }

    [Fact] // D40: a NULLIFIED node reports inert specifically, never merely "excluded" or "un-unlocked"
    public void A_nullified_node_reports_inert_not_merely_excluded()
    {
        var loser = Node("skill.might-off-t1-n0", tier: 1, form: ExclusionForm.Nullification,
            excludeProps: new[] { "posture" });
        var winner = Node("skill.might-off-t2-n0", tier: 2, tagsJson: "{\"posture\":true}");
        var tree = Tree(loser, winner);
        var owned = new HashSet<string> { loser.NodeId, winner.NodeId };

        var report = TreeResolveReport.Build(tree, TreeGateState.Wired, tierReached: 10, aptitudePoints: 45,
            owned, lenderTreeId: null, herfindahlMilli: 0, focusMilli: 1000);

        var excluded = Assert.Single(report.ExcludedNodes);
        Assert.Equal(ExclusionForm.Nullification, excluded.Form);
        Assert.True(excluded.IsInert);
        Assert.Equal(winner.NodeId, excluded.WinnerNodeId);
    }

    [Fact] // D14/§13: an exclusion NEVER refunds -- the node stays "owned", just absent from Contributing
    public void An_excluded_node_is_never_refunded_it_simply_stops_contributing()
    {
        var loser = Node("skill.might-off-t1-n0", tier: 1, form: ExclusionForm.Reroute,
            excludeProps: new[] { "posture" });
        var winner = Node("skill.might-off-t2-n0", tier: 2, tagsJson: "{\"posture\":true}");
        var tree = Tree(loser, winner);
        var owned = new HashSet<string> { loser.NodeId, winner.NodeId };

        var report = TreeResolveReport.Build(tree, TreeGateState.Wired, tierReached: 10, aptitudePoints: 45,
            owned, lenderTreeId: null, herfindahlMilli: 0, focusMilli: 1000);

        // "Refunded" would mean the node disappears from every list; it does not -- it is named on
        // ExcludedNodes, which is the whole point of reporting rather than silently dropping it.
        Assert.Contains(report.ExcludedNodes, e => e.NodeId == loser.NodeId);
    }

    [Fact] // spec-tree-resolve.md §12 test 18, D11/D12: a gate that closed invalidates, not repairs
    public void A_gate_that_closed_invalidates_rather_than_repairing()
    {
        var node = Node("skill.might-off-t5-n0", tier: 5);
        var tree = Tree(node);
        var owned = new HashSet<string> { node.NodeId };

        // Bought while tier 5 was open; the actor's aptitude allocation then dropped (item swap) and
        // the gate only reaches tier 4 now.
        var report = TreeResolveReport.Build(tree, TreeGateState.Wired, tierReached: 4, aptitudePoints: 20,
            owned, lenderTreeId: null, herfindahlMilli: 0, focusMilli: 1000);

        Assert.DoesNotContain(node.NodeId, report.ContributingNodeIds);
        Assert.Contains(node.NodeId, report.InvalidNodeIds);
        Assert.Empty(report.ExcludedNodes); // it is invalid, not excluded -- a different reason for the same zero
    }

    [Fact] // success criterion 8: every field is plain data once built -- no delegate, no lazy read
    public void The_built_report_carries_lender_H_and_F_as_plain_already_resolved_values()
    {
        var node = Node("skill.might-off-t3-n0", tier: 3);
        var tree = Tree(node);
        var owned = new HashSet<string> { node.NodeId };

        var report = TreeResolveReport.Build(tree, TreeGateState.Wired, tierReached: 10, aptitudePoints: 45,
            owned, lenderTreeId: "fortitude", herfindahlMilli: 1000, focusMilli: 1200);

        Assert.Equal("fortitude", report.LenderTreeId);
        Assert.Equal(1000, report.HerfindahlMilli);
        Assert.Equal(1200, report.FocusMilli);
        Assert.Contains(node.NodeId, report.ContributingNodeIds);
    }

    [Fact] // no mate -> LenderTreeId is null, never a placeholder string
    public void No_mate_reports_a_null_lender_never_a_placeholder()
    {
        var node = Node("skill.might-off-t3-n0", tier: 3);
        var tree = Tree(node);
        var owned = new HashSet<string> { node.NodeId };

        var report = TreeResolveReport.Build(tree, TreeGateState.Wired, tierReached: 10, aptitudePoints: 45,
            owned, lenderTreeId: null, herfindahlMilli: 0, focusMilli: 1000);

        Assert.Null(report.LenderTreeId);
    }

    [Fact] // a retired winner's tag no longer holds -- the exclusion is a property of the CURRENT build
    public void A_retired_winner_stops_the_exclusion_from_firing()
    {
        var loser = Node("skill.might-off-t1-n0", tier: 1, form: ExclusionForm.Nullification,
            excludeProps: new[] { "posture" });
        var retiredWinner = Node("skill.might-off-t2-n0", tier: 2, tagsJson: "{\"posture\":true}", enabled: false);
        var tree = Tree(loser, retiredWinner);
        var owned = new HashSet<string> { loser.NodeId, retiredWinner.NodeId };

        var report = TreeResolveReport.Build(tree, TreeGateState.Wired, tierReached: 10, aptitudePoints: 45,
            owned, lenderTreeId: null, herfindahlMilli: 0, focusMilli: 1000);

        Assert.Empty(report.ExcludedNodes);
        Assert.Contains(loser.NodeId, report.ContributingNodeIds);
    }
}
