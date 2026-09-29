using FusionRpg.Core.PassiveTree.Resolve;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Tools.SquadHarness;
using Xunit;

namespace FusionRpg.SquadHarness.Tests;

/// <summary>
/// spec-squad-harness.md §11 S3 (todo "F5: S3 -- the soul track in the model"). Pure-math layer, no
/// <c>BattleEngine</c> call -- <see cref="SoulTrackModel.Resolve"/> is tested directly against real
/// production tuning, exactly <see cref="TreeModelTests"/>'s own split (algebra tested directly,
/// wiring tested separately at trivial trial counts in <see cref="SoulTrackSweepTests"/>).
/// </summary>
public class SoulTrackModelTests
{
    static readonly string Might = BuildFactory.Roster[0]; // "Might"


    static AptitudeAllocation SingleTreeBuild() =>
        AptitudeAllocation.Single(AllocationScope.Commander, Might, 100_000);

    static AptitudeAllocation EvenSpreadBuild() => BuildFactory.EvenSpread();

    // ---- acceptance bullet 3: the soul read matches D3's shipped derivation exactly ------------------

    [Fact]
    public void Resolve_ThetaNode_matches_the_real_SoulTrack_ThetaNode_derivation_exactly()
    {
        // A single-tree build puts the WHOLE soul budget in one tree (share = 1), so that tree's
        // soulLevel is exactly the production PointBudget.PointsFor(...) budget -- no rounding split
        // across multiple trees to account for. This is the direct comparison acceptance bullet 3 asks
        // for: the model's own Theta_node for that tree must equal SoulTrack.ThetaNode's output for the
        // SAME inputs, computed independently by the test, never assumed to agree.
        const long theta = 100;
        const long ws = 250; // thetaPerSoulLevelMilli
        var result = SoulTrackModel.Resolve(SingleTreeBuild(), theta, fmaxMilli: 1200, wMilli: 500, b: 5,
            includeOwnershipCost: false, TreeModel.CreditRule.None, thetaPerSoulLevelMilli: ws);

        var spiked = result.SoulTrees.Single(t => t.TreeId == Might);
        var expectedThetaNode = SoulTrack.ThetaNode(theta, spiked.SoulLevel, ws);
        Assert.Equal(expectedThetaNode, spiked.ThetaNode);
        Assert.Equal(expectedThetaNode - theta, spiked.ThetaOffset);
    }

    [Fact]
    public void Resolve_ThetaNode_matches_SoulTrack_ThetaNode_across_every_tree_of_a_spread_build()
    {
        // Same assertion, but over EVERY tree of a multi-tree build -- proves the per-tree loop calls
        // the real production function for each tree, not just the one a single-tree test would exercise.
        const long theta = 300;
        const long ws = 400;
        var result = SoulTrackModel.Resolve(EvenSpreadBuild(), theta, fmaxMilli: 1200, wMilli: 500, b: 5,
            includeOwnershipCost: false, TreeModel.CreditRule.None, thetaPerSoulLevelMilli: ws);

        foreach (var tree in result.SoulTrees)
        {
            var expected = SoulTrack.ThetaNode(theta, tree.SoulLevel, ws);
            Assert.Equal(expected, tree.ThetaNode);
            Assert.Equal(expected - theta, tree.ThetaOffset);
        }
    }

