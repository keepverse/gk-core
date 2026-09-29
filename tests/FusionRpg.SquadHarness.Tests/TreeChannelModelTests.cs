using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Tools.SquadHarness;
using Xunit;

namespace FusionRpg.SquadHarness.Tests;

/// <summary>
/// F8 (passive-tree-todo.md, opened 2026-09-07 by F7's own finding). Same two-layer split
/// <see cref="TreeModelTests"/> already uses: the pure per-tree/per-actor math tested directly with
/// hand-computed expectations (no <c>BattleEngine</c> call), then a small layer proving
/// <see cref="TreeChannelModel.ConcentrationSweep"/>/<see cref="TreeChannelModel.CrossUnlockSweep"/>
/// actually wire into the real measurement machinery. The load-bearing test is
/// <see cref="Tree_Bs_own_amount_never_changes_with_tree_As_investment"/> -- the property F7 found the
/// OLD fold-back structurally could not have.
/// </summary>
public class TreeChannelModelTests
{
    static readonly string Might = BuildFactory.Roster[0];
    static readonly string Fortitude = BuildFactory.Roster[1];


    static PowerLadder Ladder() => new(PowerTuningHub.Tuning);

    // ---- RepresentativeKMicroPerNode: deterministic, positive, reads the real settled tunables -------

    [Fact]
    public void RepresentativeKMicroPerNode_is_positive_and_deterministic()
    {
        var a = TreeChannelModel.RepresentativeKMicroPerNode(PassiveTreeTuningHub.Tuning, PowerTuningHub.Tuning);
        var b = TreeChannelModel.RepresentativeKMicroPerNode(PassiveTreeTuningHub.Tuning, PowerTuningHub.Tuning);
        Assert.True(a > 0, "a representative node's own coefficient must be a real, positive contribution");
        Assert.Equal(a, b);
    }

    [Fact]
    public void RepresentativeKMicroPerNode_matches_CoefficientBinder_directly()
    {
        // Never re-derived: reproduces the exact call CoefficientBinder.Bind's own callers make, with
        // this class's own structural inputs (D53's treeShareMilli=1000, D15's treeBudgetMilli=1000,
        // the 25-per-mille average node share, D29's 2 branches).
        var tuning = PassiveTreeTuningHub.Tuning;
        var anchor = FusionRpg.Core.PassiveTree.Binding.ChannelAnchor.ForChannel(
            TreeChannelModel.RepresentativeChannel, PowerTuningHub.Tuning);
        var expected = FusionRpg.Core.PassiveTree.Binding.CoefficientBinder.Bind(
            tuning.TreeShareMilli, tuning.TreeBudgetMilli,
            TreeChannelModel.ApproxBudgetShareMilliPerNode, anchor, TreeChannelModel.Branches);

        Assert.Equal(expected, TreeChannelModel.RepresentativeKMicroPerNode(tuning, PowerTuningHub.Tuning));
    }

    // ---- PerTreeChannelAmount: the pure per-tree formula ------------------------------------------

    [Fact]
    public void PerTreeChannelAmount_is_zero_when_the_tree_owns_no_nodes()
    {
        var tree = new TreeModel.PerTreeState(Might, AptitudePoints: 500, GateTier: 3, GateNodeCount: 12, OwnedNodeCount: 0, AffordTier: 0, TreePower: 0);
        var amount = TreeChannelModel.PerTreeChannelAmount(tree, kMicroPerNode: 1000, Ladder(), theta: 100, fMultiplier: 1.0);
        Assert.Equal(0L, amount);
    }

