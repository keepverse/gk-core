using FusionRpg.Core.PassiveTree.GateCounters;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.GateCounters;

/// <summary>
/// Task G4 — spec-gate-counters.md §11 test 9 / success criterion 4: D35's invariant, made executable
/// rather than argued. spec-gate-counters.md §10's project-structure table names this test
/// <c>tests/FusionRpg.Guard.Tests/GateCounterAllocationGuardTests.cs</c>, but
/// <c>FusionRpg.Guard.Tests.csproj</c> carries NO <c>ProjectReference</c> at all (verified by opening
/// the file) — every existing guard test in that project is a pure text scan over source files, by
/// design, and none of them can call a real <c>AptitudeAllocation</c> or <c>GateQuantityRegistry</c>.
/// A behavioural proof needs to run somewhere that can actually construct and call those types, so it
/// lives here in <c>FusionRpg.Core.Tests</c> (which already references Core) instead — the SAME
/// text-guard half of the split (no gate-counter file references the aptitude-allocation machinery) is
/// already covered by <see cref="GateCounterBoundaryGuardTests"/>, extended for task G4's two new files.
/// </summary>
public class GateCounterAllocationInvarianceTests
{
    /// <summary>
    /// Build a real, independent <see cref="AptitudeAllocation"/>, snapshot its <c>GrandTotal</c> and
    /// every <c>Share</c>, then run the real production sources through a REAL
    /// <see cref="GateQuantityRegistry"/> for dozens of ever-increasing counter credits — and prove the
    /// allocation snapshot never moved. Nothing in the gate-counter path holds a reference to the
    /// allocation at all, so this is provable rather than merely likely; the test exists so that fact is
    /// asserted, not assumed.
    /// </summary>
    [Fact]
    public void Crediting_gate_counters_any_number_of_times_never_moves_GrandTotal_or_any_Share()
    {
        var allocation =
            AptitudeAllocation.Single(AllocationScope.Commander, "Might", 30) +
            AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 10) +
            AptitudeAllocation.Single(AllocationScope.Aspect, "Focus", 5) +
            AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Onslaught", 70);

        var grandTotalBefore = allocation.GrandTotal();
        var sharesBefore = allocation.Shares();

        var tuning = new GateCountersTuning(
            MasteryCurveFirstCount: 23, MasteryCurveStepCount: 23,
            ElementMasteryRatePoints: 4, StatusMasteryRatePoints: 4,
            FlushIntervalMs: 5000, RateDivergenceWhy: null);

        var counts = new Dictionary<(GateOwnerKey Owner, string Quantity, string SubjectId), long>();
        long ReadCount(GateOwnerKey owner, string quantity, string subjectId) =>
            counts.GetValueOrDefault((owner, quantity, subjectId));

        var registry = new GateQuantityRegistry();
        var statusSource = new StatusAppliedSource(
            (owner, subjectId) => ReadCount(owner, StatusAppliedCounter.Quantity, subjectId), tuning);
        var elementSource = new ElementMasterySource(
            (owner, subjectId) => ReadCount(owner, ElementMasteryCounter.Quantity, subjectId), tuning);
        registry.Register(statusSource);
        registry.Register(elementSource);

        var owner = new GateOwnerKey("player", "player:1");
        var actor = new GateActorContext(owner);

        for (var i = 0; i < 200; i++)
        {
            counts[(owner, StatusAppliedCounter.Quantity, "wither")] = i * 37L;
            counts[(owner, ElementMasteryCounter.Quantity, "fire")] = i * 41L;

            _ = registry.AptitudePointEquivalents(new GateQuantityId(StatusAppliedCounter.Quantity, "wither"), actor);
            _ = registry.AptitudePointEquivalents(new GateQuantityId(ElementMasteryCounter.Quantity, "fire"), actor);

            // Assert INSIDE the loop, not only at the end -- a transient move that self-corrects would
            // still be a real bug D35 forbids.
            Assert.Equal(grandTotalBefore, allocation.GrandTotal());
            Assert.Equal(sharesBefore, allocation.Shares());
        }
    }
}
