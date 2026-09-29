using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// class-system-todo.md V4 — gk-core/scripts/regen_class_system_baselines.py produces the three checked-in
/// baselines every later class-system phase diffs against. Two properties, proven separately:
/// each file parses and carries `_meta.measuredAt` + `_meta.conditions`, and regenerating twice
/// reproduces byte-identical PAYLOAD content (the `_meta.measuredAt` timestamp is the one field
/// allowed to differ between runs — everything else must not).
///
/// <para><b>One collection.</b> <see cref="RegeneratingTwiceReproducesIdenticalPayloads"/> runs the
/// real regen script while the other two tests read the same files. Under xUnit's default parallel
/// collections the reader could observe a half-written `_baseline-dominance.json` and flake on a
/// missing `coverage.tuningSync` (seen 2026-09-12). The regen now writes to a temp dir so the
/// tracked baselines are never touched, and the collection keeps the two from interleaving at all.</para>
/// </summary>
[Collection("class-system-baselines")]
[Trait("VerificationId", "guard.class-system-baseline-regen")]
public class ClassSystemBaselineRegenTests
{
    static readonly string[] BaselineFiles =
    {
        "_baseline-residual.json", "_baseline-dominance.json", "_baseline-goldens.json"
    };

    [Fact]
    public void EveryBaselineParsesAndCarriesMeta()
    {
        var repoRoot = FindRepoRoot();
        // Self-contained rather than relying on a sibling test (or a prior CI step) having already
        // regenerated the live files — a fresh checkout with the baselines not yet committed must not
        // spuriously fail this test just because of run order.
        //
        // Regenerated into a TEMP dir, never docs/research/class-system: a test that rewrites
        // committed files is a substrate violation, and it is what raced the sibling reads below.
        using var scratch = new TempDirectory();
        var dir = scratch.Path;
        var setup = RunRegen(repoRoot, dir);
        Assert.True(setup.Exit == 0, $"regen failed: {setup.Stdout}\n{setup.Stderr}");

        foreach (var name in BaselineFiles)
        {
            var path = Path.Combine(dir, name);
            Assert.True(File.Exists(path), $"missing {path} — run python scripts/regen_class_system_baselines.py");

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(doc.RootElement.TryGetProperty("_meta", out var meta), $"{name}: no _meta block");
            Assert.True(meta.TryGetProperty("measuredAt", out var measuredAt), $"{name}: _meta.measuredAt missing");
            Assert.True(meta.TryGetProperty("conditions", out var conditions), $"{name}: _meta.conditions missing");
            Assert.False(string.IsNullOrWhiteSpace(measuredAt.GetString()));
            Assert.False(string.IsNullOrWhiteSpace(conditions.GetString()));
        }
    }

    [Fact]
    public void DominanceBaseline_coverageNamesEveryAxisHonestly()
    {
        // class-system-todo.md P8.5: "a test asserts the file's coverage names every axis now live...
        // if still red, that is a recorded number with an owner — the test asserts the RECORD exists,
        // not that the number is good." elementAxis/actionsActive stay honestly non-live (P8.1's own
        // real, still-open gk-core/tools/CombatSim concurrent-edit block) — this test does not require them
        // live, only present and non-empty, so a truthful "not yet" cannot silently regress into an
        // absent field nobody notices. tuningSync is the field this task itself adds: it names a SECOND
        // gap discovered while doing this task (trinity reads gk-core/tools/CombatSim's own internal,
        // still-v1-only tuning copy, not the shipped v2 config P8.2/P8.3 published) — asserted content,
        // not just presence, so the specific claim it makes cannot silently drift from what is true.
        //
        // 2026-09-02: that assertion used to name "aptitudes.v2.json" as a literal, and the regen
        // script pinned v2 to match — so script, baselines and test agreed with each other while all
        // three disagreed with the shipped config (v5 by then). A version literal here means "whatever
        // shipped the day this line was written", which is exactly the drift the test exists to catch.
        // Both sides now resolve the LIVE config the same way: highest data/tuning/aptitudes.v*.json.
        var repoRoot = FindRepoRoot();
        var liveAptitudes = LiveAptitudesFileName(repoRoot);
        var path = Path.Combine(repoRoot, "docs", "research", "class-system", "_baseline-dominance.json");
        Assert.True(File.Exists(path), $"missing {path} — run python scripts/regen_class_system_baselines.py");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(doc.RootElement.TryGetProperty("coverage", out var coverage), "_baseline-dominance.json: no coverage block");

        foreach (var axis in new[] { "elementAxis", "actionsActive", "reservedFamilies", "tuningSync" })
            Assert.True(coverage.TryGetProperty(axis, out _), $"coverage.{axis} missing — every known axis must be named, live or not");

        var tuningSync = coverage.GetProperty("tuningSync").GetString();
        Assert.False(string.IsNullOrWhiteSpace(tuningSync), "coverage.tuningSync must not be empty — an honest gap still needs its own record");
        Assert.Contains(liveAptitudes, tuningSync, StringComparison.Ordinal);
        Assert.Contains("tools/CombatSim", tuningSync, StringComparison.Ordinal);
    }