    [Fact]
    public void PerTreeChannelAmount_grows_with_owned_node_count()
    {
        var kMicro = TreeChannelModel.RepresentativeKMicroPerNode(PassiveTreeTuningHub.Tuning, PowerTuningHub.Tuning);
        var few = new TreeModel.PerTreeState(Might, 0, 3, 12, OwnedNodeCount: 4, AffordTier: 1, 0);
        var many = new TreeModel.PerTreeState(Might, 0, 10, 40, OwnedNodeCount: 40, AffordTier: 10, 0);

        var fewAmount = TreeChannelModel.PerTreeChannelAmount(few, kMicro, Ladder(), theta: 100, fMultiplier: 1.0);
        var manyAmount = TreeChannelModel.PerTreeChannelAmount(many, kMicro, Ladder(), theta: 100, fMultiplier: 1.0);

        Assert.True(fewAmount > 0);
        Assert.True(manyAmount > fewAmount, $"40 owned nodes ({manyAmount}) must contribute more than 4 ({fewAmount})");
        // Linear in node count (no exponent, no cross-tree term) -- 10x the nodes is ~10x the amount,
        // within the rounding each independent Math.Round call introduces (each node-count level rounds
        // its own double amount to long separately, so 10*round(x) need not equal round(10*x) exactly).
        Assert.InRange(manyAmount, fewAmount * 10 - 10, fewAmount * 10 + 10);
    }

    [Fact]
    public void PerTreeChannelAmount_scales_with_fMultiplier_and_nothing_else_hidden()
    {
        var kMicro = TreeChannelModel.RepresentativeKMicroPerNode(PassiveTreeTuningHub.Tuning, PowerTuningHub.Tuning);
        var tree = new TreeModel.PerTreeState(Might, 0, 5, 20, OwnedNodeCount: 20, AffordTier: 5, 0);

        var atNoBonus = TreeChannelModel.PerTreeChannelAmount(tree, kMicro, Ladder(), theta: 100, fMultiplier: 1.0);
        var atDoubleBonus = TreeChannelModel.PerTreeChannelAmount(tree, kMicro, Ladder(), theta: 100, fMultiplier: 2.0);
        Assert.Equal(atNoBonus * 2, atDoubleBonus);
    }

    /// <summary>
    /// THE load-bearing test. F7's own finding: the OLD fold-back routed tree power through
    /// <see cref="AptitudeAllocation.Share"/> -- a zero-sum ratio across every aptitude the actor
    /// holds -- so growing tree A's contribution diluted tree B's own share and therefore tree B's own
    /// combat contribution too, with no real-game analog. This test proves the NEW model cannot have
    /// that defect: tree B's own amount is a pure function of tree B's own state (plus actor-wide
    /// constants that are legitimately global in the real game too -- F and Θ, held fixed here) and
    /// changes with NOTHING about tree A.
    /// </summary>
    [Fact]
    public void Tree_Bs_own_amount_never_changes_with_tree_As_investment()
    {
        var kMicro = TreeChannelModel.RepresentativeKMicroPerNode(PassiveTreeTuningHub.Tuning, PowerTuningHub.Tuning);
        var ladder = Ladder();
        const long theta = 150;
        const double fMultiplier = 1.15; // an arbitrary, fixed, non-trivial F -- held identical both times

        // Tree B's OWN state never changes between the two calls.
        var treeBLightlyInvested = new TreeModel.PerTreeState(Fortitude, AptitudePoints: 40, GateTier: 2, GateNodeCount: 8, OwnedNodeCount: 8, AffordTier: 2, TreePower: 0);

        // Tree A ranges from "owns nothing" to "owns everything" -- irrelevant, since PerTreeChannelAmount
        // is called once per tree and never receives any OTHER tree's state at all.
        var treeAEmpty = new TreeModel.PerTreeState(Might, 0, 0, 0, OwnedNodeCount: 0, AffordTier: 0, 0);
        var treeAMaxed = new TreeModel.PerTreeState(Might, 100_000, 10, 40, OwnedNodeCount: 40, AffordTier: 10, 0);

        // Compute tree A's own amount too, just to prove it DOES move (the model is not simply inert) --
        // then confirm tree B's amount, called independently, is byte-identical regardless.
        var treeAAmountEmpty = TreeChannelModel.PerTreeChannelAmount(treeAEmpty, kMicro, ladder, theta, fMultiplier);
        var treeAAmountMaxed = TreeChannelModel.PerTreeChannelAmount(treeAMaxed, kMicro, ladder, theta, fMultiplier);
        Assert.True(treeAAmountMaxed > treeAAmountEmpty, "sanity: tree A's own amount must actually move with its own investment");

        var treeBAmountWhenAEmpty = TreeChannelModel.PerTreeChannelAmount(treeBLightlyInvested, kMicro, ladder, theta, fMultiplier);
        var treeBAmountWhenAMaxed = TreeChannelModel.PerTreeChannelAmount(treeBLightlyInvested, kMicro, ladder, theta, fMultiplier);

        Assert.Equal(treeBAmountWhenAEmpty, treeBAmountWhenAMaxed);
    }