    [Fact]
    public void Resolve_throws_when_thetaPerSoulLevelMilli_is_negative_via_the_real_SoulTrack_guard()
    {
        // SoulTrack.ThetaNode itself refuses a negative Ws -- this class never duplicates that guard, it
        // just must not swallow it either.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SoulTrackModel.Resolve(SingleTreeBuild(), 100, 1200, 500, 5, includeOwnershipCost: false,
                TreeModel.CreditRule.None, thetaPerSoulLevelMilli: -1));
    }

    // ---- H_souls: the second Herfindahl axis ----------------------------------------------------------

    [Fact]
    public void Resolve_a_single_tree_build_has_fully_concentrated_H_souls_when_Ws_is_positive()
    {
        var result = SoulTrackModel.Resolve(SingleTreeBuild(), 100, 1200, wMilli: 0 /* H reads H_souls alone */,
            b: 5, includeOwnershipCost: false, TreeModel.CreditRule.None, thetaPerSoulLevelMilli: 250);
        // wMilli=0 -> H = H_souls exactly (Concentration.BlendMilli's own formula). A single tree holds
        // the WHOLE soul budget, so its Theta-offset share is 100% -- H_souls reads 1000 permille.
        Assert.Equal(1000L, result.HSoulsMilli);
        Assert.Equal(1000L, result.HMilli);
    }

    [Fact]
    public void Resolve_H_souls_is_zero_when_thetaPerSoulLevelMilli_is_zero()
    {
        // Ws=0 -> every tree's offset is 0 regardless of soul level (SoulTrack.ThetaNode's own formula)
        // -> Concentration.HerfindahlMilli's own "sum=0 reads zero" rule, never a 1/n fallback.
        var result = SoulTrackModel.Resolve(SingleTreeBuild(), 100, 1200, wMilli: 0, b: 5,
            includeOwnershipCost: false, TreeModel.CreditRule.None, thetaPerSoulLevelMilli: 0);
        Assert.Equal(0L, result.HSoulsMilli);
    }

    [Fact]
    public void Resolve_an_even_spread_build_has_lower_H_souls_than_a_single_tree_build()
    {
        var single = SoulTrackModel.Resolve(SingleTreeBuild(), 100, 1200, 0, 5, includeOwnershipCost: false, TreeModel.CreditRule.None, 250);
        var spread = SoulTrackModel.Resolve(EvenSpreadBuild(), 100, 1200, 0, 5, includeOwnershipCost: false, TreeModel.CreditRule.None, 250);
        Assert.True(spread.HSoulsMilli < single.HSoulsMilli,
            $"spread H_souls={spread.HSoulsMilli} should be well below single-tree H_souls={single.HSoulsMilli}");
    }

    [Fact]
    public void Resolve_H_blends_H_nodes_and_H_souls_exactly_as_Concentration_BlendMilli_would()
    {
        const long wMilli = 400;
        var result = SoulTrackModel.Resolve(EvenSpreadBuild(), 100, 1200, wMilli, 5, includeOwnershipCost: true, TreeModel.CreditRule.Largest, 250);
        var expected = Concentration.BlendMilli(result.HNodesMilli, result.HSoulsMilli, wMilli);
        Assert.Equal(expected, result.HMilli);
    }

    [Fact]
    public void Resolve_HNodesMilli_matches_the_plain_TreeModel_Resolve_call_it_was_borrowed_from()
    {
        // SoulTrackModel never re-derives the points track -- it borrows TreeModel.Resolve's own
        // HNodesMilli unchanged.
        var pointsOnly = TreeModel.Resolve(EvenSpreadBuild(), 100, 1200, 500, 5, includeOwnershipCost: true, TreeModel.CreditRule.Largest);
        var soulAware = SoulTrackModel.Resolve(EvenSpreadBuild(), 100, 1200, 500, 5, includeOwnershipCost: true, TreeModel.CreditRule.Largest, thetaPerSoulLevelMilli: 250);
        Assert.Equal(pointsOnly.HNodesMilli, soulAware.HNodesMilli);
    }

    [Fact]
    public void Resolve_F_reads_1000_at_H_zero_and_Fmax_at_H_one_thousand_same_as_TreeModel()
    {
        // No soul investment at all AND a single-tree build's own node concentration would normally push
        // H to 1 -- force H_souls to 0 (Ws=0) and read wMilli=1000 so H = H_nodes alone, matching F4's
        // own single-tree-is-fully-concentrated result.
        var result = SoulTrackModel.Resolve(SingleTreeBuild(), 100, 1200, wMilli: 1000, b: 5,
            includeOwnershipCost: false, TreeModel.CreditRule.None, thetaPerSoulLevelMilli: 0);
        Assert.Equal(1000L, result.HMilli);
        Assert.Equal(1200L, result.FMilli);
    }

    [Fact]
    public void Resolve_folds_tree_power_back_additively_never_below_the_raw_aptitude_points()
    {
        var result = SoulTrackModel.Resolve(SingleTreeBuild(), 100, 1200, 500, 5, includeOwnershipCost: false, TreeModel.CreditRule.None, 250);
        var effectivePoints = result.EffectiveAllocation.PointsAt(AllocationScope.Commander, Might);
        var raw = TreeModel.Resolve(SingleTreeBuild(), 100, 1200, 500, 5, false, TreeModel.CreditRule.None)
            .Trees.Single(t => t.TreeId == Might).AptitudePoints;
        Assert.True(effectivePoints >= raw, "F*W is always added, never subtracted (F >= 1000pm)");
    }

    [Fact]
    public void Resolve_is_deterministic_for_the_same_inputs()
    {
        var a = SoulTrackModel.Resolve(EvenSpreadBuild(), 100, 1200, 500, 5, includeOwnershipCost: true, TreeModel.CreditRule.Quarter, 250);
        var b = SoulTrackModel.Resolve(EvenSpreadBuild(), 100, 1200, 500, 5, includeOwnershipCost: true, TreeModel.CreditRule.Quarter, 250);
        Assert.Equal(a.HMilli, b.HMilli);
        Assert.Equal(a.HSoulsMilli, b.HSoulsMilli);
        Assert.Equal(a.FMilli, b.FMilli);
        Assert.Equal(a.EffectiveAllocation.PointsAt(AllocationScope.Commander, Might), b.EffectiveAllocation.PointsAt(AllocationScope.Commander, Might));
    }

    // ---- ApplyTreeModel: squad-scope fold ------------------------------------------------------------

    [Fact]
    public void ApplyTreeModel_produces_one_effective_allocation_per_squad_actor()
    {
        var squads = SquadRoster.Squads();
        var corner = squads.Single(s => s.Id == $"mono-{Might.ToLowerInvariant()}");
        var entry = SoulTrackModel.ApplyTreeModel(corner, 100, 1200, 500, 5, includeOwnershipCost: true, TreeModel.CreditRule.Largest, thetaPerSoulLevelMilli: 250);
        Assert.Equal(corner.Actors.Count, entry.Actors.Count);
        Assert.Equal(6, entry.Actors.Count); // WebMatchService.BuildSquad's own maxSquad, same bound TreeModelTests ties to
    }
}
