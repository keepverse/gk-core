using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FusionRpg.Core.Tests.TestSupport;
using System.Linq;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>
/// class-system-todo.md P8.4 — runs gk-core/scripts/audit-reader-census.py fresh and asserts its computed
/// reader-less-family-count and reserved-edge numbers match what aptitudes.v2.json's own
/// _meta.measurable prose currently claims. P1.5/P1.6 (2026-08-26) produced that count exactly
/// ONCE, by hand, with no script attached despite P1.5's own verify line requiring one — this is
/// that script's test. Per P8.4's own verify line: "the reader census script recomputes it and
/// fails if the file disagrees — a stale reservation table becomes a red test, not a lie with a
/// green test beside it." If _meta.measurable is stale, <see cref="ComputedCensus_matches_metaMeasurables_claimed_numbers"/>
/// is SUPPOSED to fail — that is not a bug in this test, it is P8.4's entire point.
/// </summary>
public class ReaderCensusTests
{
    static readonly Regex FamiliesClaimRe = new(@"(\d+)\s+families with no shipped reader", RegexOptions.Compiled);
    static readonly Regex EdgesClaimRe = new(@"(\d+)\s+of\s+(\d+)\s+edges\s*/\s*(\d+)\s*%", RegexOptions.Compiled);

    [Fact]
    public void ComputedCensus_matches_metaMeasurables_claimed_numbers()
    {
        var repoRoot = FindRepoRoot();
        var claim = ParseMeasurableClaim(repoRoot);
        using var report = RunCensusJson(repoRoot);
        var computed = ExtractTotals(report);

        Assert.True(
            claim.Families == computed.Families &&
            claim.EdgesReserved == computed.EdgesReserved &&
            claim.EdgesTotal == computed.EdgesTotal &&
            claim.Pct == computed.Pct,
            "the live data/tuning/aptitudes.v{n}.json's _meta.measurable is STALE relative to a fresh reader census.\n" +
            $"  claimed:  {claim.Families} families, {claim.EdgesReserved} of {claim.EdgesTotal} edges / {claim.Pct}%\n" +
            $"  computed: {computed.Families} families, {computed.EdgesReserved} of {computed.EdgesTotal} edges / {computed.Pct}%\n" +
            "Run `python scripts/audit-reader-census.py` for the current per-family breakdown (`--json` for " +
            "machine-readable detail). _meta.measurable is prose, not something this test or the census " +
            "script may rewrite for you — it needs a manual, reviewed edit.");
    }

    [Fact]
    public void CensusScript_ownCheckMode_agrees_with_this_tests_independent_parse()
    {
        // Belt-and-braces: --check is the script's OWN prose-vs-computed comparison, exposed as a CLI
        // mode for humans/CI to use directly. This test parses the same prose independently (see
        // ParseMeasurableClaim) rather than trusting --check's internal regex — if the two disagree,
        // the script's own parsing has drifted from this test's, which is itself worth catching.
        var repoRoot = FindRepoRoot();
        var claim = ParseMeasurableClaim(repoRoot);
        using var report = RunCensusJson(repoRoot);
        var computed = ExtractTotals(report);
        var independentVerdict = claim.Families == computed.Families && claim.EdgesReserved == computed.EdgesReserved
                                  && claim.EdgesTotal == computed.EdgesTotal && claim.Pct == computed.Pct;

        var (checkExit, checkStdout, checkStderr) = RunCensus(repoRoot, "--check");
        Assert.True((checkExit == 0) == independentVerdict,
            $"scripts/audit-reader-census.py --check exit={checkExit} disagrees with this test's own " +
            $"independent prose parse (independent verdict: agrees={independentVerdict})\n" +
            $"stdout:\n{checkStdout}\nstderr:\n{checkStderr}");
    }

