using FusionRpg.Core.PassiveTree.Resolve;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.Resolve;

/// <summary>
/// passive-tree G7 (spec-species-tree.md §8.1 point 2) — the end-to-end proof the todo's own
/// verification line names: *"a reviewer opening a species card sees a live ladder, not zeros."*
/// Not a unit test of the budget function alone — this wires the real pieces in the real order a
/// species tree's own resolve would: a specimen's OWN level → <see cref="PointBudget.UniqueCreatureSourceFromLevel"/>
/// → <see cref="PointBudget.PointsFor"/> at <see cref="AllocationScope.UniqueCreature"/> (via
/// <see cref="UniqueCreatureAllocation.Baseline"/>) → the resulting aptitude points fed into
/// <see cref="TierGate.Reached"/> with the REAL shipped `passive-tree.v1.json` req-scale — the exact
/// call `PassiveTreeEndpoints.cs:142` makes for the shared corpus today, proven here for the
/// `UniqueCreature` scope a species tree's own gate would read.
/// </summary>
public class UniqueCreatureSpeciesTreeGateTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static readonly AptitudeTuning RealAptitudeTuning = AptitudeTuningLoader.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "aptitudes.v10.json")));

    static readonly PassiveTreeTuning RealTreeTuning = PassiveTreeTuningLoader.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "passive-tree.v1.json")));

    // D17: a species tree locks a build-favour triple -- ONE primary aptitude. Modelled here as a
    // 100%-share plan (the whole UniqueCreature budget lands on the species' own locked aptitude), never
    // a private split invented for this test.
    static readonly Dictionary<string, long> LockedPrimaryOnly = new(StringComparer.Ordinal) { ["Might"] = 1000 };

    [Fact]
    public void A_levelled_specimens_species_tree_reaches_above_tier_zero()
    {
        // The fixture actor: a specimen at level 21 (UniqueCreatureSourceFromLevel(21) = 20, the same
        // representative mid-game milestone PointBudgetTests.Real_budgets_are_ordered_at_representative_sources
        // already uses for this exact scope).
        const long specimenLevel = 21;

        var allocation = UniqueCreatureAllocation.Baseline(LockedPrimaryOnly, specimenLevel, RealAptitudeTuning);
        var gatePoints = allocation.PointsAt(AllocationScope.UniqueCreature, "Might");

        var tierReached = TierGate.Reached(
            gatePoints, authoredTierCount: 10, reqScalePoints: RealTreeTuning.TierLadder.ReqScalePoints);

        Assert.True(gatePoints > 0, "a levelled specimen must reach a non-zero UniqueCreature aptitude budget");
        Assert.True(tierReached > 0,
            $"a species tree gated on this specimen's own aptitude points must read above tier 0 " +
            $"(gatePoints={gatePoints}, reqScalePoints={RealTreeTuning.TierLadder.ReqScalePoints}) -- " +
            "otherwise a reviewer judging the tree is judging the writing, never the mechanics");
    }

    [Fact]
    public void A_never_levelled_specimens_species_tree_stays_at_tier_zero()
    {
        // The negative half of the same proof: a fresh roster entry (level 1, RpgStore.CreateUniqueActor's
        // own default) must NOT light up a ladder it never earned -- the same "zero means zero, never a
        // ceiling" property CreatureType's baseline already guarantees for species-type budgets.
        const long specimenLevel = 1;

        var allocation = UniqueCreatureAllocation.Baseline(LockedPrimaryOnly, specimenLevel, RealAptitudeTuning);
        Assert.Same(FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Empty, allocation);

        var gatePoints = allocation.PointsAt(AllocationScope.UniqueCreature, "Might");
        var tierReached = TierGate.Reached(
            gatePoints, authoredTierCount: 10, reqScalePoints: RealTreeTuning.TierLadder.ReqScalePoints);

        Assert.Equal(0, gatePoints);
        Assert.Equal(0, tierReached);
    }
}