    /// <summary>The LIVE shipped aptitude tuning file name — the highest
    /// <c>data/tuning/aptitudes.v*.json</c>, never a pinned literal. Tuning files are never
    /// hand-edited (tunables-ssot.md T4 publishes v{n+1}), so the newest file is the live one.
    /// Ordered NUMERICALLY on the version: a lexical sort puts v9 above v10.</summary>
    static string LiveAptitudesFileName(string repoRoot)
    {
        var dir = Path.Combine(repoRoot, "data", "tuning");
        var best = Directory.GetFiles(dir, "aptitudes.v*.json")
            .Select(p => (Path: p, V: int.TryParse(
                Path.GetFileNameWithoutExtension(p).Split(".v").Last(), out var v) ? v : -1))
            .Where(x => x.V >= 0)
            .OrderByDescending(x => x.V)
            .FirstOrDefault();
        if (best.Path is null) throw new InvalidOperationException("no data/tuning/aptitudes.v*.json under " + dir);
        return Path.GetFileName(best.Path);
    }

    [Fact]
    public void RegeneratingTwiceReproducesIdenticalPayloads()
    {
        var repoRoot = FindRepoRoot();

        // Write to a TEMP dir, never the tracked baselines. The regen script defaults to
        // docs/research/class-system, so running this test used to leave three committed files
        // dirty every time (a test-substrate violation) AND race the sibling test below, which
        // reads _baseline-dominance.json — the reader could catch it mid-rewrite and flake on a
        // missing coverage.tuningSync. `--out-dir` redirects the tool; the [Collection] attribute
        // on this class keeps the two tests from running concurrently regardless.
        using var scratch = new TempDirectory();
        var outDir = scratch.Path;

        var runA = RunRegen(repoRoot, outDir);
        var snapshotA = BaselineFiles.ToDictionary(n => n, n => StripMeta(File.ReadAllText(Path.Combine(outDir, n))));

        var runB = RunRegen(repoRoot, outDir);
        var snapshotB = BaselineFiles.ToDictionary(n => n, n => StripMeta(File.ReadAllText(Path.Combine(outDir, n))));

        Assert.True(runA.Exit == 0, $"first regen failed: {runA.Stdout}\n{runA.Stderr}");
        Assert.True(runB.Exit == 0, $"second regen failed: {runB.Stdout}\n{runB.Stderr}");

        foreach (var name in BaselineFiles)
            Assert.Equal(snapshotA[name], snapshotB[name]);
    }

    /// <summary>A temp directory that deletes itself, with a failed delete a FAILURE, never a
    /// swallowed catch (docs/contributing/testing-standard.md; the 65.5 GB temp-dir leak).</summary>
    sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "kilo-class-system-regen-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (!Directory.Exists(Path)) return;
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"failed to delete the regen scratch dir {Path} — a swallowed delete leaks temp " +
                    "storage (testing-standard.md); investigate rather than ignoring", ex);
            }
        }
    }

    /// <summary>Removes `_meta.measuredAt` (the one field the regen script intentionally re-stamps
    /// every run) and re-serializes canonically, so an identical payload compares equal regardless of
    /// property-order jitter across separate JSON writes.</summary>
    static string StripMeta(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        if (node["_meta"] is JsonObject meta) meta.Remove("measuredAt");
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    // The configuration this test assembly was built in. The csproj's build-order references build
    // CombatSim and DominanceBaseline in the same configuration, so the regen script runs those.
#if DEBUG
    const string BuildConfiguration = "Debug";
#else
    const string BuildConfiguration = "Release";
#endif

    static (int Exit, string Stdout, string Stderr) RunRegen(string repoRoot, string outDir)
    {
        var script = Path.Combine(repoRoot, "scripts", "regen_class_system_baselines.py");
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{script}\" --root \"{repoRoot}\" --out-dir \"{outDir}\" --configuration {BuildConfiguration}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot
        };
        return ExternalProcess.Run(psi, 180_000, "regen script timed out");
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            // Marker: `Directory.Build.props`, the tracked MSBuild root file -- NOT a tool. This
            // walked up for the regen SCRIPT, so deleting the script made the test unable to find
            // the repository at all. Same convention as gk-forge/tools/DominanceBaseline and the other three
            // class-system tools, whose `.ps1` marker had already been retired.
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repo root with Directory.Build.props");
    }
}