    [Fact]
    public void CrossCheck_scriptAgreesWithEstablishedGroundTruth_forFamiliesWithACitation()
    {
        // P8.4 deliverable #2, as a standing regression: catalog.json's entries[] carry an explicit
        // "Reader VERIFIED (class-system P1.5)" / "No reader:" citation for the 29 families that
        // original manual census actually covered (H.1-H.7 catalog-extension families only — see
        // gk-core/scripts/audit-reader-census.py's own SCOPE docstring for why 29, not familyRead's full 48).
        // Parsed independently here, not through the script's own --crosscheck text output.
        var repoRoot = FindRepoRoot();
        var catalogPath = Path.Combine(repoRoot, "data", "seed", "derived-stats", "catalog.json");
        Assert.True(File.Exists(catalogPath), "missing " + catalogPath);
        using var catalogDoc = JsonDocument.Parse(File.ReadAllText(catalogPath));

        var groundTruth = new Dictionary<string, string>();
        foreach (var entry in catalogDoc.RootElement.GetProperty("entries").EnumerateArray())
        {
            var family = entry.GetProperty("family").GetString()!;
            var note = entry.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "";
            var ucn = (entry.TryGetProperty("unitClassNote", out var u) ? u.GetString() ?? "" : "").ToLowerInvariant();
            if (note.Contains("Reader VERIFIED", StringComparison.OrdinalIgnoreCase))
                groundTruth[family] = "reader";
            else if (ucn.Contains("no reader") || ucn.Contains("no shipped reader"))
                groundTruth[family] = "no-reader";
        }
        // The scope this ground truth covers (29 families, H.1-H.7 catalog-extension only) is a fixed
        // historical citation scan, never pinned as a count (population-pin SE3.3, 2026-09-19) -- if
        // catalog.json's citation text convention ever changed shape, an EMPTY ground truth would make
        // the cross-check loop below vacuously pass, which this guards against.
        Assert.NotEmpty(groundTruth);

        using var report = RunCensusJson(repoRoot);
        var computed = new Dictionary<string, bool>();
        foreach (var f in report.RootElement.GetProperty("families").EnumerateArray())
            computed[f.GetProperty("family").GetString()!] = f.GetProperty("has_reader").GetBoolean();

        // resource.max/resource.regen are a DOCUMENTED, evidence-backed exception, not a blind
        // allowlist: gk-core/src/FusionRpg.Core/Balance/Analytic/Predictor.cs and .../Balance/Guards/
        // TerminationGuard.cs both call .Get(DerivedStatChannels.ResourceRegen("hp")) /
        // .Get(DerivedStatChannels.ResourceMax("hp")) for real (Predictor.cs ~147-155,
        // TerminationGuard.cs ~122) and `git status` shows both files untracked -- i.e. written after
        // catalog.json's 2026-08-26 citation, as part of this program's own P8.1-P8.3 work. That makes
        // catalog.json's "no reader" claim for these two families stale, not this script wrong. Any
        // OTHER disagreement below is NOT pre-explained and means the census script regressed.
        var knownDrift = new HashSet<string> { "resource.max", "resource.regen" };

        // RETIRED families: registered (so catalog.json lists them, and groundTruth is built from
        // catalog.json) but carrying zero edges in the live tuning (so the census, which walks EDGES,
        // never reports them). That is a legitimate state, not a script regression — a retirement shim
        // exists precisely to stay registered while nothing points at it. combat.heal.power was
        // generalised into resource.restore.{resource} on 2026-09-02 and kept only so the archived
        // aptitudes.v1/v2/v3.json remain loadable.
        var retiredNoEdges = new HashSet<string> { "combat.heal.power" };

        var unexplained = new List<string>();
        foreach (var (family, expected) in groundTruth)
        {
            if (!computed.TryGetValue(family, out var computedHasReader))
            {
                if (!retiredNoEdges.Contains(family))
                    unexplained.Add($"{family}: missing from the computed report");
                continue;
            }
            var expectedHasReader = expected == "reader";
            if (computedHasReader != expectedHasReader && !knownDrift.Contains(family))
                unexplained.Add($"{family}: ground-truth={expected} computed={(computedHasReader ? "reader" : "no-reader")}");
        }

        Assert.True(unexplained.Count == 0,
            "reader census disagrees with established P1.5/P1.6 ground truth on famil(y/ies) with no " +
            "documented explanation (a script bug to investigate, per P8.4's own instructions):\n" +
            string.Join("\n", unexplained));
    }

    // ---- shared parsing -----------------------------------------------------------------------

    readonly record struct MeasurableTotals(int Families, int EdgesReserved, int EdgesTotal, int Pct);

    static MeasurableTotals ParseMeasurableClaim(string repoRoot)
    {
        // Live shipped config, not a pinned literal (2026-09-02) — see the census script's own note.
        var tuningDir = Path.Combine(repoRoot, "data", "tuning");
        var aptitudesPath = Directory.GetFiles(tuningDir, "aptitudes.v*.json")
            .Select(f => (Path: f, V: int.TryParse(Path.GetFileNameWithoutExtension(f).Split(".v").Last(), out var v) ? v : -1))
            .Where(x => x.V >= 0).OrderByDescending(x => x.V).Select(x => x.Path).FirstOrDefault() ?? "";
        Assert.True(File.Exists(aptitudesPath), "missing " + aptitudesPath);
        using var doc = JsonDocument.Parse(File.ReadAllText(aptitudesPath));
        var measurable = doc.RootElement.GetProperty("_meta").GetProperty("measurable").GetString();
        Assert.False(string.IsNullOrEmpty(measurable), "_meta.measurable is empty");

        var famMatch = FamiliesClaimRe.Match(measurable!);
        var edgeMatch = EdgesClaimRe.Match(measurable!);
        Assert.True(famMatch.Success,
            "could not find 'N families with no shipped reader' in _meta.measurable — its prose " +
            "shape changed; update FamiliesClaimRe in ReaderCensusTests.cs to match");
        Assert.True(edgeMatch.Success,
            "could not find 'N of M edges / P%' in _meta.measurable — its prose shape changed; " +
            "update EdgesClaimRe in ReaderCensusTests.cs to match");

        return new MeasurableTotals(
            int.Parse(famMatch.Groups[1].Value),
            int.Parse(edgeMatch.Groups[1].Value),
            int.Parse(edgeMatch.Groups[2].Value),
            int.Parse(edgeMatch.Groups[3].Value));
    }

    static MeasurableTotals ExtractTotals(JsonDocument report) => new(
        report.RootElement.GetProperty("families_without_reader").GetInt32(),
        report.RootElement.GetProperty("edges_reserved").GetInt32(),
        report.RootElement.GetProperty("edges_total").GetInt32(),
        (int)Math.Round(report.RootElement.GetProperty("edges_reserved_pct").GetDouble()));

    // ---- process plumbing (mirrors PowerGuardTests/StatTaxonomyGuardTests' own Process.Start pattern,
    // pointed at `python` instead of `powershell`) ------------------------------------------------

    static JsonDocument RunCensusJson(string repoRoot)
    {
        var (exit, stdout, stderr) = RunCensus(repoRoot, "--json");
        Assert.True(exit == 0, $"census --json failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        return JsonDocument.Parse(stdout);
    }

    static (int Exit, string Stdout, string Stderr) RunCensus(string repoRoot, string args)
    {
        var script = Path.Combine(repoRoot, "scripts", "audit-reader-census.py");
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{script}\" {args}",
            WorkingDirectory = repoRoot,
            CreateNoWindow = true
        };
        return ExternalProcess.Run(psi, 60_000, "census script timed out");
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