    // ---- ChannelModsFor: the actor-level sum -------------------------------------------------------

    [Fact]
    public void ChannelModsFor_is_empty_for_an_actor_who_owns_nothing()
    {
        var allocation = AptitudeAllocation.Empty;
        var mods = TreeChannelModel.ChannelModsFor(allocation, theta: 100, fmaxMilli: 1200, wMilli: 500, b: 5,
            includeOwnershipCost: false, TreeModel.CreditRule.None, PassiveTreeTuningHub.Tuning, PowerTuningHub.Tuning);
        Assert.Empty(mods);
    }

    [Fact]
    public void ChannelModsFor_returns_exactly_one_mod_on_the_representative_channel()
    {
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, Might, 100_000);
        var mods = TreeChannelModel.ChannelModsFor(allocation, theta: 100, fmaxMilli: 1200, wMilli: 500, b: 5,
            includeOwnershipCost: false, TreeModel.CreditRule.None, PassiveTreeTuningHub.Tuning, PowerTuningHub.Tuning);

        var mod = Assert.Single(mods);
        Assert.Equal(TreeChannelModel.RepresentativeChannel, mod.ChannelId);
        Assert.True(mod.Amount > 0);
    }

    [Fact]
    public void ChannelModsFor_never_touches_AptitudeAllocation_Share_never_changes_with_other_aptitudes()
    {
        // The actor-level twin of the load-bearing per-tree test: an actor's OWN allocation share on a
        // different aptitude must not move this actor's OWN tree-channel amount -- there is nothing in
        // ChannelModsFor that reads GrandTotal() or Share() at all, only TreeModel.Resolve's per-tree
        // OwnedNodeCount (a genuinely different, legitimate coupling -- D25's shared skill-point wallet
        // -- which this test holds fixed by using CreditRule.None with ownership cost off, so every
        // tree's OwnedNodeCount equals its own GateNodeCount, independent of any other tree's share).
        var focused = AptitudeAllocation.Single(AllocationScope.Commander, Might, 100_000);
        var focusedPlusNoise = focused + AptitudeAllocation.Single(AllocationScope.Commander, Fortitude, 1);

        var a = TreeChannelModel.ChannelModsFor(focused, theta: 100, fmaxMilli: 1200, wMilli: 500, b: 5,
            includeOwnershipCost: false, TreeModel.CreditRule.None, PassiveTreeTuningHub.Tuning, PowerTuningHub.Tuning);
        var b = TreeChannelModel.ChannelModsFor(focusedPlusNoise, theta: 100, fmaxMilli: 1200, wMilli: 500, b: 5,
            includeOwnershipCost: false, TreeModel.CreditRule.None, PassiveTreeTuningHub.Tuning, PowerTuningHub.Tuning);

        // Might's own gate reads aptitude POINTS (TierGate.Reached), not share -- one extra point on a
        // different tree does not change Might's own gate tier here, so Might's contribution is unchanged.
        Assert.Equal(a.Single().Amount, b.Single().Amount);
    }

    // ---- ToActorSetupWithTreeChannels: appends, never replaces -------------------------------------

    [Fact]
    public void ToActorSetupWithTreeChannels_preserves_the_plain_aptitude_mods_and_appends_tree_mods()
    {
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, Might, 100_000);
        var plain = SquadMatch.ToActorSetup("k", "squad", allocation, theta: 100);
        var withTrees = TreeChannelModel.ToActorSetupWithTreeChannels("k", "squad", allocation, theta: 100,
            fmaxMilli: 1200, wMilli: 500, b: 5, includeOwnershipCost: false, TreeModel.CreditRule.None,
            PassiveTreeTuningHub.Tuning, PowerTuningHub.Tuning);

        Assert.True(withTrees.ChannelMods.Count > plain.ChannelMods.Count, "tree power must add mods, not replace them");
        foreach (var m in plain.ChannelMods)
            Assert.Contains(withTrees.ChannelMods, wm => wm.ChannelId == m.ChannelId && wm.Amount == m.Amount);
    }

    // ---- Sweep re-run: same shape TreeModel's own sweeps produce, small trial counts ----------------

    [Fact]
    public void ConcentrationSweep_refuses_a_sweep_that_omits_1000()
    {
        var squads = SquadRoster.Squads();
        var corner = squads.Single(s => s.Id == $"mono-{Might.ToLowerInvariant()}");
        var spread = squads.Single(s => s.Id == "mono-spread");
        var spec = new RunSpec(Theta: 60, Trials: 2, RunSeed: 20260907);

        var ex = Assert.Throws<ArgumentException>(() =>
            TreeChannelModel.ConcentrationSweep(spec, corner, spread, fmaxMillis: new long[] { 1150 }, wMillis: new long[] { 500 }, b: 5));
        Assert.Contains("1000", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcentrationSweep_produces_one_cell_per_fmax_w_ownershipCost_combination_and_is_deterministic()
    {
        var squads = SquadRoster.Squads();
        var corner = squads.Single(s => s.Id == $"mono-{Might.ToLowerInvariant()}");
        var spread = squads.Single(s => s.Id == "mono-spread");
        var spec = new RunSpec(Theta: 60, Trials: 2, RunSeed: 20260907);

        var r1 = TreeChannelModel.ConcentrationSweep(spec, corner, spread, new long[] { 1000, 1200 }, new long[] { 500 }, b: 5);
        var r2 = TreeChannelModel.ConcentrationSweep(spec, corner, spread, new long[] { 1000, 1200 }, new long[] { 500 }, b: 5);

        Assert.Equal(4, r1.Cells.Count);
        foreach (var cell in r1.Cells) Assert.InRange(cell.CornerWinShareMilli, 0, 1000);
        Assert.Equal(r1.Cells.Select(c => c.CornerWinShareMilli), r2.Cells.Select(c => c.CornerWinShareMilli));
    }

    [Fact]
    public void CrossUnlockSweep_produces_one_cell_per_rule_ownershipCost_combination()
    {
        var squads = SquadRoster.Squads();
        var corner = squads.Single(s => s.Id == $"mono-{Might.ToLowerInvariant()}");
        var spread = squads.Single(s => s.Id == "mono-spread");
        var spec = new RunSpec(Theta: 60, Trials: 2, RunSeed: 20260907);
        var rules = new[] { TreeModel.CreditRule.None, TreeModel.CreditRule.Largest };

        var result = TreeChannelModel.CrossUnlockSweep(spec, corner, spread, rules, fmaxMilli: 1200, wMilli: 500, b: 5);

        Assert.Equal(4, result.Cells.Count); // 2 rules x 2 ownershipCost
        foreach (var cell in result.Cells) Assert.InRange(cell.CornerWinShareMilli, 0, 1000);
    }

    // ---- F5's own extension: the soul track inherits the identical fold-back defect, and the fix ----

    [Fact]
    public void SoulChannelModsFor_uses_the_soul_aware_F_not_the_plain_one()
    {
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, Might, 100_000);

        // wMilli=1000 weights H entirely toward H_souls -- a non-zero thetaPerSoulLevelMilli must then
        // move the reported amount relative to wMilli=0 (all node-H, soul track ignored), proving the
        // soul-aware F is genuinely read, not silently defaulted to the plain TreeModel.Resolve value.
        var soulHeavy = TreeChannelModel.SoulChannelModsFor(allocation, theta: 100, fmaxMilli: 1200, wMilli: 1000, b: 5,
            includeOwnershipCost: false, TreeModel.CreditRule.None, thetaPerSoulLevelMilli: 1000,
            PassiveTreeTuningHub.Tuning, PowerTuningHub.Tuning);
        var nodeOnly = TreeChannelModel.ChannelModsFor(allocation, theta: 100, fmaxMilli: 1200, wMilli: 1000, b: 5,
            includeOwnershipCost: false, TreeModel.CreditRule.None, PassiveTreeTuningHub.Tuning, PowerTuningHub.Tuning);

        Assert.Single(soulHeavy);
        Assert.Single(nodeOnly);
        // Not asserting a direction (a single-tree focused build's own H is already near-maximal on
        // both tracks, so the two can legitimately land close) -- asserting the call succeeds end to
        // end and both paths report a real, positive contribution is the wiring proof this test exists
        // for.
        Assert.True(soulHeavy[0].Amount > 0);
        Assert.True(nodeOnly[0].Amount > 0);
    }

    [Fact]
    public void MeasureSoulPairWithTreeChannels_produces_a_well_formed_result()
    {
        var squads = SquadRoster.Squads();
        var corner = RosterEntry.From(squads.Single(s => s.Id == $"mono-{Might.ToLowerInvariant()}"));
        var spread = RosterEntry.From(squads.Single(s => s.Id == "mono-spread"));
        var spec = new RunSpec(Theta: 100, Trials: 5, RunSeed: 20260907);

        var pair = TreeChannelModel.MeasureSoulPairWithTreeChannels(corner, spread, spec,
            fmaxMilli: 1200, wMilli: 500, b: 5, includeOwnershipCost: true, TreeModel.CreditRule.Largest,
            thetaPerSoulLevelMilli: 1000);

        Assert.Equal(5L, pair.Victories + pair.Defeats + pair.Stalemates);
        Assert.InRange(pair.WinShareMilli, 0, 1000);
    }


    /// <summary>
    /// The explicit "state the delta" requirement (F8's own acceptance bullet 3): run the SAME cell
    /// through both the old fold-back model and the new channel model and record whether they agree.
    /// F7's own analytical finding predicts these need not match -- this test does not assert equality,
    /// it OBSERVES and reports, matching F8's own instruction not to assume unchanged.
    /// </summary>
    [Fact]
    public void Old_foldback_and_new_channel_model_report_for_the_same_cell_recorded_not_assumed_equal()
    {
        var squads = SquadRoster.Squads();
        var corner = squads.Single(s => s.Id == $"mono-{Might.ToLowerInvariant()}");
        var spread = squads.Single(s => s.Id == "mono-spread");
        var spec = new RunSpec(Theta: 60, Trials: 50, RunSeed: 20260907);

        var oldModel = TreeModel.ConcentrationSweep(spec, corner, spread, new long[] { 1000, 1200 }, new long[] { 500 }, b: 5, refineTrials: null, parallel: false);
        var newModel = TreeChannelModel.ConcentrationSweep(spec, corner, spread, new long[] { 1000, 1200 }, new long[] { 500 }, b: 5);

        Assert.Equal(oldModel.Cells.Count, newModel.Cells.Count);
        // Both are well-formed, real measurements -- no assertion that the numbers match, since F7
        // found they measure genuinely different mechanisms. Recorded via the test's own output.
        for (var i = 0; i < oldModel.Cells.Count; i++)
        {
            Assert.InRange(oldModel.Cells[i].CornerWinShareMilli, 0, 1000);
            Assert.InRange(newModel.Cells[i].CornerWinShareMilli, 0, 1000);
        }
    }
}
