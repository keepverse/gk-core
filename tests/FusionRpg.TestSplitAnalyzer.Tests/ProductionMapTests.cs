using Xunit;

namespace FusionRpg.Tools.TestSplitAnalyzer.Tests;

/// <summary>
/// K-T1 (`core-registry-rekey`): the production map lists EVERY test project that references an area's
/// symbols, and only those. The fixture lives in <see cref="ProductionMap.RunFixture"/> so the tool can
/// print a real verdict from `--production-map --self-check` without a test project; this is the xunit
/// port TVB6.1's own row asked for and could not write (its lane's fence excluded `tests/**`), routed as
/// TVB-F17 and landed here on 2026-09-23.
///
/// <para>It is asserted on the fixture's OUTPUT, not only on its boolean, so a fixture that started
/// reporting the right verdict for the wrong reason would fail: `Alpha` is referenced by both synthetic
/// projects, `Beta` by exactly one.</para>
/// </summary>
public class ProductionMapTests
{
    [Fact]
    public void K_T1_the_map_lists_every_project_that_references_an_area_and_only_those()
    {
        var (ok, report) = ProductionMap.RunFixture();

        Assert.True(ok, report);
        Assert.Equal(new[] { "Fixture.TestOne", "Fixture.TestTwo" }, ReferencedBy(report, "Alpha"));
        Assert.Equal(new[] { "Fixture.TestTwo" }, ReferencedBy(report, "Beta"));
    }

    /// <summary>Reads the `  &lt;Area&gt; referenced by: a, b` line the fixture prints.</summary>
    static string[] ReferencedBy(string report, string area)
    {
        var line = report.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .FirstOrDefault(l => l.TrimStart().StartsWith(area + " ", StringComparison.Ordinal)
                              && l.Contains("referenced by:", StringComparison.Ordinal));
        Assert.True(line is not null, $"no '{area} referenced by:' line in the fixture report:\n{report}");
        var names = line![(line.IndexOf("referenced by:", StringComparison.Ordinal) + "referenced by:".Length)..]
            .Trim();
        return names.Length == 0
            ? Array.Empty<string>()
            : names.Split(',').Select(n => n.Trim()).ToArray();
    }
}
