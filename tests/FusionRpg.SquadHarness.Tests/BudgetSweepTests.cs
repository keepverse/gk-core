using FusionRpg.Tools.SquadHarness;
using Xunit;

namespace FusionRpg.SquadHarness.Tests;

/// <summary>
/// spec-squad-harness.md §11 S4 (todo "F6: S4 -- the budget mode, and D42's two dials"). Two layers,
/// matching <see cref="SoulTrackSweepTests"/>'s/<see cref="ErosionTests"/>'s own split: the refusal-only
/// unit tests below need no <c>BattleEngine</c> call, and the small live layer proves the mechanism (a
/// real corner-vs-spread measurement at duel scope, folding <see cref="TreeModel"/> in, a real doc-11-
/// section6b-style spread reproduction, and the two dial refusals) at trivial trial counts -- this
/// session's heavy concurrent machine load made a real 3,000/40,000-trial production sweep impractical
/// to run live (the same disclosed gap F2/F3/F4/F5 already carry), so no production balance finding is
/// produced here.
/// </summary>
public class BudgetSweepTests
{

    static IReadOnlyList<NamedBuild> DuelRoster()
    {
        return SquadRoster.Duels();
    }

    // ---- Run: refusals -----------------------------------------------------------------------------

    [Fact]
    public void Run_refuses_a_null_bValues_list()
    {
        var roster = DuelRoster();
        var spec = new RunSpec(100, 1, 20260906);
        Assert.Throws<ArgumentException>(() => BudgetSweep.Run(spec, roster, null!, 1200, 500, null, false));
    }

    [Fact]
    public void Run_refuses_a_single_value_bValues_list()
    {
        var roster = DuelRoster();
        var spec = new RunSpec(100, 1, 20260906);
        Assert.Throws<ArgumentException>(() => BudgetSweep.Run(spec, roster, new long[] { 5 }, 1200, 500, null, false));
    }

    [Fact]
    public void Run_refuses_a_bValues_list_that_collapses_to_one_distinct_value()
    {
        // 5,5 has two entries but only one DISTINCT value -- still not a finite difference.
        var roster = DuelRoster();
        var spec = new RunSpec(100, 1, 20260906);
        Assert.Throws<ArgumentException>(() => BudgetSweep.Run(spec, roster, new long[] { 5, 5 }, 1200, 500, null, false));
    }

    [Fact]
    public void Run_refuses_a_duel_roster_missing_the_even12_spread_baseline()
    {
        var spec = new RunSpec(100, 1, 20260906);
        var withoutSpread = DuelRoster().Where(b => b.Id != "even12").ToList();
        Assert.Throws<ArgumentException>(() => BudgetSweep.Run(spec, withoutSpread, new long[] { 5, 6 }, 1200, 500, null, false));
    }

    [Fact]
    public void Run_refuses_a_duel_roster_with_no_corner_builds()
    {
        var spec = new RunSpec(100, 1, 20260906);
        var onlySpread = DuelRoster().Where(b => b.Id == "even12").ToList();
        Assert.Throws<ArgumentException>(() => BudgetSweep.Run(spec, onlySpread, new long[] { 5, 6 }, 1200, 500, null, false));
    }

    // ---- Run: the shape of a real sweep -------------------------------------------------------------

    [Fact]
    public void Run_produces_one_cell_per_b_value_per_corner()
    {
        var roster = DuelRoster();
        var spec = new RunSpec(100, 1, 20260906);
        var result = BudgetSweep.Run(spec, roster, new long[] { 5, 6, 7 }, 1200, 500, null, false);

        var cornerCount = roster.Count(b => b.Kind == "corner");
        Assert.Equal(3 * cornerCount, result.Cells.Count);
        foreach (var cell in result.Cells)
        {
            Assert.InRange(cell.CornerVsSpreadWinShareMilli, 0, 1000);
            Assert.True(cell.HalfWidthMilli > 0);
        }
    }

    [Fact]
    public void Run_produces_one_marginal_row_per_adjacent_b_pair_per_corner()
    {
        var roster = DuelRoster();
        var spec = new RunSpec(100, 1, 20260906);
        var result = BudgetSweep.Run(spec, roster, new long[] { 5, 6, 7 }, 1200, 500, null, false);

        var cornerCount = roster.Count(b => b.Kind == "corner");
        // 3 b-values -> 2 adjacent pairs per corner.
        Assert.Equal(2 * cornerCount, result.Marginals.Count);
        foreach (var m in result.Marginals)
        {
            Assert.Equal(m.HighB - m.LowB, m.DeltaB);
            Assert.Equal(m.HighWinShareMilli - m.LowWinShareMilli, m.DeltaWinShareMilli);
            Assert.True(m.HalfWidthMilli > 0);
        }
    }

