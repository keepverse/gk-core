using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// lawn LW1.1 (<c>docs/architecture/lawn-playable/spec-rider-default-on.md</c>): the perf ceiling a lawn
/// feature is measured against is a <b>tunable</b>, and every gate that checks it reads
/// <c>gk-core/data/tuning/lawn-perf-budget.v1.json</c> rather than a compiled-in constant.
///
/// <para><b>What went wrong, precisely.</b> LAWN-COMBAT-WIRE's L-N1 ruling shipped
/// <c>LawnBasicAttackFeature</c> default-off because the RPG capture pipeline took 28.06% of wall at 300
/// zombies against a proposed <c>&lt;=6%</c> — and that ceiling existed only in prose: a plan
/// paragraph, a commit message and a doc comment. Nothing could read it, so nothing could fail when it
/// drifted. This guard is the falsifier for the second half of that sentence.</para>
///
/// <para>The ceiling's VALUE is pinned in the Core loader's own test, not here: this project does not
/// reference <c>FusionRpg.Core</c> (Guard.Tests is deliberately lightweight, see its csproj), and a
/// second parser here would be a second reader. What this file owns is the <b>no-constant</b> claim,
/// which is the shape a source-text scan is good at.</para>
/// </summary>
public class LawnPerfBudgetGuardTests
{
    const string BudgetPath = "data/tuning/lawn-perf-budget.v1.json";

    /// <summary>The one production file allowed to name the ceiling's fields — the loader that reads
    /// the file. Kept as a path, not a count, so moving the loader fails loudly here.</summary>
    const string Reader = "src/FusionRpg.Core/Diagnostics/LawnPerfBudgetTuning.cs";

    /// <summary>A compiled-in lawn perf ceiling: a `const` whose NAME says it is this budget's ceiling.
    /// Deliberately narrow — the repo has many legitimate `Ceiling` constants (rung bands, socket
    /// ceilings, parity bands), and a broad scan would drown in them.</summary>
    static readonly Regex CompiledCeiling = new(
        @"\bconst\b[^;\r\n]*(PerfCeiling|PipelineShare|SharePercent|LawnPerfBudget)",
        RegexOptions.Compiled);

    [Fact]
    public void The_ceiling_is_stated_in_the_shipped_tunable()
    {
        var path = Path.Combine(RepoRoot(), BudgetPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{BudgetPath} is missing — the ceiling has no home");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var ceiling = doc.RootElement.GetProperty("ceiling");

        Assert.Equal(6, ceiling.GetProperty("pipelineSharePercent").GetDouble());
        Assert.Equal(300, ceiling.GetProperty("referenceZombies").GetInt32());
        Assert.True(
            ceiling.TryGetProperty("minFpsRatioOfOff", out _),
            "the fps half is a required gate (owner ruling 2026-09-16) — null means unmeasured, an absent "
            + "key means the gate was dropped");
    }

    [Fact]
    public void No_production_source_declares_a_lawn_perf_ceiling_constant()
    {
        var offenders = ProductionSources(RepoRoot())
            .Where(f => CompiledCeiling.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "a compiled-in lawn perf ceiling was found in " + string.Join(", ", offenders)
            + $" — the ceiling belongs in {BudgetPath}, read through {Reader}");
    }

    /// <summary>The falsifier for the test above: a scan with the wrong root, the wrong filter or an
    /// unreadable pattern reports zero offenders and passes vacuously for the wrong reason.</summary>
    [Fact]
    public void The_scan_is_not_vacuous()
    {
        var root = RepoRoot();
        var files = ProductionSources(root).ToList();

        Assert.True(files.Count > 100,
            $"the production source scan found only {files.Count} files — it is not reaching src/**, so the "
            + "guard above would pass without proving anything");

        var reader = Path.Combine(root, Reader.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(reader), $"{Reader} has moved — update this guard's Reader constant");

        // The reader really does name the fields it reads, so a scan that found the reader file empty
        // would still be caught.
        var text = File.ReadAllText(reader);
        Assert.Contains("pipelineSharePercent", text, StringComparison.Ordinal);
        Assert.Contains("referenceZombies", text, StringComparison.Ordinal);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CONTRIBUTING.md"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }

    static IEnumerable<string> ProductionSources(string root)
    {
        var src = Path.Combine(root, "src");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                return !rel.Contains("/bin/", StringComparison.Ordinal)
                    && !rel.Contains("/obj/", StringComparison.Ordinal);
            });
    }
}
