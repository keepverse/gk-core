using FusionRpg.Core.PassiveTree;
using FusionRpg.Core.PassiveTree.Resolve;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Tools.SquadHarness;
using Xunit;

namespace FusionRpg.SquadHarness.Tests;

/// <summary>
/// spec-squad-harness.md §4 / §11 S2 (F4). Two layers, matching <see cref="ErosionTests"/>'s own split:
/// the algebra (<see cref="TreeModel.GateQuantities"/>, <see cref="TreeModel.AffordableNodeCount"/>,
/// <see cref="TreeModel.AllocateOwnedNodes"/>, <see cref="TreeModel.Resolve"/>) is pure and tested
/// directly with hand-computed expectations -- no <c>BattleEngine</c> call at all -- and a second,
/// smaller layer proves <see cref="TreeModel.ConcentrationSweep"/>/<see cref="TreeModel.CrossUnlockSweep"/>
/// actually wire into the real measurement machinery at trivial trial counts. This repo's heavy
/// concurrent machine load this session made a real 3,000/40,000-trial production sweep impractical to
/// run live (matching F2's and F3's own disclosed gap) -- these tests deliberately stay small.
/// </summary>
public class TreeModelTests
{
    static readonly string Might = BuildFactory.Roster[0]; // "Might"
    static readonly string Fortitude = BuildFactory.Roster[1]; // "Fortitude", a different posture-mate group

    // ---- AffordableNodeCount: exact match against doc 12's own worked example ----------------------

    [Fact]
    public void AffordableNodeCount_matches_doc12s_worked_example_exactly()
    {
        // docs/research/passive-tree/12-rising-unlock-cost.md section 5.2: first=5, step=2, wallet=1100
        // -> 31 nodes (31*35=1085 <= 1100 < 32*36... i.e. Cumulative(32)=1152 > 1100).
        Assert.Equal(1085L, TreeUnlockCost.Cumulative(31, first: 5, step: 2));
        Assert.Equal(1152L, TreeUnlockCost.Cumulative(32, first: 5, step: 2));
        Assert.Equal(31L, TreeModel.AffordableNodeCount(1100, first: 5, step: 2));
    }

    [Fact]
    public void AffordableNodeCount_is_zero_when_even_the_first_node_is_unaffordable()
    {
        Assert.Equal(0L, TreeModel.AffordableNodeCount(4, first: 5, step: 2));
    }

    [Fact]
    public void AffordableNodeCount_is_zero_at_zero_budget()
    {
        Assert.Equal(0L, TreeModel.AffordableNodeCount(0, first: 5, step: 2));
    }

