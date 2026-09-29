using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.GateCounters;

/// <summary>
/// Task G2 — spec-gate-counters.md §5.1/§5.3/§14: D35 forbids a fifth <c>AllocationScope</c> member and
/// forbids writing an <c>AptitudeAllocation</c> row from a counter; §5.3 closes OQ2 in favour of this
/// module owning <c>gateCounters.statusMasteryRatePoints</c> rather than reading
/// <c>AllocationScope.Aspect</c> / <c>PointBudget.PointsFor(Aspect, ...)</c>. A text guard over the
/// SOURCE rather than a runtime assertion, mirroring the <c>StatusDerivedWiringGuardTests</c> precedent
/// spec-gate-counters.md §12 names for the same reason: the property being protected is "this name never
/// appears in this file", which a behavioural test cannot observe the absence of as directly as a
/// source scan can.
/// </summary>
public class GateCounterBoundaryGuardTests
{
    static readonly string[] GateCounterFiles =
    {
        "src/FusionRpg.Core/PassiveTree/GateCounters/GateCounterKey.cs",
        "src/FusionRpg.Core/PassiveTree/GateCounters/GateCounterAccumulator.cs",
        "src/FusionRpg.Core/PassiveTree/GateCounters/StatusAppliedCounter.cs",
        "src/FusionRpg.Data/Sqlite/RpgStore.GateCounters.cs",
        // Task G3 additions -- same rule, same reason: element_mastery is D35's other half, and the
        // registry is the seam a future aspect-scope producer plugs into (§12), never a scope member.
        "src/FusionRpg.Core/PassiveTree/GateCounters/ElementMasteryCounter.cs",
        "src/FusionRpg.Core/PassiveTree/GateCounters/GateQuantityId.cs",
        "src/FusionRpg.Core/PassiveTree/GateCounters/IGateQuantitySource.cs",
        "src/FusionRpg.Core/PassiveTree/GateCounters/GateQuantityRegistry.cs",
        // Task G4 additions -- the two real producers. Same rule: neither may reference the
        // aptitude-allocation machinery it exists specifically to bypass (§5.1/§5.3/§14, and §5.3's
        // OQ2 closure for ElementMasterySource in particular, which is the one place a "helpful" call
        // into the class system's per-scope budget calculator would be easiest to reach for by
        // mistake). MasteryIndex.cs is deliberately NOT in this list: its own doc comment (spec
        // §9's normative code sample) cites the precedent incident BY FILE NAME in prose -- the exact
        // kind of reference this guard cannot tell apart from a real dependency, since it is a plain
        // substring scan. That file's arithmetic has no code path that could reach the forbidden
        // machinery at all, so the citation is safe and the guard would be a false positive there.
        "src/FusionRpg.Core/PassiveTree/GateCounters/StatusAppliedSource.cs",
        "src/FusionRpg.Core/PassiveTree/GateCounters/ElementMasterySource.cs",
        // Task G5 (D43 / spec-gate-counters.md §16 OQ1) added ExistingSaveSeed.cs and
        // RpgStore.GateCounterSeed.cs, and NEITHER is added here -- for the same reason MasteryIndex.cs
        // above is excluded. ExistingSaveSeed.cs's own doc comment names the aptitude-allocation types
        // in prose to explain why its `SeededCount` takes an already-extracted `long` rather than
        // touching them, which this guard's plain substring scan cannot distinguish from a real
        // dependency; its code has no path that could reach them. RpgStore.GateCounterSeed.cs
        // deliberately DOES read `rpg_aptitude_allocation` (the one-time migration proxy D43 calls
        // for) -- see that file's own doc comment for why that read is not the thing D35/§14 forbid.
        // ExistingSaveSeedTests.cs carries a narrower, precise version of this same check instead
        // (no `using FusionRpg.Core.Stats.Aptitudes`, rather than a blanket substring ban).
    };

    static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CONTRIBUTING.md")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("could not locate repo root from " + Directory.GetCurrentDirectory());
    }

    [Theory]
    [InlineData("AllocationScope")]
    [InlineData("AptitudeAllocation")]
    [InlineData("PointBudget")]
    public void No_gate_counter_file_references_the_aptitude_allocation_machinery(string forbidden)
    {
        var root = RepoRoot();
        foreach (var relativePath in GateCounterFiles)
        {
            var text = File.ReadAllText(Path.Combine(root, relativePath));
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// §5.3's coupling (G2 acceptance bullet 5, spec §11 test 14) is already enforced at
    /// <c>PassiveTreeTuningLoader.Parse</c> (verified by opening the file: it throws
    /// <c>PassiveTreeTuningRejection</c> naming both rate values when they diverge with no
    /// <c>rateDivergenceWhy</c>), and already has an executable test —
    /// <c>PassiveTreeTuningTests.Rejects_diverged_gate_counter_rates_with_no_stated_why</c> plus its
    /// sibling proving a stated <c>rateDivergenceWhy</c> is accepted. This module's own job is only to
    /// prove it reads the ALREADY-validated <see cref="FusionRpg.Core.PassiveTree.State.GateCountersTuning"/>
    /// record correctly, which <see cref="FusionRpg.Core.PassiveTree.State.PassiveTreeTuningTests"/>
    /// already does end to end against the live tuning file
    /// (<c>gk-core/data/tuning/passive-tree.v1.json</c>) -- duplicating that check here would be a second copy
    /// of a rule that already has one enforcement point and one test suite.
    /// </summary>
    [Fact]
    public void The_rate_divergence_refusal_is_already_covered_at_the_loader_level_not_duplicated_here()
    {
        var root = RepoRoot();
        var loaderText = File.ReadAllText(Path.Combine(root,
            "src/FusionRpg.Core/PassiveTree/State/PassiveTreeTuning.cs"));
        Assert.Contains("rateDivergenceWhy", loaderText, StringComparison.Ordinal);
        Assert.Contains("PassiveTreeTuningRejection", loaderText, StringComparison.Ordinal);

        // Located by name under tests/, never by a literal `gk-core/tests/FusionRpg.Core.Tests/...` path: this file
        // moves with the Core split, and a hardcoded source path is the one thing a byte-for-byte move
        // cannot keep working (core-split-apply's own BLOCKS SPLIT finding).
        var testFile = Directory.EnumerateFiles(Path.Combine(root, "tests"), "PassiveTreeTuningTests.cs", SearchOption.AllDirectories)
            .FirstOrDefault(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                              && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
        Assert.NotNull(testFile);
        var testText = File.ReadAllText(testFile!);
        Assert.Contains("elementMasteryRatePoints", testText, StringComparison.Ordinal);
        Assert.Contains("statusMasteryRatePoints", testText, StringComparison.Ordinal);
    }
}
