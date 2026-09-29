using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Effects.Atoms.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Rungs;

/// <summary>
/// ST4.2 (`spec-budget-calibration-report.md` contracts 2 and 6, spec tests 1–3, 6, 9). The reading
/// inverts the published budget derivation, so nothing here pins a percentile or a scalar: every
/// expectation is either the derivation's own arithmetic or a property the rule must have.
/// </summary>
public class BudgetCalibrationTests
{
    static RungRow Row(int rung, int poolRolls, int qPowerMilli, long? budget = null) =>
        new(rung, MinTier: 1, MaxTier: 5, poolRolls, qPowerMilli, CostMulti: 1000, CdMulti: 1000,
            StructureBudget: Array.Empty<string>(), PowerBudgetMilli: budget);

    /// <summary>`rows` must be contiguous from rung 1 — <see cref="RungTable"/> indexes by rung.</summary>
    static RungTable Table(params RungRow[] rows) => new(rows.Length, rows);

    /// <summary>The published derivation, mirrored (the same test-only mirror `RungPowerBudgetTests`
    /// already uses): `poolRolls × referencePower × qPowerMilli / 1000`, widened, divided once, last.</summary>
    static long BudgetAt(RungRow row, long referencePower) =>
        checked((long)row.PoolRolls * referencePower * row.QPowerMilli / 1000);

    /// <summary>Contract 6's rule as the test states it, independently of the implementation: the
    /// smallest scalar at which this action stops exceeding its rung's budget.</summary>
    static long CeilingFor(long realizedPowerMilli, RungRow row)
    {
        var divisor = checked((long)row.PoolRolls * row.QPowerMilli);
        return checked((checked(realizedPowerMilli * PowerMath.One) + divisor - 1) / divisor);
    }

    [Fact]
    public void A_priced_action_that_sits_exactly_on_budget_at_the_reference_reads_that_reference()
    {
        // Spec test 1: the inversion matches the published derivation. The fixture is chosen so the
        // division is EXACT (poolRolls x qPowerMilli x REF is a whole multiple of 1000) -- the reading
        // is floor division, so an inexact fixture would prove the rounding rather than the inversion.
        const long reference = 500;
        const int poolRolls = 2, qPowerMilli = 1323;                 // 2 x 1323 x 500 / 1000 = 1323 exact
        var row = Row(1, poolRolls, qPowerMilli, BudgetAt(Row(1, poolRolls, qPowerMilli), reference));
        var table = Table(row);
        var budget = row.PowerBudgetMilli!.Value;

        var report = BudgetCalibration.Read(new[] { new PricedAction("action.on-budget", 1, budget) }, table);

        var rung = Assert.Single(report.Rungs);
        Assert.Equal(1, rung.Count);
        Assert.Equal(reference, rung.Min);
        Assert.Equal(reference, rung.P50);
        Assert.Equal(reference, rung.P90);
        Assert.Equal(reference, rung.Max);
    }