    [Fact]
    public void Run_dedupes_and_sorts_bValues_before_sweeping()
    {
        var roster = DuelRoster();
        var spec = new RunSpec(100, 1, 20260906);
        var result = BudgetSweep.Run(spec, roster, new long[] { 7, 5, 7, 6 }, 1200, 500, null, false);
        Assert.Equal(new long[] { 5, 6, 7 }, result.BValues);
    }

    [Fact]
    public void Run_reuses_the_same_RunSeed_regardless_of_which_other_b_values_are_swept_alongside()
    {
        // Common-random-numbers property (spec §7), applied to this mode's own axis: a (b, corner) cell
        // measured inside a wide sweep must be byte-identical to the same cell measured alone.
        var roster = DuelRoster();
        var spec = new RunSpec(100, 3, 20260906);

        var wide = BudgetSweep.Run(spec, roster, new long[] { 5, 6, 7 }, 1200, 500, null, false);
        var narrow = BudgetSweep.Run(spec, roster, new long[] { 5, 6 }, 1200, 500, null, false);

        var wideCell = wide.Cells.Single(c => c.B == 5 && c.CornerId == "Might");
        var narrowCell = narrow.Cells.Single(c => c.B == 5 && c.CornerId == "Might");
        Assert.Equal(narrowCell.CornerVsSpreadWinShareMilli, wideCell.CornerVsSpreadWinShareMilli);
        Assert.Equal(narrowCell.HalfWidthMilli, wideCell.HalfWidthMilli);
    }

    [Fact]
    public void CornerSpread_is_reported_at_the_lowest_swept_b_value()
    {
        var roster = DuelRoster();
        var spec = new RunSpec(100, 2, 20260906);
        var result = BudgetSweep.Run(spec, roster, new long[] { 9, 5, 7 }, 1200, 500, null, false);

        Assert.Equal(5L, result.CornerSpreadLowestCornerB);
        var atLowestB = result.Cells.Where(c => c.B == 5).Select(c => c.CornerVsSpreadWinShareMilli).ToList();
        Assert.Equal(atLowestB.Min(), result.CornerSpreadLowMilli);
        Assert.Equal(atLowestB.Max(), result.CornerSpreadHighMilli);
        Assert.True(result.CornerSpreadLowMilli <= result.CornerSpreadHighMilli);
    }

    // ---- the two dial proposals: ALWAYS an honest, named refusal, never a fabricated number ----------

    [Fact]
    public void ProposeTreeTotalPoints_is_always_unresolved_and_names_the_plan_authoring_gap()
    {
        var proposed = BudgetSweep.ProposeTreeTotalPoints();
        Assert.False(proposed.Resolved);
        Assert.Equal(0L, proposed.ValueMilli);
        Assert.Equal(1000L, proposed.HalfWidthMilli);
        Assert.Contains("treeTotalPoints", proposed.WhyNot, StringComparison.Ordinal);
        Assert.Contains("plan/emit.py", proposed.WhyNot, StringComparison.Ordinal);
    }

