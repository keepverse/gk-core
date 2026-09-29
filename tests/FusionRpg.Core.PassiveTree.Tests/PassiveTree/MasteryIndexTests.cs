using FusionRpg.Core.PassiveTree.GateCounters;
using FusionRpg.Core.PassiveTree.State;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree;

/// <summary>
/// Task G4 — spec-gate-counters.md §11 tests 1-4, 12. <see cref="MasteryIndex"/> is the square-root
/// transform that turns a raw lifetime counter into an aptitude-point-equivalent index; these tests
/// pin its exact arithmetic against the spec's own worked table (§3.4) rather than re-deriving it, so a
/// change to <c>c</c>, the mastery-curve rate, or <see cref="Core.PassiveTree.Resolve.TierGate"/>'s
/// <c>req(t)</c> scale goes red here first.
/// </summary>
public class MasteryIndexTests
{
    // Mirrors the live gk-core/data/tuning/passive-tree.v1.json gateCounters block (masteryCurveFirstCount=23,
    // masteryCurveStepCount=23) -- PassiveTreeTuningTests.The_live_tuning_file_loads_every_key already
    // proves the live file carries these values; this suite constructs the tuning record directly so a
    // MasteryIndex defect is diagnosed here without a file-load round trip in the way.
    static readonly GateCountersTuning Tuning = new(
        MasteryCurveFirstCount: 23, MasteryCurveStepCount: 23,
        ElementMasteryRatePoints: 4, StatusMasteryRatePoints: 4,
        FlushIntervalMs: 5000, RateDivergenceWhy: null);

    /// <summary>§11 test 1. A day-one save (no history at all) must read index 1 and zero
    /// equivalents -- a non-zero value here would open tier 1 on all 27 counter-backed trees for every
    /// existing save the moment this module ships (§5.3).</summary>
    [Fact]
    public void Index_of_zero_is_one_and_equivalents_are_zero()
    {
        Assert.Equal(1, MasteryIndex.Index(0, Tuning));
        Assert.Equal(0, MasteryIndex.Equivalents(0, ratePoints: 4, Tuning));
    }

    /// <summary>§11 test 2. Exact at every ladder boundary and never off-by-one: one qualifying event
    /// short of <c>CountToReach(m)</c> must still read <c>m-1</c>, and the boundary count itself must
    /// read exactly <c>m</c>. Monotone non-decreasing as the count rises, with no gaps or double-steps.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(14)]
    [InlineData(20)]
    [InlineData(28)]
    [InlineData(36)]
    [InlineData(46)]
    [InlineData(58)]
    [InlineData(70)]
    public void Index_is_monotone_and_exact_at_every_ladder_boundary(long index)
    {
        var boundary = CountToReach(index);
        Assert.Equal(index, MasteryIndex.Index(boundary, Tuning));
        if (boundary > 0)
            Assert.Equal(index - 1, MasteryIndex.Index(boundary - 1, Tuning));
    }

    /// <summary>The spec's own §3.4 calibration table, reproduced exactly -- these are the numbers
    /// <c>c = 23</c> was rounded up to hit. A survivor that flips `&lt;=` to `&lt;` in the search
    /// predicate, or drops the `-1` in <see cref="MasteryIndex.Equivalents"/>, changes at least one of
    /// these rows.</summary>
    [Theory]
    [InlineData(1, 3, 69)]
    [InlineData(2, 5, 230)]
    [InlineData(3, 9, 828)]
    [InlineData(4, 14, 2093)]
    [InlineData(5, 20, 4370)]
    [InlineData(6, 28, 8694)]
    [InlineData(7, 36, 14490)]
    [InlineData(8, 46, 23805)]
    [InlineData(9, 58, 38019)]
    [InlineData(10, 70, 55545)]
    public void Calibration_table_matches_the_spec_worked_example(int tier, long expectedIndex, long expectedCount)
    {
        Assert.Equal(expectedCount, CountToReach(expectedIndex));
        Assert.Equal(expectedIndex, MasteryIndex.Index(expectedCount, Tuning));
        _ = tier; // documents which req(t) row this pins; TierParityTests below reads req(t) itself.
    }

    static long CountToReach(long index) => MasteryIndex.CountToReach(index, Tuning);

