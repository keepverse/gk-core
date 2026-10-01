using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Executable-topology contracts for the Phase 0B verification repair. These tests deliberately
/// assert relationships and failure contracts, never the number of projects or tests in the tree.
/// </summary>
[Trait("VerificationId", "guard.verification-topology")]
// A class may carry several VerificationId traits, and the generated-seed guard's owner boundary
// needs one of its own: the row's `verificationId` must resolve to a real trait or the registry's
// integrity guard refuses the whole registry and every agent's scoped verification with it.
[Trait("VerificationId", "guard.generated-seed")]
public sealed class VerificationTopologyTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    /// <summary>Runs a Python tool under test: the generated-seed guard and the guard runner are both
    /// Python now, so this is the only interpreter these tests need.</summary>
    static (int Exit, string Stdout, string Stderr) RunPython(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot(),
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        return ExternalProcess.Run(psi, 120_000, "verification topology python timed out");
    }

    /// <summary>
    /// Runs the Python planner against a PLANTED root, with the root as BOTH the working directory and
    /// an explicit <c>--root</c> so the tool resolves its own guard and lib relative to the planted copy
    /// rather than to the repository this test file lives in.
    /// <para>
    /// A single spawn helper, deliberately. There were two dialects here while the guard runner was
    /// PowerShell, and the honest reason for that -- "one helper that accepted both would have to guess
    /// which interpreter a caller meant" -- died with the port. A helper that guesses is worse than two
    /// that do not, and one helper cannot guess.
    /// </para>
    /// </summary>
    static (int Exit, string Stdout, string Stderr) RunPlannerProcess(string root, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root,
        };
        psi.ArgumentList.Add("scripts/verify-change.py");
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return ExternalProcess.Run(psi, 300_000, "verify-change.py timed out");
    }

    static (int Exit, string Stdout, string Stderr) RunGit(string root, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return ExternalProcess.Run(psi, 30_000, "verification topology git command timed out");
    }

    [Fact]
    public void Release_calls_the_complete_ci_workflow_before_publishing()
    {
        var ci = Read(".github", "workflows", "ci.yml");
        var release = Read(".github", "workflows", "release.yml");

        // The reusable workflow call is the executable superset contract. Raw comment markers
        // are not evidence and must not be used as a substitute for the actual job dependency.
        Assert.Contains("workflow_call:", ci, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/ci.yml", release, StringComparison.Ordinal);
        Assert.Contains("needs: ci-superset", release, StringComparison.Ordinal);
        Assert.DoesNotContain("VERIFICATION_GATE:", release, StringComparison.Ordinal);

        // ── The superset property, measured rather than matched against a magic string ──────────────
        //
        // Three assertions used to sit here: that release.yml contained the literal text
        // "CI superset contract", that it contained `Get-Content .github/workflows/ci.yml -Raw`, and
        // that it contained "FUSIONRPG_REVIEW_SESSION is required for a release". None of those strings
        // is in the file any more, so the test asserted a mechanism that had been ported away and could
        // only ever fail. A string that is absent cannot be a contract.
        //
        // What replaced them is checked structurally, and it is STRICTLY STRONGER than a substring: a
        // comment could satisfy a magic string, and none of these can be satisfied by prose.
        //
        //   * The release requires the COMPLETE CI workflow (asserted above), and gates on that job.
        //   * CI may narrow a project with a trait filter; the release must re-run every such project
        //     UNFILTERED, because a release that skips the tests CI filtered out is not a superset.
        //   * The release's own gate must narrow nothing at all, and must not name a project CI does
        //     not run — a stale entry there would silently widen the release gate beyond CI.
        //
        // Measured: ci.yml runs 77 `dotnet test` projects, 1 of them with `--filter`; release.yml runs
        // 68, none with a filter; the filtered one is among the 68; orphans are 0.
        var ciProjects = TestProjects(ci);
        var ciFiltered = TestProjects(ci, filteredOnly: true);
        var releaseUnfiltered = TestProjects(release, filteredOnly: false);
        var releaseFiltered = TestProjects(release, filteredOnly: true);

        Assert.True(ciProjects.Count > 0, "ci.yml names no test project, so the superset claim is vacuous");
        Assert.True(releaseUnfiltered.Count > 0, "release.yml names no test project");
        Assert.True(ciFiltered.Count > 0,
            "ci.yml filters no project, so the release gate has nothing to re-run unfiltered and the "
            + "superset argument reduces to 'the release repeats CI'");

        Assert.Equal(new string[0], releaseFiltered.Where(p => !ciProjects.Contains(p)).ToArray());
        foreach (var filtered in ciFiltered)
        {
            Assert.True(releaseUnfiltered.Contains(filtered),
                $"ci.yml filters {filtered} but release.yml does not re-run it unfiltered, so a release "
                + "would ship without the tests CI skipped. That is the superset contract, broken.");
        }
        var orphans = releaseUnfiltered.Where(p => !ciProjects.Contains(p)).ToArray();
        Assert.Equal(new string[0], orphans);
    }

    /// <summary>The test projects a workflow invokes, as repository-relative csproj paths.</summary>
    /// <param name="workflow">The workflow's text.</param>
    /// <param name="filteredOnly">
    /// True for the projects invoked WITH a trait filter, false for the projects invoked WITHOUT one.
    /// The distinction is the whole point: an unfiltered re-run is what makes the release a superset of
    /// a filtered CI pass, so the two must be counted separately rather than as one set.
    /// </param>
    /// <remarks>
    /// A line is matched by its `dotnet test &lt;csproj&gt;` invocation and classified by whether that
    /// SAME line carries `--filter`. Matching per line rather than per file is deliberate: a filter on a
    /// following line would otherwise be attributed to the wrong project, and that is the shape of
    /// mistake this is here to prevent.
    /// </remarks>
    static HashSet<string> TestProjects(string workflow, bool? filteredOnly = null)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in workflow.Split('\n'))
        {
            var line = raw.Trim();
            var at = line.IndexOf("dotnet test ", StringComparison.Ordinal);
            if (at < 0) continue;
            var rest = line[(at + "dotnet test ".Length)..].Trim();
            var space = rest.IndexOf(' ');
            var project = (space < 0 ? rest : rest[..space]).Trim('"');
            if (!project.EndsWith(".csproj", StringComparison.Ordinal)) continue;
            var filtered = line.Contains("--filter", StringComparison.Ordinal);
            if (filteredOnly is null || filtered == filteredOnly) result.Add(project);
        }
        return result;
    }

    [Fact]
    public void Every_workflow_fetches_enough_history_and_passes_an_explicit_guard_range()
    {
        foreach (var name in new[] { "ci.yml", "release.yml", "nightly.yml" })
        {
            var text = Read(".github", "workflows", name);
            Assert.Contains("fetch-depth: 0", text, StringComparison.Ordinal);
        }

        var ci = Read(".github", "workflows", "ci.yml");
        Assert.Contains("run_guards.py --tier ci --ci-range", ci.Replace('\\', '/'));
        Assert.Contains("generated-seed", ci, StringComparison.Ordinal);
        Assert.Contains("--require-explicit-range", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("HEAD~1..HEAD", ci, StringComparison.Ordinal);

        var runner = Read("scripts", "run_guards.py");
        var generated = Read("scripts", "guard-generated-seed.py");
        Assert.DoesNotContain("HEAD~1..HEAD", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("HEAD~1..HEAD", generated, StringComparison.Ordinal);
        // BOTH spellings, in the places that actually carry them: the runner still drops a range
        // switch for a non-git fixture, and the ported guard takes the long option. A single
        // `Contains` over the concatenation passed while either half was wrong. Both the PowerShell and
        // the long spelling are named in the runner's RANGE_SWITCHES precisely so this stays true while
        // the registry holds a mixture.
        Assert.Contains("-RequireExplicitRange", runner, StringComparison.Ordinal);
        Assert.Contains("--require-explicit-range", runner, StringComparison.Ordinal);
        Assert.Contains("require_explicit_range", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_ci_range_is_mandatory_in_both_range_consumers()
    {
        var (generatedExit, generatedOut, generatedErr) = RunPython(new[]
        {
            Path.Combine(RepoRoot(), "scripts", "guard-generated-seed.py"),
            "--require-explicit-range",
        });
        Assert.True(generatedExit != 0, $"generated-seed accepted working-tree mode in CI contract\n{generatedOut}\n{generatedErr}");

        var (runnerExit, runnerOut, runnerErr) = RunPython(new[]
        {
            Path.Combine(RepoRoot(), "scripts", "run_guards.py"), "--tier", "ci",
        });
        Assert.True(runnerExit != 0, $"guard runner accepted a missing CI range\n{runnerOut}\n{runnerErr}");
        // The refusal NAME, not the sentence around it: a reworded message must not fail a topology
        // contract, and a renamed refusal must.
        Assert.Contains("CI-RANGE-REQUIRED", runnerOut + runnerErr, StringComparison.Ordinal);
    }

    /// <summary>
    /// A filtered test run that executes ZERO tests is RED, and a run that leaves no unambiguous TRX
    /// evidence is AMBIGUOUS -- both must be refusals, never a green run reported from an exit code alone.
    /// <para>
    /// This used to assert PowerShell SPELLINGS: <c>Get-JUnitTestCases</c> (a PowerShell function that
    /// the Python never had, so the assertion could only ever fail), plus <c>DiffBaseRef</c> and
    /// <c>RequireDiffFence</c>. A substring sweep over a source file pins how the tool is WRITTEN, and
    /// it changes for a rename that preserves behaviour -- which is how an assertion ends up either
    /// blocking a correct edit or, worse, passing for the wrong reason.
    /// <para>
    /// What is asserted now is the CONTRACT: the tool declares named refusals for every way a run can
    /// produce no evidence, and a name is the part a caller can branch on. The BEHAVIOURAL half of this
    /// contract is the test below, which plants a root, runs the planner, and requires a non-zero exit --
    /// a sweep cannot stand in for that, and a name cannot either.
    /// </para>
    /// </summary>
    [Fact]
    public void Filtered_verification_declares_a_named_refusal_for_every_way_a_run_yields_no_evidence()
    {
        var verify = Read("scripts", "verify-change.py");

        // The evidence plumbing must be there for a refusal to be ABOUT anything.
        Assert.Contains("--logger", verify, StringComparison.Ordinal);
        Assert.Contains("--results-directory", verify, StringComparison.Ordinal);

        // The closed set of ways a run can yield nothing. Each is a refusal NAME, not a message.
        foreach (var refusal in new[]
                 {
                     "ZERO-TESTS",              // a zero-match dotnet filter is RED, not vacuously green
                     "TEST-EVIDENCE-AMBIGUOUS", // not exactly one TRX file is not evidence
                     "ZERO-PYTESTS",            // pytest collected nothing
                     "DIFF-FENCE-INCOMPLETE",   // a reviewed run needs both ends of the diff
                 })
        {
            Assert.Contains($"\"{refusal}\"", verify, StringComparison.Ordinal);
        }

        // A name that is DECLARED is not a name that is REACHABLE, so the diff fence is pinned by its
        // flag rather than by its table entry: the two spellings together are the whole contract.
        Assert.Contains("--diff-base-ref", verify, StringComparison.Ordinal);
        Assert.Contains("--diff-head-ref", verify, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_runner_rejects_an_empty_ci_catalog_instead_of_printing_green()
    {
        var root = Path.Combine(Path.GetTempPath(), "verification-topology-empty-guards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        File.WriteAllText(
            Path.Combine(root, "scripts", "enforcement-registry.v1.json"),
            "{\"schemaVersion\":1,\"guards\":{},\"invariants\":[]}");
        try
        {
            var (exit, stdout, stderr) = RunPython(new[]
            {
                Path.Combine(RepoRoot(), "scripts", "run_guards.py"),
                "--root", root, "--tier", "ci",
            });

            Assert.True(exit != 0, $"empty guard catalog was accepted\n{stdout}\n{stderr}");
            // CATALOG-EMPTY rather than the original's empty-SELECTION refusal: an empty `guards` object
            // is a REGISTRY integrity fault, and the port refuses it at that stage with a name for the
            // actual fault. Both are red with a non-zero exit; this one says which thing was wrong.
            Assert.Contains("CATALOG-EMPTY", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void Session_boundary_has_a_reviewed_diff_fence_in_ci_mode()
    {
        // The FLAGS are the subject of this half of the test, so it reads the tool's own text and
        // asserts the knob spellings. The Python tool's are kebab-case; asserting the PowerShell
        // spellings here would have read as a pass against a script that no longer parses them.
        // The session-boundary policy and its checker are gk-WORKFLOW's, not gk-core's: the records
        // live in `tasks/sessions/` at the workspace root and the checker reads them from there. This
        // read went through gk-core's root and failed with FileNotFoundException.
        var script = File.ReadAllText(Path.Combine(KeepverseRoots.Workspace(), "scripts", "session-boundary-check.py"));
        Assert.Contains("--diff-base-ref", script, StringComparison.Ordinal);
        Assert.Contains("--diff-head-ref", script, StringComparison.Ordinal);
        Assert.Contains("--require-diff-fence", script, StringComparison.Ordinal);
        Assert.Contains("outside session fence", script, StringComparison.OrdinalIgnoreCase);

        var root = Path.Combine(Path.GetTempPath(), "verification-topology-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "tasks", "sessions"));
        Directory.CreateDirectory(Path.Combine(root, "allowed"));
        try
        {
            File.WriteAllText(Path.Combine(root, "tasks", "sessions", "lane.json"),
                "{\"session\":\"lane\",\"program\":\"test\",\"problem\":\"fence\",\"mode\":\"direct\",\"branch\":\"main\",\"worktree\":null,\"paths\":[\"allowed/**\"],\"started\":\"2026-01-01T00:00:00Z\",\"status\":\"active\"}");
            File.WriteAllText(Path.Combine(root, "allowed", "ok.txt"), "before\n");
            File.WriteAllText(Path.Combine(root, "outside.txt"), "before\n");
            AssertGit(root, "init");
            AssertGit(root, "config", "user.email", "topology@example.invalid");
            AssertGit(root, "config", "user.name", "Topology Test");
            AssertGit(root, "add", ".");
            AssertGit(root, "commit", "-m", "base");
            File.WriteAllText(Path.Combine(root, "outside.txt"), "after\n");
            AssertGit(root, "add", "outside.txt");
            AssertGit(root, "commit", "-m", "escape");

            var (exit, stdout, stderr) = RunPython(new[]
            {
                Path.Combine(KeepverseRoots.Workspace(), "scripts", "session-boundary-check.py"),
                "--repo-root", root, "--ci", "--session", "lane",
                "--diff-base-ref", "HEAD~1", "--diff-head-ref", "HEAD", "--require-diff-fence",
            });

            Assert.True(exit != 0, $"out-of-fence reviewed diff was accepted\n{stdout}\n{stderr}");
            Assert.Contains("outside session fence", stdout + stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void Generated_seed_guard_sees_a_change_in_an_earlier_commit_of_the_range()
    {
        var root = Path.Combine(Path.GetTempPath(), "verification-topology-seed-range-" + Guid.NewGuid().ToString("N"));
        // The fixture belongs to `root`, which is the tree the guard is pointed at with --root. This
        // used to write into KeepverseRoots.Content() - the real gk-data pack - and then
        // `git add data/seed/items/row.json` inside `root`, where the file did not exist. Two
        // consequences: the guard saw no generated edit at all, and the `git add` failed, so the test
        // failed while leaving a 72-byte row.json behind in a PRIVATE repository, untracked and
        // undeleted, for every future run. Every other fixture in this file is built inside `root`
        // for the same reason.
        Directory.CreateDirectory(Path.Combine(root, "data", "seed", "items"));
        Directory.CreateDirectory(Path.Combine(root, "tools", "seedsmith", "seedsmith", "adapters", "items"));
        try
        {
            File.WriteAllText(Path.Combine(root, "data", "seed", "items", "row.json"),
                "{\"_meta\":{\"model\":\"fixture\",\"promptVersion\":1,\"batch\":\"one\"},\"value\":1}\n");
            File.WriteAllText(Path.Combine(root, "tools", "seedsmith", "seedsmith", "adapters", "items", "generator.py"),
                "# fixture generator\n");
            File.WriteAllText(Path.Combine(root, "unrelated.txt"), "base\n");
            AssertGit(root, "init");
            AssertGit(root, "config", "user.email", "topology@example.invalid");
            AssertGit(root, "config", "user.name", "Topology Test");
            AssertGit(root, "add", ".");
            AssertGit(root, "commit", "-m", "base");
            File.WriteAllText(Path.Combine(root, "data", "seed", "items", "row.json"),
                "{\"_meta\":{\"model\":\"fixture\",\"promptVersion\":1,\"batch\":\"one\"},\"value\":2}\n");
            AssertGit(root, "add", "data/seed/items/row.json");
            AssertGit(root, "commit", "-m", "generated edit");
            File.WriteAllText(Path.Combine(root, "unrelated.txt"), "later\n");
            AssertGit(root, "add", "unrelated.txt");
            AssertGit(root, "commit", "-m", "later unrelated edit");

            var (exit, stdout, stderr) = RunPython(new[]
            {
                Path.Combine(RepoRoot(), "scripts", "guard-generated-seed.py"),
                "--root", root, "--require-explicit-range", "--range", "HEAD~2..HEAD",
            });

            Assert.True(exit != 0, $"the earlier generated edit escaped the complete range\n{stdout}\n{stderr}");
            Assert.Contains("data/seed/items/row.json", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void Filtered_verify_change_rejects_a_zero_test_filter_in_execution()
    {
        var root = Path.Combine(Path.GetTempPath(), "verification-topology-zero-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "scripts", "lib"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Fake"));
        Directory.CreateDirectory(Path.Combine(root, "tests", "Fake"));
        try
        {
            foreach (var relative in new[]
                     {
                         "scripts/verify-change.py",
                         // The guard imports its lib, so the two are one unit in a fixture. Copying the
                         // PowerShell pair instead left a fixture whose guard could not even import.
                         "scripts/guard-verification-boundaries.py",
                         "scripts/lib/verification_boundaries.py",
                     })
            {
                var destination = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(RepoRoot(), relative), destination);
            }
            File.WriteAllText(Path.Combine(root, "scripts", "test_fast.py"),
                "FILTER = \"Category!=DiskSemantics&Category!=Heavy\"\n\n");
            File.WriteAllText(Path.Combine(root, "scripts", "enforcement-registry.v1.json"),
                "{\"schemaVersion\":1,\"guards\":{},\"invariants\":[]}");
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"),
                "{\"schemaVersion\":5,\"projects\":{\"fake\":\"tests/Fake/Fake.csproj\"},\"boundaries\":[{\"id\":\"fake-boundary\",\"kind\":\"owner\",\"paths\":[\"src/Fake/Sample.cs\"],\"project\":\"fake\",\"verificationId\":\"fake.missing\",\"guards\":[],\"level\":\"focused\"}]}\n");
            File.WriteAllText(Path.Combine(root, "src", "Fake", "Sample.cs"), "namespace Fake; public sealed class Sample { }\n");
            File.WriteAllText(Path.Combine(root, "tests", "Fake", "Fixture.cs"),
                "// [Trait(\"VerificationId\", \"fake.missing\")]\nnamespace Fake; public sealed class Fixture { }\n");
            File.WriteAllText(Path.Combine(root, "tests", "Fake", "Fake.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>\n");

            var (exit, stdout, stderr) = RunPlannerProcess(
                root, "--paths", "src/Fake/Sample.cs", "--root", root, "--allow-unscoped");

            Assert.True(exit != 0, $"zero-test verification unexpectedly succeeded\n{stdout}\n{stderr}");
            Assert.True(
                stdout.Contains("executed zero tests", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("executed zero tests", StringComparison.OrdinalIgnoreCase) ||
                stdout.Contains("TRX evidence", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("TRX evidence", StringComparison.OrdinalIgnoreCase),
                $"zero-test failure did not identify missing executed evidence\n{stdout}\n{stderr}");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void Operational_exemptions_are_explicit_and_never_catch_all_roots()
    {
        using var doc = JsonDocument.Parse(Read("scripts", "enforcement-registry.v1.json"));
        Assert.True(doc.RootElement.TryGetProperty("verificationExemptions", out var exemptions),
            "operational roots need an explicit exemption registry");
        foreach (var item in exemptions.EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("id").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("reason").GetString()));
            foreach (var path in item.GetProperty("paths").EnumerateArray())
            {
                var pattern = path.GetString() ?? "";
                Assert.False(
                    pattern.StartsWith("**", StringComparison.Ordinal) ||
                    Regex.IsMatch(pattern, @"^(scripts|tools|\.github)/\*"),
                    $"exemption is a top-level catch-all root: {pattern}");
                // The rule is OWNERSHIP, not a frozen prefix list. It used to be
                // `scripts/ | tools/ | .github/`, which was correct when one repository held
                // everything and became false the moment the split landed: the `f13-schema-upgrade-proof`
                // exemption names `tasks/reports/f13_schema_upgrade_proof.py`, and that file is
                // gk-workflow's, so the guard reported a real path as "escaping the operational roots"
                // for no reason other than that the list predates the layout.
                //
                // What the rule is actually protecting is a pattern that names nothing — an exemption for
                // a path no repository carries would be an entry that silently applies to a future file,
                // which is the catch-all the assertion above already rejects in the other direction. So
                // this asks the resolver, which is the thing that knows the layout, instead of repeating
                // the layout here where it cannot be kept true.
                // Every repository that could own a path, named explicitly.
                // `KeepverseRoots.Roots()` is NOT that list: it is the CONTENT accessor set (the pack and
                // the authored content), so it does not include gk-core itself and every `.github/` path
                // failed against it. This registry is workspace-wide in practice - gk-core holds the only
                // enforcement registry and its rows name files in gk-workflow - so the check has to ask
                // the same question the rows do.
                var roots = new[]
                {
                    RepoRoot(), KeepverseRoots.Core(), KeepverseRoots.Forge(), KeepverseRoots.Fusion(),
                    KeepverseRoots.Web(), KeepverseRoots.Workspace(),
                }
                .Concat(new[] { KeepverseRoots.AuthoredContent(), KeepverseRoots.Content() })
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
                var carrier = CarrierDirectory(pattern);
                Assert.True(
                    roots.Any(root => File.Exists(Path.Combine(root, carrier))
                                      || Directory.Exists(Path.Combine(root, carrier))),
                    $"exemption names a path no repository carries: {pattern} (looked for {carrier}). "
                    + $"A pattern that matches nothing is an entry waiting to apply to a future file. "
                    + $"Repositories consulted: {string.Join(", ", roots)}");
            }
        }
    }

    [Fact]
    public void Native_packaging_commands_are_each_failure_checked()
    {
        // The contract is STRUCTURAL, not textual. The PowerShell form read `$LASTEXITCODE` after each
        // native command, so whether a failure was noticed depended on nothing ELSE native having run in
        // between -- an invariant no substring assertion can check. The port funnels every external
        // command through one `run()` that raises on a non-zero exit, and THAT is what is asserted here:
        // one call site, and it is inside `run()`. Counting call sites proves the property; looking for
        // an idiom near a command only proves the idiom is still spelled the old way.
        var text = Read("scripts", "publish_player.py");
        var callSites = Regex.Matches(text, @"subprocess\.run\(");
        Assert.True(callSites.Count == 1,
            $"every external command must go through the one run() that raises on a non-zero exit; " +
            $"found {callSites.Count} subprocess.run call sites");

        var runDefinition = text.IndexOf("def run(", StringComparison.Ordinal);
        Assert.True(runDefinition >= 0, "the choke point is gone, so no failure check is structural");
        var runBody = text[runDefinition..];
        var runEnd = runBody.IndexOf("\ndef ", StringComparison.Ordinal);
        if (runEnd > 0) runBody = runBody[..runEnd];
        Assert.Contains("subprocess.run(", runBody, StringComparison.Ordinal);
        Assert.Contains("proc.returncode != 0", runBody, StringComparison.Ordinal);
        Assert.Contains("TimeoutExpired", runBody, StringComparison.Ordinal);

        // A Melon drop that produced no DLL is a REFUSAL. It was a `Write-Warning` in an earlier
        // revision, and a warning is invisible to a CI step reading the exit code.
        // A Melon drop that produced no DLL is a REFUSAL, so the run exits non-zero. It was a
        // `Write-Warning` in an earlier revision, and a warning is invisible to a CI step reading the
        // exit code. Asserted POSITIVELY: a `DoesNotContain("Write-Warning")` over the whole file cannot
        // be satisfied by a correct port, because the docstring is REQUIRED to name what it replaced.
        Assert.Contains("raise Refusal(\"melon\", \"OUTPUT-MISSING\"", text, StringComparison.Ordinal);
        // `node_modules` is a CODE token, not prose: the original short-circuited `npm ci` on it, which
        // is the one step that makes the pack reproducible, so its absence is meaningful here.
        Assert.DoesNotContain("node_modules", text, StringComparison.Ordinal);
        // NOTE: no DoesNotContain for the retired idiom's own names, deliberately. The port's docstring is
        // REQUIRED to name `$LASTEXITCODE` and `Assert-NativeExit` while explaining what it replaced, so
        // a whole-file scan for them is unsatisfiable by a correct port -- and leaving it in invites the
        // next author to delete the provenance instead of the assertion. The DIALECT check lives where it
        // is stronger and tests CODE: the contract suite's
        // test_neither_tool_shells_out_to_a_PowerShell_interpreter strips docstrings and comments by AST.

        var npmCiIndex = text.IndexOf("run([\"npm\", \"ci\"]", StringComparison.Ordinal);
        var webBuildIndex = text.IndexOf("run([\"npm\", \"run\", \"build\"]", StringComparison.Ordinal);
        Assert.True(npmCiIndex >= 0 && npmCiIndex < webBuildIndex,
            "publish-player must refresh the locked web dependency tree before building");
    }

    static void DeleteTree(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
            File.SetAttributes(directory, FileAttributes.Directory);
        Directory.Delete(root, recursive: true);
    }

    static void AssertGit(string root, params string[] args)
    {
        var (exit, stdout, stderr) = RunGit(root, args);
        Assert.True(exit == 0, $"git {string.Join(' ', args)} failed: {stdout}{stderr}");
    }

    /// <summary>
    /// The literal directory prefix of a repository-relative glob, for an existence check.
    /// </summary>
    /// <param name="pattern">A forward-slashed pattern, possibly with `*` and `/**`.</param>
    /// <remarks>
    /// A glob is not a path, so it cannot be tested with <c>File.Exists</c>; the deepest directory the
    /// pattern names is. `tools/ActionTimingProbe/**` yields `tools/ActionTimingProbe`, and
    /// `scripts/guard-*.py` yields `scripts` — the last segment is dropped whenever it carries a
    /// wildcard, because a directory named after half a glob is not a thing that exists.
    /// </remarks>
    static string CarrierDirectory(string pattern)
    {
        var segments = pattern.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var literal = new List<string>();
        foreach (var segment in segments)
        {
            if (segment.Contains('*', StringComparison.Ordinal)) break;
            literal.Add(segment);
        }
        if (literal.Count == segments.Length && literal.Count > 0) literal.RemoveAt(literal.Count - 1);
        return string.Join(Path.DirectorySeparatorChar, literal);
    }

}