    [Fact]
    public void ProposeTreeShareMilli_is_always_unresolved_and_names_the_catalog_bake_gap()
    {
        var proposed = BudgetSweep.ProposeTreeShareMilli();
        Assert.False(proposed.Resolved);
        Assert.Equal(0L, proposed.ValueMilli);
        Assert.Equal(1000L, proposed.HalfWidthMilli);
        Assert.Contains("CoefficientBinder", proposed.WhyNot, StringComparison.Ordinal);
        Assert.Contains("kMicro", proposed.WhyNot, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_carries_both_refusals_through_to_its_own_result()
    {
        var roster = DuelRoster();
        var spec = new RunSpec(100, 1, 20260906);
        var result = BudgetSweep.Run(spec, roster, new long[] { 5, 6 }, 1200, 500, null, false);

        Assert.False(result.ProposedTreeTotalPoints.Resolved);
        Assert.False(result.ProposedTreeShareMilli.Resolved);
    }

    // ---- artifact writer: propose only, never touches gk-core/data/tuning -----------------------------------

    [Fact]
    public void WriteArtifact_never_writes_to_data_tuning()
    {
        var roster = DuelRoster();
        var spec = new RunSpec(100, 1, 20260906);
        var result = BudgetSweep.Run(spec, roster, new long[] { 5, 6 }, 1200, 500, null, false);

        var tuningDir = Path.Combine(TuningBootstrap.FindRepoRoot(), "data", "tuning");
        var before = Directory.GetFiles(tuningDir).Select(File.GetLastWriteTimeUtc).ToList();

        var path = Path.Combine(Path.GetTempPath(), $"budget-test-{Guid.NewGuid():N}.json");
        try
        {
            BudgetSweep.WriteArtifact(result, path);
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
    public void WriteArtifact_reports_both_proposed_dials_with_their_own_whyNot()
    {
        var roster = DuelRoster();
        var spec = new RunSpec(100, 1, 20260906);
        var result = BudgetSweep.Run(spec, roster, new long[] { 5, 6 }, 1200, 500, null, false);

        var path = Path.Combine(Path.GetTempPath(), $"budget-test-{Guid.NewGuid():N}.json");
        try
        {
            var json = BudgetSweep.WriteArtifact(result, path);
            Assert.Contains("\"treeTotalPoints\"", json, StringComparison.Ordinal);
            Assert.Contains("\"treeShareMilli\"", json, StringComparison.Ordinal);
            Assert.Contains("\"resolved\": false", json, StringComparison.Ordinal);
            Assert.Contains("\"whyNot\"", json, StringComparison.Ordinal);
            Assert.Contains("\"marginals\"", json, StringComparison.Ordinal);
            Assert.Contains("\"cornerSpreadAtLowestB\"", json, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---- TreeModel.ApplyTreeModel(NamedBuild, ...) overload ------------------------------------------

    [Fact]
    public void ApplyTreeModel_on_a_NamedBuild_produces_a_single_actor_RosterEntry()
    {
        var roster = DuelRoster();
        var corner = roster.First(b => b.Kind == "corner");
        var entry = TreeModel.ApplyTreeModel(corner, theta: 100, fmaxMilli: 1200, wMilli: 500, b: 5,
            includeOwnershipCost: true, TreeModel.CreditRule.Largest);

        Assert.Equal(corner.Id, entry.Id);
        Assert.Single(entry.Actors);
    }

    [Fact]
    public void ApplyTreeModel_on_a_NamedBuild_matches_a_direct_Resolve_call()
    {
        // AptitudeAllocation is a sealed class with no value equality, so this compares the SAME way
        // TreeModel's own SquadBuild-scoped tests do: per-aptitude Total(), over the full roster, never
        // a reference-equality Assert.Equal on the allocation itself.
        var roster = DuelRoster();
        var corner = roster.First(b => b.Kind == "corner");
        var entry = TreeModel.ApplyTreeModel(corner, theta: 100, fmaxMilli: 1200, wMilli: 500, b: 5,
            includeOwnershipCost: true, TreeModel.CreditRule.Largest);
        var direct = TreeModel.Resolve(corner.Allocation, theta: 100, fmaxMilli: 1200, wMilli: 500, b: 5,
            includeOwnershipCost: true, TreeModel.CreditRule.Largest);

        foreach (var aptId in BuildFactory.Roster)
            Assert.Equal(direct.EffectiveAllocation.Total(aptId), entry.Actors[0].Total(aptId));
    }

    // ---- dedicated determinism coverage (erosion/budget are NOT rows in MeasurementModes.All --------
    // MeasurementModesTests's own doc comment names this: both take a required sweep axis with no
    // default that a shared fixed-default table row would have to invent).

    [Fact]
    public void Run_repeats_byte_identically_in_two_invocations()
    {
        var roster = DuelRoster();
        var spec = new RunSpec(100, 2, 20260906);
        var first = BudgetSweep.Run(spec, roster, new long[] { 5, 6 }, 1200, 500, null, false);
        var second = BudgetSweep.Run(spec, roster, new long[] { 5, 6 }, 1200, 500, null, false);

        Assert.Equal(first.Cells, second.Cells);
        Assert.Equal(first.Marginals, second.Marginals);
        Assert.Equal(first.CornerSpreadLowMilli, second.CornerSpreadLowMilli);
        Assert.Equal(first.CornerSpreadHighMilli, second.CornerSpreadHighMilli);
    }
}
