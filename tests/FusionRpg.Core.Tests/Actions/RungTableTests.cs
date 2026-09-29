using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions.Rungs;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>T4/T5 (action-todo.md, spec-rung-table.md). The authored ladder loads, climbs, and its
/// resolve path allocates nothing.</summary>
public class RungTableTests
{
    static string TuningPath([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;                           // tests/.../Actions
        var repo = Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));  // repo root
        return Path.Combine(repo, "data", "tuning", "action-rungs.v1.json");
    }

    static string ShippedJson() => File.ReadAllText(TuningPath());

    static RungTable Shipped() => RungTableLoader.Parse(ShippedJson());

    // ---- load / shape --------------------------------------------------------------------------------

    [Fact]
    public void The_shipped_ladder_loads_with_ten_rungs()
    {
        var table = Shipped();
        // pin: immutable gk-core/data/tuning/action-rungs.v1.json
        Assert.Equal(10, table.Cap);
        // Self-consistency, not a second literal: Cap actually matches the shipped row count.
        Assert.Equal(table.Cap, table.Rows.Count);
        Assert.True(table.TryGet(1, out _));
        Assert.True(table.TryGet(10, out _));
        Assert.False(table.TryGet(11, out _));
        Assert.False(table.TryGet(0, out _));
    }

    [Fact]
    public void Zero_rows_is_rejected()
    {
        var json = """{"cap":0,"rows":[]}""";
        var ex = Assert.Throws<RungTableRejection>(() => RungTableLoader.Parse(json));
        Assert.Contains("zero rows", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_gap_in_the_rung_sequence_is_rejected_naming_the_missing_index()
    {
        var json = """
            {"cap":3,"rows":[
              {"rung":1,"minTier":1,"maxTier":1,"poolRolls":1,"qPowerMilli":1000,"costMulti":1000,"cdMulti":1000,"structureBudget":[]},
              {"rung":3,"minTier":1,"maxTier":1,"poolRolls":1,"qPowerMilli":2000,"costMulti":2000,"cdMulti":1200,"structureBudget":[]}
            ]}
            """;
        var ex = Assert.Throws<RungTableRejection>(() => RungTableLoader.Parse(json));
        Assert.Contains("rung 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_structure_axis_is_rejected_never_ignored()
    {
        var json = """
            {"cap":1,"rows":[
              {"rung":1,"minTier":1,"maxTier":1,"poolRolls":1,"qPowerMilli":1000,"costMulti":1000,"cdMulti":1000,"structureBudget":["telekinesis"]}
            ]}
            """;
        var ex = Assert.Throws<RungTableRejection>(() => RungTableLoader.Parse(json));
        Assert.Contains("telekinesis", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_inverted_tier_window_is_rejected()
    {
        var json = """
            {"cap":1,"rows":[
              {"rung":1,"minTier":5,"maxTier":1,"poolRolls":1,"qPowerMilli":1000,"costMulti":1000,"cdMulti":1000,"structureBudget":[]}
            ]}
            """;
        Assert.Throws<RungTableRejection>(() => RungTableLoader.Parse(json));
    }

    [Fact]
    public void Changing_the_cap_reduces_the_ladder_and_the_top_rung_shifts()
    {
        // Deleting rows 9-10 and re-spanning: the cap-8 table's top rung is what WAS rung 8.
        var json = """
            {"cap":8,"rows":[
              {"rung":1,"minTier":1,"maxTier":1,"poolRolls":1,"qPowerMilli":1000,"costMulti":1000,"cdMulti":1000,"structureBudget":[]},
              {"rung":2,"minTier":1,"maxTier":1,"poolRolls":1,"qPowerMilli":1323,"costMulti":1380,"cdMulti":1150,"structureBudget":[]},
              {"rung":3,"minTier":2,"maxTier":2,"poolRolls":1,"qPowerMilli":1750,"costMulti":1904,"cdMulti":1322,"structureBudget":["scopeSplit"]},
              {"rung":4,"minTier":2,"maxTier":2,"poolRolls":1,"qPowerMilli":2315,"costMulti":2628,"cdMulti":1521,"structureBudget":["scopeSplit"]},
              {"rung":5,"minTier":3,"maxTier":3,"poolRolls":2,"qPowerMilli":3062,"costMulti":3627,"cdMulti":1749,"structureBudget":["condition"]},
              {"rung":6,"minTier":3,"maxTier":3,"poolRolls":2,"qPowerMilli":4051,"costMulti":5005,"cdMulti":2011,"structureBudget":["condition"]},
              {"rung":7,"minTier":4,"maxTier":4,"poolRolls":2,"qPowerMilli":5359,"costMulti":6907,"cdMulti":2313,"structureBudget":["sequence"]},
              {"rung":8,"minTier":4,"maxTier":4,"poolRolls":2,"qPowerMilli":7090,"costMulti":9531,"cdMulti":2660,"structureBudget":["sequence"]}
            ]}
            """;
        var table = RungTableLoader.Parse(json);
        Assert.Equal(8, table.Cap);
        Assert.True(table.TryGet(8, out var top));
        Assert.Equal(7090, top.QPowerMilli);
        Assert.False(table.TryGet(9, out _));
    }

    // ---- monotonicity (T5) ------------------------------------------------------------------------

    [Fact]
    public void The_shipped_ladder_is_monotonic_through_PowerVector()
    {
        var result = RungMonotonicity.VerifyPowerClimbs(Shipped());
        Assert.True(result.Ok, result.Detail);
    }

    [Fact]
    public void A_planted_inverted_row_fails_monotonicity()
    {
        // Rung 5 deliberately priced BELOW rung 4 — the defect the assertion exists to catch.
        var json = """
            {"cap":5,"rows":[
              {"rung":1,"minTier":1,"maxTier":1,"poolRolls":1,"qPowerMilli":1000,"costMulti":1000,"cdMulti":1000,"structureBudget":[]},
              {"rung":2,"minTier":1,"maxTier":1,"poolRolls":1,"qPowerMilli":1323,"costMulti":1380,"cdMulti":1150,"structureBudget":[]},
              {"rung":3,"minTier":2,"maxTier":2,"poolRolls":1,"qPowerMilli":1750,"costMulti":1904,"cdMulti":1322,"structureBudget":[]},
              {"rung":4,"minTier":2,"maxTier":2,"poolRolls":1,"qPowerMilli":2315,"costMulti":2628,"cdMulti":1521,"structureBudget":[]},
              {"rung":5,"minTier":3,"maxTier":3,"poolRolls":2,"qPowerMilli":1900,"costMulti":3627,"cdMulti":1749,"structureBudget":[]}
            ]}
            """;
        var table = RungTableLoader.Parse(json);
        var result = RungMonotonicity.VerifyPowerClimbs(table);
        Assert.False(result.Ok);
        Assert.Contains("rung 5", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Cost_span_exceeds_power_span_on_the_shipped_ladder()
    {
        var result = RungMonotonicity.VerifyCostSpanExceedsPowerSpan(Shipped());
        Assert.True(result.Ok, result.Detail);
    }

    [Fact]
    public void A_flat_tax_where_cost_span_equals_power_span_is_rejected()
    {
        // cost and power scale identically across the whole ladder -> the tax is flat, which the
        // spec calls the "loadout becomes a sort, not a decision" defect.
        var json = """
            {"cap":2,"rows":[
              {"rung":1,"minTier":1,"maxTier":1,"poolRolls":1,"qPowerMilli":1000,"costMulti":1000,"cdMulti":1000,"structureBudget":[]},
              {"rung":2,"minTier":1,"maxTier":1,"poolRolls":1,"qPowerMilli":1323,"costMulti":1323,"cdMulti":1150,"structureBudget":[]}
            ]}
            """;
        var table = RungTableLoader.Parse(json);
        var result = RungMonotonicity.VerifyCostSpanExceedsPowerSpan(table);
        Assert.False(result.Ok);
    }

    // ---- architecture: no Math.Pow, no contentScale ------------------------------------------------

    [Fact]
    public void No_MathPow_appears_in_the_Rungs_directory()
    {
        // spec-rung-table.md §1: "the exponent form documents how the numbers were derived; it is
        // never evaluated at runtime." A human computed them once — Math.Pow appearing here would
        // mean someone started re-deriving them live, on a magnitude/replay path.
        var dir = RungsSourceDir();
        Assert.True(Directory.Exists(dir), $"rungs source dir not found: {dir}");

        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.TopDirectoryOnly))
            if (File.ReadAllText(file).Contains("Math.Pow", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));

        Assert.True(offenders.Count == 0, "Math.Pow found in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void No_contentScale_reference_appears_in_the_Rungs_directory()
    {
        // PS-4: the rung ladder must never be multiplied by contentScale — the anchor already did
        // that. Grep-style because the failure is silent otherwise.
        var dir = RungsSourceDir();
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.TopDirectoryOnly))
            if (File.ReadAllText(file).Contains("contentScale", StringComparison.OrdinalIgnoreCase))
                offenders.Add(Path.GetFileName(file));

        Assert.True(offenders.Count == 0, "contentScale reference found in: " + string.Join(", ", offenders));
    }

    static string RungsSourceDir([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        var repo = Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));
        return Path.Combine(repo, "src", "FusionRpg.Core", "Actions", "Rungs");
    }

    // ---- resolve path -------------------------------------------------------------------------------

    [Fact]
    public void Two_readers_resolve_identical_multipliers_for_the_same_rung()
    {
        var table = Shipped();
        Assert.True(table.TryResolve(7, out var readerA));
        Assert.True(table.TryResolve(7, out var readerB));
        Assert.Equal(readerA.QPowerMilli, readerB.QPowerMilli);
        Assert.Equal(readerA.CostMulti, readerB.CostMulti);
        Assert.Equal(readerA.CdMulti, readerB.CdMulti);
    }

    [Fact]
    public void Resolve_allocates_zero_bytes()
    {
        var table = Shipped();
        for (var i = 0; i < 1000; i++) table.TryResolve((i % 10) + 1, out _); // JIT + warm

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++) table.TryResolve((i % 10) + 1, out _);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }
}