    [Fact]
    public void The_per_rung_counts_reconcile_with_the_number_of_priced_actions_and_no_id_repeats()
    {
        // Spec test 2: reconciliation, not a count.
        var table = Table(Row(1, 1, 1000, 1000), Row(2, 1, 1323, 1323), Row(3, 2, 1750, 3500));
        var actions = new[]
        {
            new PricedAction("action.a", 1, 400),
            new PricedAction("action.b", 1, 900),
            new PricedAction("action.c", 2, 700),
            new PricedAction("action.d", 3, 4000),
        };

        var report = BudgetCalibration.Read(actions, table);

        Assert.Equal(actions.Length, report.PricedActionCount);
        Assert.Equal(actions.Length, report.Rungs.Sum(r => r.Count));
        var countedOnce = report.Rungs.SelectMany(r => r.ActionsAboveLoadedScalar).ToList();
        Assert.Equal(countedOnce.Count, countedOnce.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_reading_is_ordered_and_a_rung_with_no_content_reports_no_percentiles()
    {
        // Spec test 3, both halves. Rung 2 deliberately has no action.
        var table = Table(Row(1, 1, 1000, 1000), Row(2, 1, 1323, 1323));
        var actions = new[]
        {
            new PricedAction("action.low", 1, 300),
            new PricedAction("action.mid", 1, 600),
            new PricedAction("action.high", 1, 900),
        };

        var report = BudgetCalibration.Read(actions, table);

        foreach (var rung in report.Rungs.Where(r => r.Count > 0))
        {
            Assert.NotNull(rung.Min);
            Assert.NotNull(rung.P50);
            Assert.NotNull(rung.P90);
            Assert.NotNull(rung.Max);
            Assert.True(rung.Min <= rung.P50, $"rung {rung.Rung}: min {rung.Min} > p50 {rung.P50}");
            Assert.True(rung.P50 <= rung.P90, $"rung {rung.Rung}: p50 {rung.P50} > p90 {rung.P90}");
            Assert.True(rung.P90 <= rung.Max, $"rung {rung.Rung}: p90 {rung.P90} > max {rung.Max}");
        }

        var empty = Assert.Single(report.Rungs, r => r.Rung == 2);
        Assert.Equal(0, empty.Count);
        Assert.Null(empty.Min);
        Assert.Null(empty.P50);
        Assert.Null(empty.P90);
        Assert.Null(empty.Max);
        Assert.Empty(empty.ActionsAboveLoadedScalar);
        Assert.Empty(empty.ActionsAboveP90);
    }

    [Fact]
    public void An_action_the_budget_rejects_is_named_by_the_report()
    {
        // Spec test 4, at the report's own level: the ids above the LOADED table's implied scalar are
        // exactly the ones the check would reject at that table, and a table with no budget column
        // (pre-A-G1) has nothing above it rather than guessing.
        var table = Table(Row(1, 1, 1000, budget: 1000));
        var actions = new[]
        {
            new PricedAction("action.kept", 1, 900),
            new PricedAction("action.rejected", 1, 1500),
        };

        var report = BudgetCalibration.Read(actions, table);
        var rung = Assert.Single(report.Rungs);
        Assert.Equal(new[] { "action.rejected" }, rung.ActionsAboveLoadedScalar);

        var unbudgeted = Table(Row(1, 1, 1000));
        var withoutColumn = Assert.Single(BudgetCalibration.Read(actions, unbudgeted).Rungs);
        Assert.Empty(withoutColumn.ActionsAboveLoadedScalar);
    }

    [Fact]
    public void The_arithmetic_throws_on_long_overflow_and_refuses_a_zero_divisor()
    {
        // Spec test 6. Overflow: widening first is not enough when the value itself is absurd.
        var huge = Table(Row(1, 1, 1000, 1000));
        var overflowing = new[] { new PricedAction("action.huge", 1, long.MaxValue / 2) };
        Assert.Throws<OverflowException>(() => BudgetCalibration.Read(overflowing, huge));

        // A zero factor has no reading, and the message names the rung rather than dividing by zero.
        var zeroed = Table(Row(1, 0, 1000, 1000));
        var err = Assert.Throws<InvalidOperationException>(
            () => BudgetCalibration.Read(new[] { new PricedAction("action.any", 1, 10) }, zeroed));
        Assert.Contains("rung 1", err.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Repricing_at_the_recommendation_rejects_nothing_and_one_below_it_rejects_the_max()
    {
        // Spec test 9: a property of the rule, never a pinned value. "Rejects" is stated as the budget
        // check states it -- a repriced budget below the action's realized power.
        var table = Table(Row(1, 1, 1000, 1000), Row(2, 1, 1323, 1323));
        var actions = new[]
        {
            new PricedAction("action.a", 1, 900),
            new PricedAction("action.b", 1, 300),
            new PricedAction("action.c", 2, 2500),
        };
        var rowOf = new Dictionary<int, RungRow> { [1] = table.Rows[0], [2] = table.Rows[1] };

        var report = BudgetCalibration.Read(actions, table);
        var recommended = report.RecommendedReferencePower;
        Assert.NotNull(recommended);
        Assert.Equal(
            actions.Max(a => CeilingFor(a.RealizedPowerMilli, rowOf[a.AuthoredRung])),
            recommended!.Value);

        foreach (var action in actions)
        {
            var row = rowOf[action.AuthoredRung];
            Assert.True(BudgetAt(row, recommended.Value) >= action.RealizedPowerMilli,
                        $"'{action.ActionId}' is still rejected at the recommended scalar");
        }

        var maxSetter = actions.OrderByDescending(
            a => CeilingFor(a.RealizedPowerMilli, rowOf[a.AuthoredRung])).First();
        var maxRow = rowOf[maxSetter.AuthoredRung];
        Assert.True(BudgetAt(maxRow, recommended.Value - 1) < maxSetter.RealizedPowerMilli,
                    "one below the recommendation must reject the action that set the max");
    }

    [Fact]
    public void An_empty_catalog_reports_every_rung_empty_and_no_recommendation()
    {
        var table = Table(Row(1, 1, 1000, 1000));

        var report = BudgetCalibration.Read(Array.Empty<PricedAction>(), table);

        Assert.Equal(0, report.PricedActionCount);
        Assert.Null(report.RecommendedReferencePower);
        Assert.Empty(report.RecommendedBy);
        var rung = Assert.Single(report.Rungs);
        Assert.Equal(0, rung.Count);
        Assert.Null(rung.Max);
    }

    [Fact]
    public void An_action_on_a_rung_the_loaded_table_does_not_carry_is_refused_by_name()
    {
        var table = Table(Row(1, 1, 1000, 1000));

        var err = Assert.Throws<InvalidOperationException>(
            () => BudgetCalibration.Read(new[] { new PricedAction("action.orphan", 9, 10) }, table));

        Assert.Contains("action.orphan", err.Message, StringComparison.Ordinal);
        Assert.Contains("rung 9", err.Message, StringComparison.Ordinal);
    }

    /// <summary>The published inversion, mirrored for the outlier tests: with `poolRolls 1` and
    /// `qPowerMilli 1000` this is the realized power itself, which is what makes those fixtures
    /// legible without pinning anything.</summary>
    static long ImpliedFor(long realizedPowerMilli, RungRow row) =>
        checked(realizedPowerMilli * PowerMath.One) / checked((long)row.PoolRolls * row.QPowerMilli);

    [Fact]
    public void The_ids_above_p90_are_strictly_above_it_and_name_the_outlier()
    {
        // ST4.5b, contract 6's change-description list. Ten distinct readings on a rung whose
        // inversion is the identity, so nearest-rank p90 is the ninth of ten and the one action above
        // it is legible without computing a percentile: sorted[8] is 900, so only 1000 is above.
        var row = Row(1, 1, 1000, 1000);
        var actions = Enumerable.Range(1, 10)
            .Select(i => new PricedAction($"action.{i:00}", 1, i * 100))
            .ToArray();

        var report = BudgetCalibration.Read(actions, Table(row));

        var rung = Assert.Single(report.Rungs);
        Assert.Equal(900, rung.P90);
        Assert.Equal(new[] { "action.10" }, rung.ActionsAboveP90);
    }

    [Fact]
    public void Every_id_above_p90_is_on_that_rung_unique_across_the_report_and_above_its_own_p90()
    {
        // A property, never a count: recompute each named reading from the published inversion and
        // assert it really is above its rung's own published p90 -- so the list and the percentile
        // cannot disagree about where the line is.
        var table = Table(Row(1, 1, 1000, 1000), Row(2, 1, 1323, 1323), Row(3, 2, 1750, 3500));
        var actions = new[]
        {
            new PricedAction("action.a", 1, 100), new PricedAction("action.b", 1, 200),
            new PricedAction("action.c", 1, 300), new PricedAction("action.d", 1, 400),
            new PricedAction("action.e", 1, 500), new PricedAction("action.f", 1, 600),
            new PricedAction("action.g", 1, 700), new PricedAction("action.h", 1, 800),
            new PricedAction("action.i", 1, 900), new PricedAction("action.j", 1, 1000),
            new PricedAction("action.k", 2, 700),
        };
        var rungOf = actions.ToDictionary(a => a.ActionId, a => a.AuthoredRung, StringComparer.Ordinal);
        var realizedOf = actions.ToDictionary(a => a.ActionId, a => a.RealizedPowerMilli, StringComparer.Ordinal);

        var report = BudgetCalibration.Read(actions, table);

        var named = report.Rungs.SelectMany(r => r.ActionsAboveP90).ToList();
        Assert.Equal(named.Count, named.Distinct(StringComparer.Ordinal).Count());

        foreach (var rung in report.Rungs)
        {
            foreach (var id in rung.ActionsAboveP90)
            {
                Assert.Equal(rung.Rung, rungOf[id]);
                Assert.NotNull(rung.P90);
                Assert.True(
                    ImpliedFor(realizedOf[id], table.Rows[rung.Rung - 1]) > rung.P90!.Value,
                    $"'{id}' is named above rung {rung.Rung}'s p90 {rung.P90} but does not exceed it");
            }
        }

        Assert.Contains("action.j", named);
    }

    [Fact]
    public void A_rung_whose_readings_are_all_equal_names_nothing_above_its_p90()
    {
        // "Strictly above", not "at or above": p90 IS the max here, so there is no outlier to name.
        var row = Row(1, 1, 1000, 1000);
        var equal = Enumerable.Range(1, 12)
            .Select(i => new PricedAction($"action.equal{i:00}", 1, 500))
            .ToArray();

        var rung = Assert.Single(BudgetCalibration.Read(equal, Table(row)).Rungs);

        Assert.Equal(500, rung.P90);
        Assert.Equal(500, rung.Max);
        Assert.Empty(rung.ActionsAboveP90);
    }

    [Fact]
    public void A_rung_with_fewer_than_ten_actions_has_p90_equal_to_its_max_and_names_nothing()
    {
        // The nearest-rank consequence the record documents: ceil(0.9 x n) == n below ten actions, so
        // p90 is the max and no reading can be strictly above it. Named here so the empty list on a
        // small rung is a recorded property rather than a surprise in the live reading.
        var row = Row(1, 1, 1000, 1000);
        var actions = new[]
        {
            new PricedAction("action.low", 1, 300),
            new PricedAction("action.mid", 1, 600),
            new PricedAction("action.high", 1, 900),
        };

        var rung = Assert.Single(BudgetCalibration.Read(actions, Table(row)).Rungs);

        Assert.Equal(rung.Max, rung.P90);
        Assert.Empty(rung.ActionsAboveP90);
    }

    [Fact]
    public void The_recommendation_names_the_actions_that_set_it_and_keeps_a_tie_whole()
    {
        // ST4.5f: the argmax, so the outlier dragging the scalar up is named rather than implied.
        // Rung 4 of the real corpus is why the field exists: n=3 there, so its p90 IS its max and the
        // aboveP90 list is empty by construction, yet one action sets the scalar.
        var row = Row(1, 1, 1000, 1000);
        var actions = new[]
        {
            new PricedAction("action.low", 1, 100),
            new PricedAction("action.max", 1, 900),
            new PricedAction("action.tie", 1, 900),
        };

        var report = BudgetCalibration.Read(actions, Table(row));

        Assert.Equal(900, report.RecommendedReferencePower);
        Assert.Equal(new[] { "action.max", "action.tie" }, report.RecommendedBy);
    }

    [Fact]
    public void An_action_whose_price_left_atoms_out_is_named_rather_than_read_as_zero()
    {
        // ST4.5e: the report NAMES the actions whose price could not include every atom, so a 0 in a
        // rung's distribution is never mistaken for "this action is worth nothing". The number itself
        // is untouched -- the action is still priced, which is what contract 3 requires.
        var table = Table(Row(1, 1, 1000, 1000), Row(2, 1, 1323, 1323));
        var actions = new[]
        {
            new PricedAction("action.whole", 1, 900, Array.Empty<string>()),
            new PricedAction("action.partial", 1, 0, new[] { "atom.pooled.a", "atom.pooled.b" }),
            new PricedAction("action.silent", 2, 700),
        };

        var report = BudgetCalibration.Read(actions, table);

        var named = Assert.Single(report.UnpricedActions);
        Assert.Equal("action.partial", named.ActionId);
        Assert.Equal(1, named.AuthoredRung);
        Assert.Equal(new[] { "atom.pooled.a", "atom.pooled.b" }, named.AtomIds);

        // Still counted and still priced: the finding is additive, never a filter.
        Assert.Equal(actions.Length, report.PricedActionCount);
        Assert.Equal(actions.Length, report.Rungs.Sum(r => r.Count));
        Assert.Equal(0L, report.Rungs.Single(r => r.Rung == 1).Min);
    }

    [Fact]
    public void A_reader_that_reported_no_findings_produces_no_unpriced_actions()
    {
        // `null` means "did not ask", and an EMPTY list means "asked and found none" -- neither is a
        // claim that something is unpriced, so both are silent here.
        var table = Table(Row(1, 1, 1000, 1000));
        var actions = new[]
        {
            new PricedAction("action.asked", 1, 900, Array.Empty<string>()),
            new PricedAction("action.did-not-ask", 1, 500),
        };

        var report = BudgetCalibration.Read(actions, table);

        Assert.Empty(report.UnpricedActions);
    }

    [Fact]
    public void The_named_setter_is_always_an_action_the_report_priced()
    {
        // A property over a mixed fixture, never a pinned count: every named id is a priced action,
        // and the scalar it names really is the largest ceiling in the report.
        var table = Table(Row(1, 1, 1000, 1000), Row(2, 1, 1323, 1323), Row(3, 2, 1750, 3500));
        var actions = new[]
        {
            new PricedAction("action.a", 1, 100), new PricedAction("action.b", 2, 700),
            new PricedAction("action.c", 2, 700), new PricedAction("action.d", 3, 40_000),
        };
        var ids = actions.Select(a => a.ActionId).ToHashSet(StringComparer.Ordinal);

        var report = BudgetCalibration.Read(actions, table);

        Assert.NotNull(report.RecommendedReferencePower);
        Assert.NotEmpty(report.RecommendedBy);
        foreach (var id in report.RecommendedBy)
        {
            Assert.Contains(id, ids);
            var action = actions.Single(a => a.ActionId == id);
            Assert.Equal(
                report.RecommendedReferencePower!.Value,
                CeilingFor(action.RealizedPowerMilli, table.Rows[action.AuthoredRung - 1]));
        }
    }
}