    /// <summary>§11 test 4 -- the overflow a naive doubling search has. A count at (and near)
    /// <c>long.MaxValue</c> must resolve without throwing, and the result must be internally
    /// consistent: the boundary the search settled on actually reaches the count, and one qualifying
    /// event less does not.</summary>
    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MaxValue - 1)]
    [InlineData(long.MaxValue / 2)]
    [InlineData(long.MaxValue / 3)]
    public void Index_search_survives_a_count_at_long_MaxValue(long count)
    {
        var index = MasteryIndex.Index(count, Tuning);

        Assert.True(index >= 1);
        Assert.True(CountToReach(index) <= count);
        // CountToReach(index + 1) either exceeds `count` (the ordinary case) or itself overflows --
        // both are acceptable outcomes this close to the ceiling; what matters is `index` never
        // overstates what `count` actually reached.
        try
        {
            Assert.True(CountToReach(index + 1) > count);
        }
        catch (OverflowException)
        {
            // CountToReach(index + 1) itself overflowed long -- consistent with `index` being the
            // true maximum reachable index for this count, since a real index one higher would need
            // a count beyond long's range entirely.
        }

        // Equivalents must not throw either, for a realistic rate.
        var equivalents = MasteryIndex.Equivalents(count, ratePoints: 4, Tuning);
        Assert.Equal((index - 1) * 4, equivalents);
    }

    [Fact]
    public void A_negative_count_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MasteryIndex.Index(-1, Tuning));
    }

    // spec-gate-counters.md §3.3's two anchors, cited not re-derived: `a_focus = 1.625` (3 x 0.54163,
    // D29/D38's corner share) and `A = 1.875` (qualifying events per Theta^2, the worked anchor). Both
    // are the inputs `c` was solved FROM (§3.3's algebra), never a private re-fit.
    const double AFocus = 1.625;
    const double A = 1.875;
    const long ReqScalePoints = 5; // tierLadder.reqScalePoints -- live tuning file + TierGate.Reached.
    const long Rate = 4;           // gateCounters.*MasteryRatePoints -- live tuning file.

    /// <summary>
    /// The exact §3.4 parity ratio for one tier: `req(t)/a_focus` (the Θ at which a FOCUSED primary
    /// tree opens tier `t`) against `sqrt(c·n·(n+1) / 2A)` (the Θ a counter-backed tree implies at the
    /// same tier, using the EXACT triangular count rather than the large-n approximation the spec's own
    /// prose writes as `n·sqrt(c/2A)` -- the exact form is what reproduces the table's published
    /// numbers to the decimal, verified against every row in
    /// <see cref="Calibration_table_matches_the_spec_worked_example"/>).
    ///
    /// <para>Double-precision VERIFICATION arithmetic -- <see cref="MasteryIndex"/> itself (the shipped
    /// code this checks) is integer arithmetic; Θ here is a small design quantity (low hundreds), well
    /// within a double's exact-integer range.</para>
    /// </summary>
    static (double Ratio, long Equivalents) ParityAt(int t)
    {
        var req = ReqScalePoints * (long)t * (t + 1) / 2;
        var n = (long)Math.Ceiling(req / (double)Rate);

        var thetaApt = req / AFocus;
        var thetaCounter = Math.Sqrt(Tuning.MasteryCurveFirstCount * (double)n * (n + 1) / (2.0 * A));

        var equivalents = MasteryIndex.Equivalents(CountToReach(n + 1), Rate, Tuning);
        return (thetaCounter / thetaApt, equivalents);
    }

    /// <summary>
    /// §11 test 12 / success criterion 3's named case: tier 10, the deepest tier the primary-tree
    /// catalog authors today. This is the tier the whole calibration in §3.3 was solved to fit, and it
    /// holds tight (≈1.7%, verified) -- "red the moment c, r or s moves alone" (§11 test 12's own
    /// description).
    /// </summary>
    [Fact]
    public void Tier_10_opens_within_five_percent_of_the_primary_tree_Theta()
    {
        var (ratio, equivalents) = ParityAt(10);
        Assert.InRange(ratio, 0.95, 1.05);

        var reached = FusionRpg.Core.PassiveTree.Resolve.TierGate.Reached(equivalents, authoredTierCount: 10, ReqScalePoints);
        Assert.Equal(10, reached);
    }

    /// <summary>
    /// §3.4's full table, recomputed to full precision rather than at the table's own 1-decimal-place
    /// display. <b>Honest finding, reported rather than papered over:</b> §3.4's prose says the ratio
    /// is "inside 5% from tier 4 on," but at full precision tier 4 is ≈8.6% and tier 6 is ≈5.4% -- both
    /// outside a strict 5% band even though their rounded display (1.09, 1.05) reads as borderline.
    /// Only tiers 7-10 hold consistently under 3%. This test asserts what the shipped constants
    /// actually deliver -- comfortably inside 10% from tier 4, and tight (≤5%) from tier 7 -- rather
    /// than restate the tighter tier-4 claim the numbers do not, in fact, support. (Flagged for the
    /// spec owner in the task report rather than silently "corrected" here.)
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void Parity_ratio_is_close_from_tier_four_up_and_tight_from_tier_seven(int t)
    {
        var (ratio, equivalents) = ParityAt(t);

        Assert.InRange(ratio, 0.90, 1.10);
        if (t >= 7)
            Assert.InRange(ratio, 0.95, 1.05);

        // Ties the abstract ratio to the actual gate: the equivalents this tier's own boundary count
        // produces must open exactly tier `t` on the real TierGate -- not t-1, not t+1.
        var reached = FusionRpg.Core.PassiveTree.Resolve.TierGate.Reached(equivalents, authoredTierCount: 10, ReqScalePoints);
        Assert.Equal(t, reached);
    }

    static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CONTRIBUTING.md")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("could not locate repo root from " + Directory.GetCurrentDirectory());
    }
}
