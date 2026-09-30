using FusionRpg.Core.PassiveTree.GateCounters;
using FusionRpg.Core.PassiveTree.State;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.PassiveTree.GateCounters;

/// <summary>Task G5 — D43 / spec-gate-counters.md §16 OQ1's pure math (<see cref="ExistingSaveSeed"/>).
/// The store-level orchestration (the stamp, the "never clobber a real row" rule) is covered by
/// <c>gk-core/tests/FusionRpg.Data.Tests/GateCounterSeedTests.cs</c> against a real <c>RpgStore</c>; this suite
/// only proves the proxy formula itself.</summary>
public class ExistingSaveSeedTests
{
    static readonly GateCountersTuning Tuning = new(
        MasteryCurveFirstCount: 23, MasteryCurveStepCount: 23,
        ElementMasteryRatePoints: 4, StatusMasteryRatePoints: 4,
        FlushIntervalMs: 5000, RateDivergenceWhy: null);

    [Fact]
    public void A_fresh_account_with_zero_commander_points_seeds_zero()
    {
        Assert.Equal(0L, ExistingSaveSeed.SeededCount(0, ratePoints: 4, Tuning));
    }

    [Fact]
    public void A_deep_primary_tree_seeds_a_non_zero_count()
    {
        // A level-169-ish focused build's own worked example from spec §3.3: 275 commander points
        // is roughly what req(10) implies for a_focus*Theta. Any value well above one rate step
        // must seed something, or "proportionate to primary-tree depth" would be false on its face.
        var seeded = ExistingSaveSeed.SeededCount(275, ratePoints: 4, Tuning);
        Assert.True(seeded > 0, $"expected a non-zero seed for a deep primary tree, got {seeded}");
    }

    [Fact]
    public void Seeded_count_never_decreases_as_commander_points_grow()
    {
        long previous = 0;
        foreach (var points in new long[] { 0, 4, 8, 23, 50, 120, 275, 1000, 10_000 })
        {
            var seeded = ExistingSaveSeed.SeededCount(points, ratePoints: 4, Tuning);
            Assert.True(seeded >= previous,
                $"commander points {points} seeded {seeded}, less than the previous {previous}");
            previous = seeded;
        }
    }

    [Fact]
    public void Seeded_equivalents_never_exceed_the_commander_points_they_were_derived_from()
    {
        // The whole point of flooring the index (spec §3.4's "errs strict"): a seeded tree must never
        // read AHEAD of the primary-tree investment that justified seeding it at all.
        foreach (var points in new long[] { 1, 4, 5, 23, 99, 275, 12_345, 1_000_000 })
        {
            var seeded = ExistingSaveSeed.SeededCount(points, ratePoints: 4, Tuning);
            var equivalents = MasteryIndex.Equivalents(seeded, ratePoints: 4, Tuning);
            Assert.True(equivalents <= points,
                $"commander points {points} seeded equivalents {equivalents}, which overshoots it");
        }
    }

    [Fact]
    public void A_lower_rate_seeds_at_least_as_much_as_a_higher_rate_for_the_same_points()
    {
        // element and status ratePoints default equal (4/4) but the formula must hold even if a
        // deliberately-diverged rate (rateDivergenceWhy set) is in force -- a cheaper rate (fewer
        // points needed per index step) must never seed LESS than a pricier one for the same input.
        var cheap = ExistingSaveSeed.SeededCount(500, ratePoints: 2, Tuning);
        var pricey = ExistingSaveSeed.SeededCount(500, ratePoints: 8, Tuning);
        Assert.True(cheap >= pricey, $"cheap-rate seed {cheap} should be >= pricey-rate seed {pricey}");
    }

    [Fact]
    public void Never_imports_the_aptitude_allocation_namespace()
    {
        // Same D35 rule GateCounterBoundaryGuardTests already enforces for the rest of this
        // namespace, checked here too since this file exists specifically BECAUSE of the aptitude
        // allocation machinery and is the one most tempted to reach for it directly. The doc comment's
        // own prose names AllocationScope/AptitudeAllocation/PointBudget in backticks to explain why
        // they are absent, so a plain substring ban (like the stricter production guard uses) would
        // false-positive on the comment -- checking for the one `using` that would make them reachable
        // is the precise version of the same rule.
        var root = ExistingSaveSeedTestsRepoRoot();
        var text = File.ReadAllText(Path.Combine(root,
            "src/FusionRpg.Core/PassiveTree/GateCounters/ExistingSaveSeed.cs"));
        Assert.DoesNotContain("using FusionRpg.Core.Stats.Aptitudes", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_negative_commander_total()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExistingSaveSeed.SeededCount(-1, ratePoints: 4, Tuning));
    }

    [Fact]
    public void Rejects_a_non_positive_rate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExistingSaveSeed.SeededCount(100, ratePoints: 0, Tuning));
    }

    [Fact]
    public void Rejects_a_null_tuning()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ExistingSaveSeed.SeededCount(100, ratePoints: 4, null!));
    }

    static string ExistingSaveSeedTestsRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
