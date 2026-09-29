using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions.Rungs;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// A-G1 (spec-tier-access-gate.md §3.1, §5 tests 1/2/8): the per-rung power budget as published data.
/// Production never recomputes <c>powerBudgetMilli</c> at runtime (same "no `Math.Pow` here" rule
/// <see cref="RungTableTests.No_MathPow_appears_in_the_Rungs_directory"/> already covers for the
/// multiplier columns) — <see cref="Recompute"/> below is a TEST-ONLY mirror of the published
/// derivation, used to prove the shipped numbers are what the formula says they are.
/// </summary>
public class RungPowerBudgetTests
{
    const long ReferencePower = 1000; // PowerMath.One (PowerVector.cs:135) — spec-tier-access-gate.md §3.1

    static string RepoRoot([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;                          // tests/.../Actions
        return Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));    // repo root
    }

    static RungTable ShippedV2() =>
        RungTableLoader.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "action-rungs.v2.json")));

    /// <summary>
    /// The published derivation (spec-tier-access-gate.md §3.1): `poolRolls × referencePower ×
    /// qPowerMilli / 1000`, widened to `long` before multiplying, divided by 1000 last and exactly
    /// once, overflow throws rather than wraps (`checked`, AGENTS.md's numeric rule) — mirrors
    /// `CostFunction.PricePooled`'s own `checked { ... }` shape exactly.
    /// </summary>
    static long Recompute(long poolRolls, long referencePower, long qPowerMilli)
    {
        checked
        {
            return poolRolls * referencePower * qPowerMilli / 1000;
        }
    }

    // ---- test 1: every row equals the recomputed derivation ----------------------------------------

    [Fact]
    public void Every_shipped_rows_powerBudgetMilli_equals_the_recomputed_derivation()
    {
        var table = ShippedV2();

        foreach (var row in table.Rows)
        {
            Assert.True(row.PowerBudgetMilli.HasValue, $"rung {row.Rung} has no powerBudgetMilli");
            var expected = Recompute(row.PoolRolls, ReferencePower, row.QPowerMilli);
            Assert.Equal(expected, row.PowerBudgetMilli!.Value);
        }
    }

    [Theory]
    [InlineData(1, 1000)]   // spec's own worked row: rung 1 lands on exactly referencePower's unit
    [InlineData(5, 6124)]
    [InlineData(10, 37221)]
    public void The_specs_own_three_worked_rows_match_the_shipped_table(int rung, long expectedBudget)
    {
        var table = ShippedV2();
        Assert.True(table.TryGet(rung, out var row));
        Assert.Equal(expectedBudget, row.PowerBudgetMilli);
    }

    [Fact]
    public void Rung_ones_budget_is_exactly_referencePower_the_neutral_default_property()
    {
        // spec §3.1 / AC1b: at referencePower=1000 the budget IS qPowerMilli's own curve, unscaled --
        // rung 1's qPowerMilli is 1000 and its poolRolls is 1, so the budget lands on exactly 1000,
        // never approximately.
        var table = ShippedV2();
        Assert.True(table.TryGet(1, out var row));
        Assert.Equal(1000L, row.PowerBudgetMilli);
    }

    // ---- test 2: monotonic across rungs -------------------------------------------------------------

    [Fact]
    public void The_shipped_power_budget_is_monotonic_across_rungs()
    {
        var table = ShippedV2();
        long? prev = null;
        int? prevRung = null;

        foreach (var row in table.Rows)
        {
            Assert.True(row.PowerBudgetMilli.HasValue);
            if (prev is { } p)
                Assert.True(row.PowerBudgetMilli!.Value > p,
                    $"rung {row.Rung}'s budget {row.PowerBudgetMilli} is not greater than rung {prevRung}'s {p}");
            prev = row.PowerBudgetMilli;
            prevRung = row.Rung;
        }
    }

    // ---- test 8: overflow throws, never wraps -------------------------------------------------------

    [Fact]
    public void Recompute_throws_on_forced_overflow_rather_than_wrapping()
    {
        Assert.Throws<OverflowException>(() => Recompute(long.MaxValue / 2, 3, 1000));
    }

    [Fact]
    public void Recompute_stays_exact_at_realistic_shipped_magnitudes()
    {
        // The sanity check the overflow test needs: realistic content-scale inputs never come close
        // to the boundary that test exercises deliberately.
        Assert.Equal(37221L, Recompute(3, 1000, 12407));
    }

    // ---- backward compatibility: v1 (no powerBudgetMilli column) still loads ------------------------

    [Fact]
    public void The_v1_table_with_no_powerBudgetMilli_column_still_loads_with_null_budgets()
    {
        // v1 stays on disk untouched (tunables-ssot.md T4) and several other test bootstraps embed
        // v1-shaped inline JSON directly -- the loader must keep accepting the column's absence.
        var table = RungTableLoader.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "action-rungs.v1.json")));

        Assert.True(table.TryGet(1, out var row));
        Assert.Null(row.PowerBudgetMilli); // absent, never 0 -- "no ceiling data source loaded"
    }

    [Fact]
    public void A_negative_powerBudgetMilli_is_rejected()
    {
        var json = """
            {"cap":1,"rows":[
              {"rung":1,"minTier":1,"maxTier":1,"poolRolls":1,"qPowerMilli":1000,"costMulti":1000,"cdMulti":1000,"structureBudget":[],"powerBudgetMilli":-1}
            ]}
            """;
        Assert.Throws<RungTableRejection>(() => RungTableLoader.Parse(json));
    }

    [Fact]
    public void A_non_numeric_powerBudgetMilli_is_rejected()
    {
        var json = """
            {"cap":1,"rows":[
              {"rung":1,"minTier":1,"maxTier":1,"poolRolls":1,"qPowerMilli":1000,"costMulti":1000,"cdMulti":1000,"structureBudget":[],"powerBudgetMilli":"a lot"}
            ]}
            """;
        Assert.Throws<RungTableRejection>(() => RungTableLoader.Parse(json));
    }

    // ---- test 6: the doc-drift check — the rung window's row in ssot-power-scale.md §11 ------------

    [Fact]
    public void The_rung_power_budget_has_its_row_in_the_power_scale_register()
    {
        // Mirrors RungSemanticsTests' own doc-drift precedent against this exact file (test 7 there).
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "architecture", "power", "ssot-power-scale.md"));

        Assert.Contains("powerBudgetMilli", text, StringComparison.Ordinal);
        Assert.Contains("action-rungs.v2.json", text, StringComparison.Ordinal);
        Assert.Contains("A-G1", text, StringComparison.Ordinal);
        // The row's own justification: not a private curve, and C1 stays disabled either way.
        Assert.Contains("poolRolls", text, StringComparison.Ordinal);
        Assert.Contains("C1", text, StringComparison.Ordinal);
    }
}