    [Fact]
    public void AffordableNodeCount_throws_on_a_negative_budget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TreeModel.AffordableNodeCount(-1, 5, 2));
    }

    [Fact]
    public void AffordableNodeCount_never_overshoots_its_own_cumulative_cost()
    {
        // Property check across a spread of budgets: N is affordable iff Cumulative(N) <= budget and
        // Cumulative(N+1) > budget -- the binary search must land exactly on that boundary every time.
        foreach (var budget in new long[] { 0, 1, 4, 5, 6, 34, 35, 36, 1000, 1_000_000, 1_000_000_000 })
        {
            var n = TreeModel.AffordableNodeCount(budget, first: 5, step: 2);
            Assert.True(TreeUnlockCost.Cumulative(n, 5, 2) <= budget, $"budget={budget} n={n} overshoots");
            Assert.True(TreeUnlockCost.Cumulative(n + 1, 5, 2) > budget, $"budget={budget} n={n} could afford one more");
        }
    }

    // ---- AllocateOwnedNodes: deterministic water-fill -----------------------------------------------

    [Fact]
    public void AllocateOwnedNodes_gives_everything_to_the_single_tree_a_focused_build_invested_in()
    {
        var priority = new Dictionary<string, long> { [Might] = 100, [Fortitude] = 0 };
        var cap = new Dictionary<string, long> { [Might] = 28, [Fortitude] = 0 };
        var owned = TreeModel.AllocateOwnedNodes(priority, cap, totalAffordable: 31);
        Assert.Equal(28L, owned[Might]); // capped at its own gate -- 3 nodes of wallet spare, doc 12's own worked number
        Assert.Equal(0L, owned[Fortitude]);
    }

    [Fact]
    public void AllocateOwnedNodes_spreads_the_budget_across_trees_in_descending_priority_order()
    {
        var priority = new Dictionary<string, long> { ["A"] = 30, ["B"] = 20, ["C"] = 10 };
        var cap = new Dictionary<string, long> { ["A"] = 4, ["B"] = 4, ["C"] = 4 };
        var owned = TreeModel.AllocateOwnedNodes(priority, cap, totalAffordable: 7);
        Assert.Equal(4L, owned["A"]); // fully funded first
        Assert.Equal(3L, owned["B"]); // partially funded second
        Assert.Equal(0L, owned["C"]); // nothing left
    }

    [Fact]
    public void AllocateOwnedNodes_breaks_ties_by_tree_id_ordinal()
    {
        var priority = new Dictionary<string, long> { ["Zebra"] = 10, ["Alpha"] = 10 };
        var cap = new Dictionary<string, long> { ["Zebra"] = 5, ["Alpha"] = 5 };
        var owned = TreeModel.AllocateOwnedNodes(priority, cap, totalAffordable: 5);
        Assert.Equal(5L, owned["Alpha"]); // "Alpha" < "Zebra" ordinally, so it is funded first
        Assert.Equal(0L, owned["Zebra"]);
    }

    [Fact]
    public void AllocateOwnedNodes_never_allocates_more_than_the_total_affordable()
    {
        var priority = new Dictionary<string, long> { ["A"] = 1, ["B"] = 1 };
        var cap = new Dictionary<string, long> { ["A"] = 100, ["B"] = 100 };
        var owned = TreeModel.AllocateOwnedNodes(priority, cap, totalAffordable: 50);
        Assert.Equal(50L, owned.Values.Sum());
    }

    [Fact]
    public void AllocateOwnedNodes_throws_on_a_negative_total()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TreeModel.AllocateOwnedNodes(new Dictionary<string, long>(), new Dictionary<string, long>(), -1));
    }

    // ---- GateQuantities: the four D28 credit rules --------------------------------------------------

    static readonly Dictionary<string, long> ThreeTreeBase = new() { ["A"] = 100, ["B"] = 40, ["C"] = 10 };
    static readonly Dictionary<string, string?> AllSameGroup = new() { ["A"] = "grp", ["B"] = "grp", ["C"] = "grp" };

    [Fact]
    public void GateQuantities_None_returns_the_base_unchanged()
    {
        var gate = TreeModel.GateQuantities(ThreeTreeBase, AllSameGroup, TreeModel.CreditRule.None);
        Assert.Equal(ThreeTreeBase, gate);
    }

    [Fact]
    public void GateQuantities_Largest_matches_the_real_production_CrossUnlock_Gate_exactly()
    {
        var gate = TreeModel.GateQuantities(ThreeTreeBase, AllSameGroup, TreeModel.CreditRule.Largest);
        foreach (var id in ThreeTreeBase.Keys)
            Assert.Equal(CrossUnlock.Gate(id, ThreeTreeBase, AllSameGroup), gate[id]);
        // Largest credits exactly ONE lender, the max of the OTHERS (never a sum, CrossUnlock's own doc).
        Assert.Equal(140L, gate["A"]); // 100 + max(40,10) = 100+40
        Assert.Equal(140L, gate["B"]); // 40 + max(100,10) = 40+100
        Assert.Equal(110L, gate["C"]); // 10 + max(100,40) = 10+100
    }

    [Fact]
    public void GateQuantities_Full_sums_every_mate()
    {
        var gate = TreeModel.GateQuantities(ThreeTreeBase, AllSameGroup, TreeModel.CreditRule.Full);
        Assert.Equal(150L, gate["A"]); // 100 + (40+10)
        Assert.Equal(150L, gate["B"]); // 40 + (100+10)
        Assert.Equal(150L, gate["C"]); // 10 + (100+40)
    }

    [Fact]
    public void GateQuantities_Quarter_credits_one_quarter_of_the_mate_sum()
    {
        var gate = TreeModel.GateQuantities(ThreeTreeBase, AllSameGroup, TreeModel.CreditRule.Quarter);
        Assert.Equal(112L, gate["A"]); // 100 + (40+10)/4 = 100 + 12
        Assert.Equal(67L, gate["B"]);  // 40 + (100+10)/4 = 40 + 27
        Assert.Equal(45L, gate["C"]);  // 10 + (100+40)/4 = 10 + 35
    }

    [Fact]
    public void GateQuantities_a_tree_with_no_stance_group_gets_no_credit_under_any_rule()
    {
        var stanceGroups = new Dictionary<string, string?> { ["A"] = null, ["B"] = "grp" };
        var baseByTree = new Dictionary<string, long> { ["A"] = 100, ["B"] = 40 };
        foreach (var rule in new[] { TreeModel.CreditRule.None, TreeModel.CreditRule.Largest, TreeModel.CreditRule.Quarter, TreeModel.CreditRule.Full })
        {
            var gate = TreeModel.GateQuantities(baseByTree, stanceGroups, rule);
            Assert.Equal(100L, gate["A"]);
        }
    }

    // ---- Resolve: the whole per-actor pipeline, against real production tuning ---------------------


    static AptitudeAllocation SingleTreeBuild() =>
        AptitudeAllocation.Single(AllocationScope.Commander, Might, 100_000);

    static AptitudeAllocation EvenSpreadBuild() => BuildFactory.EvenSpread();

    [Fact]
    public void Resolve_throws_on_a_fmaxMilli_below_the_D5_floor_of_1000()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TreeModel.Resolve(SingleTreeBuild(), theta: 100, fmaxMilli: 999, wMilli: 500, b: 5, includeOwnershipCost: false, TreeModel.CreditRule.Largest));
    }

    [Fact]
    public void Resolve_accepts_fmaxMilli_of_exactly_1000_the_D5_legal_no_multiplier_floor()
    {
        var result = TreeModel.Resolve(SingleTreeBuild(), theta: 100, fmaxMilli: 1000, wMilli: 500, b: 5, includeOwnershipCost: false, TreeModel.CreditRule.Largest);
        Assert.Equal(1000L, result.FMilli); // F == 1.0 exactly, regardless of H (Concentration's own test 9 property)
    }

    [Fact]
    public void Resolve_throws_on_wMilli_out_of_the_zero_to_one_thousand_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TreeModel.Resolve(SingleTreeBuild(), 100, 1200, wMilli: 1001, b: 5, includeOwnershipCost: false, TreeModel.CreditRule.Largest));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TreeModel.Resolve(SingleTreeBuild(), 100, 1200, wMilli: -1, b: 5, includeOwnershipCost: false, TreeModel.CreditRule.Largest));
    }

    [Fact]
    public void Resolve_throws_on_a_negative_b()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TreeModel.Resolve(SingleTreeBuild(), 100, 1200, 500, b: -1, includeOwnershipCost: false, TreeModel.CreditRule.Largest));
    }

    [Fact]
    public void Resolve_reads_H_souls_as_honestly_zero_no_soul_track_until_S3()
    {
        var result = TreeModel.Resolve(SingleTreeBuild(), 100, 1200, 500, 5, includeOwnershipCost: false, TreeModel.CreditRule.Largest);
        Assert.Equal(0L, result.HSoulsMilli);
    }

    [Fact]
    public void Resolve_a_single_tree_build_is_fully_concentrated_H_nodes_is_one_thousand_permille()
    {
        var result = TreeModel.Resolve(SingleTreeBuild(), theta: 100, fmaxMilli: 1200, wMilli: 1000, b: 5, includeOwnershipCost: false, TreeModel.CreditRule.None);
        // wMilli=1000 -> H reads H_nodes alone (H_souls' 0 contributes nothing). A single spiked tree
        // owns nodes nowhere else, so the Herfindahl index over its per-tree node counts is exactly 1000.
        Assert.Equal(1000L, result.HMilli);
        Assert.Equal(1200L, result.FMilli); // F = Fmax exactly at H = 1
    }

    [Fact]
    public void Resolve_an_even_spread_build_is_far_less_concentrated_than_a_single_tree_build()
    {
        var single = TreeModel.Resolve(SingleTreeBuild(), 100, 1200, 1000, 5, includeOwnershipCost: false, TreeModel.CreditRule.None);
        var spread = TreeModel.Resolve(EvenSpreadBuild(), 100, 1200, 1000, 5, includeOwnershipCost: false, TreeModel.CreditRule.None);
        Assert.True(spread.HMilli < single.HMilli, $"spread H={spread.HMilli} should be well below single-tree H={single.HMilli}");
    }

    [Fact]
    public void Resolve_with_and_without_ownership_cost_are_reported_as_genuinely_different_cells_for_a_spread_build()
    {
        // The whole point of D25 (doc 12 section5): a spread build's wallet cannot afford every gate it
        // opened, so turning ownership cost ON must change BOTH the realised tree power (W) and the
        // concentration index (H) for a spread build -- never the same numbers under a different label.
        var without = TreeModel.Resolve(EvenSpreadBuild(), theta: 100, fmaxMilli: 1200, wMilli: 1000, b: 5, includeOwnershipCost: false, TreeModel.CreditRule.None);
        var with = TreeModel.Resolve(EvenSpreadBuild(), theta: 100, fmaxMilli: 1200, wMilli: 1000, b: 5, includeOwnershipCost: true, TreeModel.CreditRule.None);

        Assert.False(without.OwnershipCostApplied);
        Assert.True(with.OwnershipCostApplied);

        var totalOwnedWithout = without.Trees.Sum(t => t.OwnedNodeCount);
        var totalOwnedWith = with.Trees.Sum(t => t.OwnedNodeCount);
        Assert.True(totalOwnedWith < totalOwnedWithout, "a spread build's wallet must not afford every node its gates opened");

        // Doc 12's own causal claim, stated as a direction: pricing ownership CONCENTRATES a spread
        // build's realised holdings onto fewer trees (the wallet cannot cover all twelve at once), so H
        // must rise, not just move -- the mechanism behind doc 12's measured 2.3x reversal.
        Assert.True(with.HMilli > without.HMilli, $"with={with.HMilli}pm should exceed without={without.HMilli}pm");
    }

    [Fact]
    public void Resolve_a_single_tree_build_stays_fully_concentrated_regardless_of_ownership_cost()
    {
        // With every OTHER tree at exactly zero investment, H_nodes reads 1000 permille no matter how
        // many nodes the wallet actually affords in the one tree that has any -- concentration is about
        // WHERE the nodes are, not how many of them the wallet could fund. But D25 still binds even a
        // maximally concentrated build once its gate saturates the authored tier cap (D29, req(10)=275)
        // faster than the wallet's own linear growth (11/theta) can keep pace with -- the REALISED tree
        // power still differs, which is exactly D25 being non-decorative rather than a no-op here.
        var without = TreeModel.Resolve(SingleTreeBuild(), 100, 1200, 1000, 5, includeOwnershipCost: false, TreeModel.CreditRule.None);
        var with = TreeModel.Resolve(SingleTreeBuild(), 100, 1200, 1000, 5, includeOwnershipCost: true, TreeModel.CreditRule.None);

        Assert.Equal(1000L, without.HMilli);
        Assert.Equal(1000L, with.HMilli);

        var spikeWithout = without.Trees.Single(t => t.TreeId == Might);
        var spikeWith = with.Trees.Single(t => t.TreeId == Might);
        Assert.True(spikeWith.OwnedNodeCount < spikeWithout.OwnedNodeCount, "the wallet must not cover a tier-10-saturated single-tree build's full node count");
        Assert.NotEqual(spikeWithout.TreePower, spikeWith.TreePower);
    }

    [Fact]
    public void Resolve_with_ownership_cost_still_fully_funds_the_tree_it_invested_most_in_first()
    {
        // Doc 12 section5.2's own worked point, reproduced against the REAL corner shape
        // (BuildFactory.Build, not a hand-rolled 100% allocation): at theta=100 the spike tree's gate
        // opens tier 7 (28 nodes) and the wallet affords 31 nodes in total -- the water-fill's own
        // "highest investment first" rule means the spike is fully funded before the eleven floor
        // trees see a single node, exactly as doc 12 describes ("3 nodes spare").
        var withoutCost = TreeModel.Resolve(BuildFactory.Build(Might), 100, 1200, 1000, 5, includeOwnershipCost: false, TreeModel.CreditRule.None);
        var withCost = TreeModel.Resolve(BuildFactory.Build(Might), 100, 1200, 1000, 5, includeOwnershipCost: true, TreeModel.CreditRule.None);

        var spikeWithout = withoutCost.Trees.Single(t => t.TreeId == Might);
        var spikeWith = withCost.Trees.Single(t => t.TreeId == Might);
        Assert.Equal(7, spikeWithout.GateTier);
        Assert.Equal(28L, spikeWithout.GateNodeCount);
        Assert.Equal(7, spikeWith.AffordTier);
        Assert.Equal(28L, spikeWith.OwnedNodeCount);
    }

    [Fact]
    public void Resolve_folds_tree_power_back_additively_never_below_the_raw_aptitude_points()
    {
        var result = TreeModel.Resolve(SingleTreeBuild(), 100, 1200, 500, 5, includeOwnershipCost: false, TreeModel.CreditRule.None);
        var spiked = result.Trees.Single(t => t.TreeId == Might);
        var effectivePoints = result.EffectiveAllocation.PointsAt(AllocationScope.Commander, Might);
        Assert.True(effectivePoints >= spiked.AptitudePoints, "F*W is always added, never subtracted (F >= 1000‰)");
    }

    [Fact]
    public void Resolve_is_deterministic_for_the_same_inputs()
    {
        var a = TreeModel.Resolve(EvenSpreadBuild(), 100, 1200, 500, 5, includeOwnershipCost: true, TreeModel.CreditRule.Quarter);
        var b = TreeModel.Resolve(EvenSpreadBuild(), 100, 1200, 500, 5, includeOwnershipCost: true, TreeModel.CreditRule.Quarter);
        Assert.Equal(a.HMilli, b.HMilli);
        Assert.Equal(a.FMilli, b.FMilli);
        Assert.Equal(a.EffectiveAllocation.PointsAt(AllocationScope.Commander, Might), b.EffectiveAllocation.PointsAt(AllocationScope.Commander, Might));
    }

    // ---- ConcentrationSweep: the D5 "must include 1000" refusal, and a tiny live wiring check -------

    [Fact]
    public void ConcentrationSweep_refuses_a_sweep_that_omits_1000()
    {
        var squads = SquadRoster.Squads();
        var corner = squads.Single(s => s.Id == $"mono-{Might.ToLowerInvariant()}");
        var spread = squads.Single(s => s.Id == "mono-spread");
        var spec = new RunSpec(Theta: 60, Trials: 2, RunSeed: 20260906);

        var ex = Assert.Throws<ArgumentException>(() =>
            TreeModel.ConcentrationSweep(spec, corner, spread, fmaxMillis: new long[] { 1150, 1200 }, wMillis: new long[] { 500 }, b: 5, refineTrials: null, parallel: false));
        Assert.Contains("1000", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcentrationSweep_produces_one_cell_per_fmax_w_ownershipCost_combination()
    {
        var squads = SquadRoster.Squads();
        var corner = squads.Single(s => s.Id == $"mono-{Might.ToLowerInvariant()}");
        var spread = squads.Single(s => s.Id == "mono-spread");
        var spec = new RunSpec(Theta: 60, Trials: 2, RunSeed: 20260906);

        var result = TreeModel.ConcentrationSweep(spec, corner, spread, fmaxMillis: new long[] { 1000, 1200 }, wMillis: new long[] { 500 }, b: 5, refineTrials: null, parallel: false);

        Assert.Equal(4, result.Cells.Count); // 2 fmax x 1 w x 2 ownershipCost
        foreach (var cell in result.Cells)
        {
            Assert.InRange(cell.CornerWinShareMilli, 0, 1000);
            Assert.True(cell.HalfWidthMilli > 0);
        }
        // Every proposed value carries a half-width, and both ownership-cost states are present as
        // distinct, separately-labelled cells (acceptance bullet 3).
        Assert.Contains(result.Cells, c => c.OwnershipCostApplied);
        Assert.Contains(result.Cells, c => !c.OwnershipCostApplied);
    }

    [Fact]
    public void ConcentrationSweep_is_deterministic_for_the_same_seed()
    {
        var squads = SquadRoster.Squads();
        var corner = squads.Single(s => s.Id == $"mono-{Might.ToLowerInvariant()}");
        var spread = squads.Single(s => s.Id == "mono-spread");
        var spec = new RunSpec(Theta: 60, Trials: 3, RunSeed: 20260906);

        var r1 = TreeModel.ConcentrationSweep(spec, corner, spread, new long[] { 1000, 1200 }, new long[] { 500 }, 5, null, false);
        var r2 = TreeModel.ConcentrationSweep(spec, corner, spread, new long[] { 1000, 1200 }, new long[] { 500 }, 5, null, false);

        Assert.Equal(r1.Cells, r2.Cells);
    }

    [Fact]
    public void CrossUnlockSweep_produces_one_cell_per_rule_ownershipCost_combination()
    {
        var squads = SquadRoster.Squads();
        var corner = squads.Single(s => s.Id == $"mono-{Might.ToLowerInvariant()}");
        var spread = squads.Single(s => s.Id == "mono-spread");
        var spec = new RunSpec(Theta: 60, Trials: 2, RunSeed: 20260906);

        var rules = new[] { TreeModel.CreditRule.None, TreeModel.CreditRule.Largest, TreeModel.CreditRule.Quarter, TreeModel.CreditRule.Full };
        var result = TreeModel.CrossUnlockSweep(spec, corner, spread, rules, fmaxMilli: 1200, wMilli: 500, b: 5, refineTrials: null, parallel: false);

        Assert.Equal(8, result.Cells.Count); // 4 rules x 2 ownershipCost
        foreach (var rule in rules)
        {
            Assert.Contains(result.Cells, c => c.Rule == rule && c.OwnershipCostApplied);
            Assert.Contains(result.Cells, c => c.Rule == rule && !c.OwnershipCostApplied);
        }
    }

    // ---- artifact writers: propose only, never touch gk-core/data/tuning ------------------------------------

    [Fact]
    public void WriteConcentrationArtifact_never_writes_to_data_tuning()
    {
        var squads = SquadRoster.Squads();
        var corner = squads.Single(s => s.Id == $"mono-{Might.ToLowerInvariant()}");
        var spread = squads.Single(s => s.Id == "mono-spread");
        var spec = new RunSpec(60, 2, 20260906);
        var result = TreeModel.ConcentrationSweep(spec, corner, spread, new long[] { 1000 }, new long[] { 500 }, 5, null, false);

        var tuningDir = Path.Combine(TuningBootstrap.FindRepoRoot(), "data", "tuning");
        var before = Directory.GetFiles(tuningDir).Select(File.GetLastWriteTimeUtc).ToList();

        var path = Path.Combine(Path.GetTempPath(), $"concentration-test-{Guid.NewGuid():N}.json");
        try
        {
            TreeModel.WriteConcentrationArtifact(result, path);
            Assert.True(File.Exists(path));
            var after = Directory.GetFiles(tuningDir).Select(File.GetLastWriteTimeUtc).ToList();
            Assert.Equal(before, after);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WriteCrossUnlockArtifact_never_writes_to_data_tuning()
    {
        var squads = SquadRoster.Squads();
        var corner = squads.Single(s => s.Id == $"mono-{Might.ToLowerInvariant()}");
        var spread = squads.Single(s => s.Id == "mono-spread");
        var spec = new RunSpec(60, 2, 20260906);
        var result = TreeModel.CrossUnlockSweep(spec, corner, spread, new[] { TreeModel.CreditRule.Largest }, 1200, 500, 5, null, false);

        var tuningDir = Path.Combine(TuningBootstrap.FindRepoRoot(), "data", "tuning");
        var before = Directory.GetFiles(tuningDir).Select(File.GetLastWriteTimeUtc).ToList();

        var path = Path.Combine(Path.GetTempPath(), $"crossunlock-test-{Guid.NewGuid():N}.json");
        try
        {
            TreeModel.WriteCrossUnlockArtifact(result, path);
            Assert.True(File.Exists(path));
            var after = Directory.GetFiles(tuningDir).Select(File.GetLastWriteTimeUtc).ToList();
            Assert.Equal(before, after);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
