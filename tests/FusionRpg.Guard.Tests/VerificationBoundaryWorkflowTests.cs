using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// End-to-end contract tests for the path-owned local verification workflow. These invoke only
/// plan/static-script modes; they never select an application-wide test suite.
/// </summary>
[Trait("VerificationId", "guard.verification-boundaries")]
public sealed class VerificationBoundaryWorkflowTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "verify-change.py")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root with scripts/verify-change.py");
    }

    /// <summary>
    /// Runs the Python planner. The PowerShell form is retired: it took `-Paths a,b` as a PowerShell
    /// ARRAY, whereas argparse takes space-separated values -- so a flag rename that left the commas
    /// would have produced ONE path literally named "a,b", which resolves to nothing, and an empty plan
    /// reads as a scope refusal rather than as a bug.
    /// <para>
    /// This file NO LONGER SPAWNS A SHELL AT ALL. It had two PowerShell reasons to -- `test-fast` and
    /// `run-guards` -- and a third helper for the library probe, and all three are gone. The sibling
    /// `VerificationTopologyTests` still spawns `pwsh` for `scripts/run-guards.ps1`, which is unported;
    /// that is the ONE remaining PowerShell spawn in the guard project, and it is countable there rather
    /// than spread across files.
    /// </para>
    /// </summary>
    private static (int Exit, string Stdout, string Stderr) RunPlanner(string arguments, string? root = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"scripts/verify-change.py {arguments}",
            WorkingDirectory = root ?? RepoRoot(),
            CreateNoWindow = true
        };
        return ExternalProcess.Run(psi, 300_000, "verify-change.py timed out");
    }

    private static (int Exit, string Stdout, string Stderr) RunBoundaryGuard(string root) =>
        RunPythonBoundaryGuard(root);

    /// <summary>Runs the Python guard. The PowerShell twin used a 120s budget; the Python guard's own
    /// coverage walk walks every src/**, tests/**, tools/*.Tests/** file and four enforced roots, so it
    /// gets the longer budget the walk is measured at rather than the process-startup budget.</summary>
    /// <param name="root">The repository the guard checks.</param>
    /// <param name="guardPath">
    /// WHICH COPY of the guard to run. The default is the repository's own; a test that mutates the
    /// guard or its lib in a FIXTURE must pass the fixture's copy, because the guard resolves its lib
    /// relative to ITSELF (`Path(__file__).parent / "lib"`). Running the repository's copy against a
    /// fixture root silently ignores every fixture edit - which is what happened first: `S4` rewrote
    /// `ENFORCED_ROOTS` in the fixture's lib, the runner executed the repo's guard, the repo's
    /// `ENFORCED_ROOTS` was consulted instead, and the test recorded "the guard accepted an unmapped
    /// file" for a guard that was never asked the question.
    /// </param>
    private static (int Exit, string Stdout, string Stderr) RunPythonBoundaryGuard(
        string root, string? guardPath = null)
    {
        var script = guardPath ?? Path.Combine(RepoRoot(), "scripts", "guard-verification-boundaries.py");
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{script}\" --root \"{root}\"",
            WorkingDirectory = RepoRoot(),
            CreateNoWindow = true,
        };
        return ExternalProcess.Run(psi, 300_000, "verification-boundary guard timed out");
    }

    /// <summary>
    /// registry-contract C4: `pattern_match`, `valid_pattern_grammar`, `pattern_specificity` and
    /// `resolve_owner` are IMPORTED by both `verify-change.py` and
    /// `guard-verification-boundaries.py` from one file — this runs the SAME file, in isolation, over
    /// a small planted registry fragment (a JSON array of `{id, paths}` boundaries), so the exact
    /// shared logic is proven without needing a full, independently-valid registry tree. The temp
    /// script is removed in `finally` with a throwing delete (`testing-standard.md` R3).
    /// </summary>
    /// <summary>
    /// Runs a Python probe against the REAL `gk-core/scripts/lib/verification_boundaries.py`, imported by path.
    /// <para>
    /// This replaces a helper that dot-sourced `lib/VerificationBoundaries.ps1` and ran a PowerShell
    /// script. The probe now IMPORTS the library rather than inlining a copy of its rules, so a test
    /// cannot pass against a stale transcription -- which is the whole reason these six tests existed.
    /// </para>
    /// <para>
    /// The probe PRINTS the same tokens the PowerShell one did (`owners=`, `count=1 owners=`, `valid=`,
    /// `ok=`, `KNOWN RED ... -&gt; SR-99`), so the assertions below are unchanged. That is deliberate: a
    /// port that also rewrote the expected strings would have no way to distinguish a correct port from
    /// one that changed the ANSWER, and those strings are the observable contract.
    /// </para>
    /// </summary>
    private static (int Exit, string Stdout, string Stderr) RunLibProbe(string body)
    {
        var repo = RepoRoot();
        var tempScript = Path.Combine(Path.GetTempPath(), "vb-lib-probe-" + Guid.NewGuid().ToString("N") + ".py");
        File.WriteAllText(tempScript,
            "import importlib.util, json, sys\n"
            + "spec = importlib.util.spec_from_file_location('vb', 'scripts/lib/verification_boundaries.py')\n"
            // The sys.modules REGISTRATION is not optional. `@dataclass` resolves `cls.__module__` by
            // looking the module name up in sys.modules, so a spec-loaded module that is never registered
            // raises `AttributeError: 'NoneType' object has no attribute '__dict__'` at import -- a
            // dataclass in the library, not a defect in the probe.
            + "vb = importlib.util.module_from_spec(spec); sys.modules['vb'] = vb; spec.loader.exec_module(vb)\n"
            + body + "\n");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{tempScript}\"",
                WorkingDirectory = repo,
                CreateNoWindow = true
            };
            return ExternalProcess.Run(psi, 60_000, "verification_boundaries lib probe timed out");
        }
        finally
        {
            File.Delete(tempScript);
        }
    }

    [Fact]
    public void Planner_selects_only_the_socket_group_for_source_and_its_test()
    {
        var (exit, stdout, stderr) = RunPlanner(
            "--paths \"src/FusionRpg.Data/Sqlite/RpgStore.Sockets.cs\" \"tests/FusionRpg.Data.Tests/Items/ItemSocketStoreTests.cs\" --allow-unscoped --plan-only --format json");

        Assert.True(exit == 0, $"plan failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("data.item-socket", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("FusionRpg.Core.Tests", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("FusionRpg.Server.Tests", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The active session is a <b>reading</b>, not a constant. These two tests used to hardcode
    /// <c>-Session verification-boundaries-20260913-6f31</c>, so they broke the moment that record was
    /// closed — and its sibling then passed for the WRONG reason, asserting a non-zero exit that came
    /// from "session not active" rather than "outside session scope". Both now discover whichever
    /// record is active and assert the contract against it.
    /// </summary>
    private static (string Id, string[] Paths) ActiveSession()
    {
        var dir = Path.Combine(RepoRoot(), "tasks", "sessions");
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).StartsWith("_", StringComparison.Ordinal)) continue;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (!root.TryGetProperty("status", out var st) ||
                !string.Equals(st.GetString(), "active", StringComparison.OrdinalIgnoreCase)) continue;
            var paths = root.TryGetProperty("paths", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Array
                ? p.EnumerateArray().Select(e => e.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                : Array.Empty<string>();
            return (root.GetProperty("session").GetString()!, paths);
        }

        throw new InvalidOperationException(
            "no active session record — session-boundary-check.ps1 requires exactly one, so this is a " +
            "real defect in the repo state, not a reason to skip the test");
    }

    /// <summary>A concrete file the active session owns: a literal claimed path, else a code file under one of
    /// its `/**` roots, else any file under one of them. A documentation-only fence (`tasks/reports/**`, no
    /// `.cs` at all) is a legitimate session shape and the planner must accept its Markdown exactly as it
    /// accepts a test source — so the search PREFERS code but does not require it. Null means no claimed path
    /// resolves to any file; the caller skips such a session rather than failing the whole class, which is the
    /// behaviour <see cref="In_scope_file_search_prefers_a_code_file_but_accepts_a_documentation_fence"/> pins.</summary>
    private static string? InScopeFile((string Id, string[] Paths) session) => InScopeFile(RepoRoot(), session);

    /// <summary>The same rule against an arbitrary root, so a planted temp tree pins it without reading this
    /// repo's session records or counting its files (a population is a reading, never a contract).</summary>
    internal static string? InScopeFile(string root, (string Id, string[] Paths) session)
    {
        foreach (var claimed in session.Paths)
        {
            if (claimed.Contains('*')) continue;
            if (File.Exists(Path.Combine(root, claimed))) return claimed;
        }

        foreach (var claimed in session.Paths)
        {
            if (!claimed.EndsWith("/**", StringComparison.Ordinal)) continue;
            var sub = claimed[..^3];
            var abs = Path.Combine(root, sub);
            if (!Directory.Exists(abs)) continue;
            var files = Directory.EnumerateFiles(abs, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .ToList();
            var hit = files.FirstOrDefault(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                      ?? files.FirstOrDefault();
            if (hit is not null) return Path.GetRelativePath(root, hit).Replace(Path.DirectorySeparatorChar, '/');
        }

        return null;
    }

    /// <summary>The first ACTIVE session whose fence resolves to at least one file it may write, or null when
    /// none does. Selecting by resolvability is what lets a documentation-only lane participate: the rule is
    /// "a session may plan a path it can write", never "a session must own C#".</summary>
    private static (string Id, string[] Paths)? ActiveSessionWithScope()
    {
        var dir = Path.Combine(RepoRoot(), "tasks", "sessions");
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).StartsWith("_", StringComparison.Ordinal)) continue;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (!root.TryGetProperty("status", out var st) ||
                !string.Equals(st.GetString(), "active", StringComparison.OrdinalIgnoreCase)) continue;
            var paths = root.TryGetProperty("paths", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Array
                ? p.EnumerateArray().Select(e => e.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                : Array.Empty<string>();
            var candidate = (root.GetProperty("session").GetString()!, paths);
            if (InScopeFile(candidate) is not null) return candidate;
        }

        return null;
    }

    [Fact]
    public void In_scope_file_search_prefers_a_code_file_but_accepts_a_documentation_fence()
    {
        // A documentation-only fence (`tasks/reports/**`, no `.cs` anywhere) is a legitimate session shape:
        // the planner must accept its Markdown, so the search prefers a code file but does not require one.
        // Pinned on a planted temp tree — never on this repo's records, whose contents are a population.
        var root = Path.Combine(Path.GetTempPath(), "vb-inscope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "tasks", "reports"));
        try
        {
            File.WriteAllText(Path.Combine(root, "tasks", "reports", "note.md"), "# note\n");
            var docsOnly = (Id: "docs-only-lane", Paths: new[] { "tasks/reports/**" });
            Assert.Equal("tasks/reports/note.md", InScopeFile(root, docsOnly));

            File.WriteAllText(Path.Combine(root, "tasks", "reports", "Helper.cs"), "// code\n");
            Assert.Equal("tasks/reports/Helper.cs", InScopeFile(root, docsOnly));

            // A literal claimed path still wins, and a fence that resolves to nothing is null — the caller
            // skips that session instead of throwing for the whole class.
            Assert.Equal("tasks/reports/note.md",
                InScopeFile(root, (Id: "literal-lane", Paths: new[] { "tasks/absent/**", "tasks/reports/note.md" })));
            Assert.Null(InScopeFile(root, (Id: "empty-lane", Paths: new[] { "tasks/absent/**" })));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Planner_accepts_an_explicit_path_within_the_active_session_scope()
    {
        var session = ActiveSessionWithScope();
        // No active session declares a path it can write: nothing to plan. This is a skip of THIS case (the
        // rule itself is pinned by the fixture above), not an assertion about how many sessions exist.
        if (session is null) return;
        var inScope = InScopeFile(session.Value);
        Assert.NotNull(inScope);

        var (exit, stdout, stderr) = RunPlanner(
            $"--paths {inScope} --session {session.Value.Id} --plan-only");

        Assert.True(exit == 0, $"session-scoped plan failed for '{inScope}' exit={exit}; stdout: {stdout}; stderr: {stderr}");
    }

    /// <summary>
    /// A real repo file the given session does NOT claim, or null when its fence covers every candidate.
    /// </summary>
    /// <remarks>
    /// This exists because the test below used to hardcode <c>README.md</c> and assert it was unclaimed.
    /// That made the test a reading of the operator's live session list rather than of the planner: it
    /// passed on the machine where it was written and failed on the integration branch, where the first
    /// <c>active</c> record alphabetically is a manager session that legitimately claims README.md. A
    /// guard that passes by luck of machine state is a false green, which is worse than no guard.
    /// The candidate list is a closed vocabulary of stable root files, not a population, and the
    /// answer is derived from the session's own fence so it is correct for whatever session is active.
    /// </remarks>
    private static string? OutOfScopeFile((string Id, string[] Paths) session)
    {
        string[] candidates =
        {
            "README.md", "CONTRIBUTING.md", "CHANGELOG.md", "docs/README.md", "docs/CODEOWNERS"
        };
        return candidates.FirstOrDefault(candidate =>
            File.Exists(Path.Combine(RepoRoot(), candidate)) && !FenceClaims(session.Paths, candidate));
    }

    private static bool FenceClaims(string[] paths, string file)
    {
        foreach (var claimed in paths)
        {
            if (claimed == "*" || claimed == "**") return true;
            if (!claimed.Contains('*'))
            {
                if (claimed.Equals(file, StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }
            if (claimed.EndsWith("/**", StringComparison.Ordinal) &&
                file.StartsWith(claimed[..^2], StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(claimed.Trim('*', '/'), file, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    [Fact]
    public void Planner_rejects_a_path_outside_the_active_session_scope_before_testing()
    {
        var session = ActiveSession();

        // Chosen from the session's own fence, so the refusal can only be a scope refusal and the test
        // does not depend on which session happens to be active here.
        var outside = OutOfScopeFile(session);
        if (outside is null) return; // this session's fence covers every candidate: skip THIS case
        Assert.False(FenceClaims(session.Paths, outside), $"'{outside}' is inside the session fence");

        var (exit, stdout, stderr) = RunPlanner(
            $"--paths {outside} --session {session.Id} --plan-only");

        Assert.True(exit != 0, "out-of-session plan unexpectedly succeeded");
        // The refusal NAME. The PowerShell form's message was "outside session scope"; the port names it
        // PATH-OUTSIDE-SESSION, which is a closed-vocabulary entry rather than prose.
        Assert.Contains("VERIFY-CHANGE REFUSED", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("PATH-OUTSIDE-SESSION", stdout + stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// A repo file that the verification registry genuinely does NOT map, or null when every candidate
    /// is mapped.
    /// </summary>
    /// <remarks>
    /// These tests need an UNMAPPED path, and they used to hardcode <c>README.md</c> on the belief that
    /// the root README has no owner. That stopped being true when <c>README.md</c> joined the
    /// <c>docs-and-assistant-config</c> group, so the planner correctly returned a scope plan and both
    /// tests went red against correct behaviour — the same false red as
    /// <see cref="OutOfScopeFile"/>, one cause deeper: that helper answers "does the active session
    /// claim it", this one answers "does the registry map it", and a path can be unclaimed yet mapped.
    /// <para>
    /// The candidate list is a closed vocabulary of stable root files, not a population, and membership
    /// is decided by the registry itself rather than by a comment that can rot. A future session that
    /// maps one of these just narrows the list; it cannot make this test wrong.
    /// </para>
    /// </remarks>
    private static string? UnmappedRepoFile()
    {
        string[] candidates =
        {
            "CHANGELOG.md", "CONTRIBUTING.md", "LICENSE", "NOTICE.md",
            "docs/CODEOWNERS", "global.json", "Directory.Build.rsp"
        };
        return candidates.FirstOrDefault(candidate =>
            File.Exists(Path.Combine(RepoRoot(), candidate)) && !RegistryMaps(candidate));
    }

    /// <summary>Does any boundary group claim this path? Reads the registry the planner reads.</summary>
    private static bool RegistryMaps(string file)
    {
        var registry = Path.Combine(RepoRoot(), "scripts", "verification-boundaries.v1.json");
        if (!File.Exists(registry)) return false;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(registry));
        return doc.RootElement.GetProperty("boundaries").EnumerateArray().Any(boundary =>
            boundary.GetProperty("paths").EnumerateArray().Any(p =>
                FenceClaims(new[] { p.GetString() ?? "" }, file)));
    }

    [Fact]
    public void Planner_refuses_an_unmapped_path_instead_of_selecting_a_broad_suite()
    {
        // The example path must stay unmapped, so it is derived from the registry rather than
        // hardcoded: a hardcoded path became WRONG when README.md gained an owner.
        var unmapped = UnmappedRepoFile();
        if (unmapped is null) return; // every candidate is mapped now: skip THIS case, do not fake it

        var (exit, stdout, stderr) = RunPlanner(
            $"--paths {unmapped} --allow-unscoped --plan-only");

        Assert.True(exit != 0, $"unmapped path {unmapped} unexpectedly selected a test scope");
        // The refusal NAME, not message prose. The two implementations name this differently
        // (PowerShell said "VERIFICATION BOUNDARY MISSING"), and a name is the part of a refusal a
        // caller can branch on -- prose is not, and asserting it would break on a reword.
        Assert.Contains("VERIFY-CHANGE REFUSED", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("BOUNDARY-MISSING", stdout + stderr, StringComparison.Ordinal);
        Assert.True(stdout.Length == 0, $"a refusal must not also print a plan; got: {stdout}");
    }

    /// <summary>
    /// A REFUSAL must be silent on stdout, carry a NAME, and be non-zero — the three properties a caller
    /// can actually rely on.
    /// <para>
    /// THIS TEST USED TO BE A DIFFERENTIAL against the retired PowerShell planner, asserting
    /// <c>Assert.Equal(psExit, pyExit)</c> — the one fact the Python suite could not pin, because there
    /// it would have been comparing the tool against itself. That check is GONE, and its removal is a
    /// real loss rather than a simplification: a cross-implementation comparison is evidence no
    /// single-implementation suite can replace. It was retired because the oracle was the file this
    /// change deletes, and keeping the comparison would have meant keeping a 493-line PowerShell script
    /// alive solely to be compared against its own replacement.
    /// <para>
    /// What still pins equivalence is <c>gk-core/tests/tools/test_verify_change.py</c>'s own contract suite,
    /// which asserts the plan SHAPE and the resolution rules directly. That is a weaker guarantee than
    /// "two independent implementations agree", and the honest form of that is to say so here rather
    /// than to leave a test name that promises a comparison it no longer performs.
    /// </para>
    /// </summary>
    [Fact]
    public void A_refusal_is_silent_on_stdout_carries_a_name_and_exits_non_zero()
    {
        var unmapped = UnmappedRepoFile();
        if (unmapped is null) return;

        var (exit, stdout, stderr) = RunPlanner($"--paths {unmapped} --allow-unscoped --plan-only");

        Assert.True(exit != 0, $"the planner unexpectedly selected a test scope for {unmapped}");
        Assert.True(stdout.Length == 0, $"a refusal must not print a plan; got: {stdout}");
        Assert.Contains("VERIFY-CHANGE REFUSED", stderr, StringComparison.Ordinal);
        Assert.Contains("BOUNDARY-MISSING", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// The NAMED entry point is the Python tool. A doc that still instructs agents to run the retired
    /// PowerShell script is a defect an agent will hit on its next ordinary edit, and no process-level
    /// test would notice it — so the instruction files themselves are the assertion. Cost: no spawn.
    /// </summary>
    [Fact]
    public void The_documented_entry_point_is_the_python_planner()
    {
        var root = RepoRoot();
        Assert.True(File.Exists(Path.Combine(root, "scripts", "verify-change.py")),
            "scripts/verify-change.py is missing");
        Assert.True(File.Exists(Path.Combine(root, "scripts", "lib", "verification_boundaries.py")),
            "scripts/lib/verification_boundaries.py is missing");

        foreach (var instructionFile in new[] { "AGENTS.md", "CLAUDE.md" })
        {
            var text = File.ReadAllText(Path.Combine(root, instructionFile));
            Assert.Contains("verify-change.py", text, StringComparison.Ordinal);
            // STRENGTHENED, and the old version of this assertion was VACUOUS once the file was
            // deleted. It read "no line tells an agent to run the .ps1", which is trivially true of a
            // file that is not on disk -- the check could only ever pass. Now that the script is GONE,
            // any mention of the retired name is a defect whoever writes it, in any instruction file, in
            // command position or not: an agent that reads `verify-change.ps1` in a brief will try to run
            // it, and the failure it gets is a missing-file error with no guidance.
            //
            // THE TOKEN BELOW IS `.ps1` AND MUST STAY THAT WAY. A blanket search-and-replace of
            // `verify-change.ps1` -> `verify-change.py` run over this file afterwards rewrote it, which
            // INVERTED the assertion: it then forbade the LIVE tool's name, so it failed on every
            // legitimate mention and would have passed on exactly the defect it exists to catch. A
            // hand-written assertion and a mechanical rewrite must not share a file unchecked.
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.TrimStart();
                Assert.False(
                    trimmed.Contains("verify-change.ps1", StringComparison.Ordinal),
                    $"{instructionFile} still names the retired PowerShell planner: {trimmed}");
            }
        }
    }

    [Fact]
    public void Planner_can_select_a_registered_deleted_path_without_reading_the_filesystem()
    {
        var (exit, stdout, stderr) = RunPlanner(
            "--deleted-paths src/FusionRpg.Data/Sqlite/RpgStore.Sockets.cs --allow-unscoped --plan-only");

        Assert.True(exit == 0, $"deleted-path plan failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("data-item-socket", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Planner_requires_a_session_unless_an_explicit_maintainer_override_is_given()
    {
        var (exit, stdout, stderr) = RunPlanner(
            "--paths src/FusionRpg.Data/Sqlite/RpgStore.Sockets.cs --plan-only");

        Assert.True(exit != 0, "unscoped agent plan unexpectedly succeeded");
        // The refusal NAME. The PowerShell form's message was "-Session is required"; the port names
        // it SESSION-REQUIRED, which is the part a caller can branch on.
        Assert.Contains("VERIFY-CHANGE REFUSED", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("SESSION-REQUIRED", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Test_fast_requires_an_explicit_scope()
    {
        var (exit, stdout, stderr) = RunTestFast();

        Assert.True(exit != 0, "no-argument test-fast unexpectedly selected a broad default");
        // The refusal NAME, not message prose. The PowerShell form said "A path-scoped test requires
        // -Project"; the port names it SCOPE-REQUIRED, which is a closed-vocabulary entry a caller can
        // branch on and prose is not. The prose is checked too, but case-insensitively and only for its
        // distinguishing clause -- a reword must not be able to turn this red.
        Assert.Contains("TEST-FAST REFUSED", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("SCOPE-REQUIRED", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("path-scoped test requires", stdout + stderr, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs the Python default profile. This is the LAST reason the PowerShell spawn helper in this file
    /// existed -- <c>test-fast</c> and <c>run-guards</c> were its only two -- so the helper goes with the
    /// last of them rather than outliving its purpose as an unused private method.
    /// </summary>
    private static (int Exit, string Stdout, string Stderr) RunTestFast()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = "scripts/test_fast.py",
            WorkingDirectory = RepoRoot(),
            CreateNoWindow = true
        };
        return ExternalProcess.Run(psi, 120_000, "test_fast.py scope check timed out");
    }

    [Fact]
    public void Integrity_guard_passes_on_the_current_registry()
    {
        var (exit, stdout, stderr) = RunBoundaryGuard(RepoRoot());

        Assert.True(exit == 0, $"guard failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("VERIFICATION BOUNDARY GUARD OK", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Integrity_guard_rejects_a_selector_that_matches_no_test_trait()
    {
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Fake"));
            Directory.CreateDirectory(Path.Combine(root, "tests", "Fake"));
            File.WriteAllText(Path.Combine(root, "src", "Fake", "Sample.cs"), "namespace Fake; public sealed class Sample { }");
            File.WriteAllText(Path.Combine(root, "tests", "Fake", "Fake.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "scripts", "fake-guard.ps1"), "exit 0");
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": { "fake": "tests/Fake/Fake.csproj" },
                  "boundaries": [
                    { "id": "fake-boundary", "kind": "owner", "paths": ["src/Fake/**"], "project": "fake", "verificationId": "fake.missing", "guards": [], "level": "focused" }
                  ]
                }
                """);

            var (exit, stdout, stderr) = RunBoundaryGuard(root);
            Assert.True(exit != 0, "guard accepted a zero-match VerificationId");
            Assert.Contains("VerificationId has no matching test trait: fake.missing", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ── C1: tests/**/*.cs and tools/*.Tests/**/*.cs become enforced roots, exactly like src/** ──────

    [Fact]
    public void T1_an_unmapped_test_source_file_fails_the_guard()
    {
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-t1-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            Directory.CreateDirectory(Path.Combine(root, "tests", "Fake.Tests"));
            File.WriteAllText(Path.Combine(root, "tests", "Fake.Tests", "Fake.Tests.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "tests", "Fake.Tests", "Foo.cs"), "namespace Fake; public sealed class Foo { }");
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": { "fake": "tests/Fake.Tests/Fake.Tests.csproj" },
                  "boundaries": []
                }
                """);

            var (exit, stdout, stderr) = RunBoundaryGuard(root);
            Assert.True(exit != 0, "guard accepted an unmapped test source file");
            Assert.Contains("unmapped test source: tests/Fake.Tests/Foo.cs", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ── C2: every *.Tests.csproj under tests/ or tools/ is registered or exempt with a reason ───────

    [Fact]
    public void T2_a_test_project_absent_from_projects_and_exemptions_fails_the_guard()
    {
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-t2-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            Directory.CreateDirectory(Path.Combine(root, "tests", "Fake.Tests"));
            File.WriteAllText(Path.Combine(root, "tests", "Fake.Tests", "Fake.Tests.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": {},
                  "boundaries": []
                }
                """);

            var (exit, stdout, stderr) = RunBoundaryGuard(root);
            Assert.True(exit != 0, "guard accepted an unregistered, non-exempt test project");
            Assert.Contains("test project not in registry: tests/Fake.Tests/Fake.Tests.csproj", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ── T3: an owner boundary with a VerificationId but level: module -> the guard fails ────────────

    [Fact]
    public void T3_a_level_that_disagrees_with_its_own_selector_is_rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-t3-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Fake"));
            Directory.CreateDirectory(Path.Combine(root, "tests", "Fake"));
            File.WriteAllText(Path.Combine(root, "src", "Fake", "Sample.cs"), "namespace Fake; public sealed class Sample { }");
            File.WriteAllText(Path.Combine(root, "tests", "Fake", "Fake.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "tests", "Fake", "SampleTests.cs"),
                "namespace Fake;\n[Xunit.Trait(\"VerificationId\", \"fake.sample\")]\npublic sealed class SampleTests { }");
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": { "fake": "tests/Fake/Fake.csproj" },
                  "boundaries": [
                    { "id": "fake-boundary", "kind": "owner", "paths": ["src/Fake/Sample.cs"], "project": "fake", "verificationId": "fake.sample", "guards": [], "level": "module" }
                  ]
                }
                """);

            var (exit, stdout, stderr) = RunBoundaryGuard(root);
            Assert.True(exit != 0, "guard accepted a level that disagrees with its own selector");
            Assert.Contains("level mismatch: fake-boundary", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ── T13: an exact owner pattern naming a file that is then deleted -> the guard starts passing, ─
    // ──       then fails with "stale exact path" ────────────────────────────────────────────────────

    [Fact]
    public void T13_an_exact_path_that_stops_existing_fails_as_stale()
    {
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-t13-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Fake"));
            Directory.CreateDirectory(Path.Combine(root, "tests", "Fake"));
            var samplePath = Path.Combine(root, "src", "Fake", "Sample.cs");
            File.WriteAllText(samplePath, "namespace Fake; public sealed class Sample { }");
            File.WriteAllText(Path.Combine(root, "tests", "Fake", "Fake.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "scripts", "enforcement-registry.v1.json"), """{"schemaVersion":1,"guards":{},"invariants":[]}""");
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": { "fake": "tests/Fake/Fake.csproj" },
                  "boundaries": [
                    { "id": "fake-boundary", "kind": "owner", "paths": ["src/Fake/Sample.cs"], "project": "fake", "guards": [], "level": "module" }
                  ]
                }
                """);

            var (beforeExit, beforeStdout, beforeStderr) = RunBoundaryGuard(root);
            Assert.True(beforeExit == 0, $"guard failed while the exact path still exists: {beforeStdout}{beforeStderr}");

            File.Delete(samplePath);

            var (afterExit, afterStdout, afterStderr) = RunBoundaryGuard(root);
            Assert.True(afterExit != 0, "guard accepted a stale exact path");
            Assert.Contains("stale exact path: fake-boundary: src/Fake/Sample.cs", afterStdout + afterStderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ── C7: project groups — a `projects` value may be an array of `.csproj` paths ──────────────────

    /// <summary>A group `g` = two planted csprojs (`FakeA` with no trait, `FakeB` carrying
    /// `fake.widget`), plus one focused boundary and one module boundary on the same group, so T10
    /// and T11 share a fixture.</summary>
    private static string PlantGroupFixture()
    {
        var repo = RepoRoot();
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-c7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "scripts", "lib"));
        Directory.CreateDirectory(Path.Combine(root, "src", "FakeArea"));
        Directory.CreateDirectory(Path.Combine(root, "tests", "FakeA"));
        Directory.CreateDirectory(Path.Combine(root, "tests", "FakeB"));
        // verify-change.py resolves its own integrity-guard script and lib from --root itself (not
        // from this test file's own $PSScriptRoot), so a synthetic root needs real copies of the
        // scripts under test — proving the shipped files, not a re-implementation of them.
        File.Copy(Path.Combine(repo, "scripts", "verify-change.py"), Path.Combine(root, "scripts", "verify-change.py"));
        // The Python guard IMPORTS its lib, so a fixture that copies only the guard fails at import.
        // The PowerShell pair was self-contained because the .ps1 dot-sourced its lib; the Python pair
        // has to be copied as a set or the guard is not runnable in the fixture at all.
        File.Copy(Path.Combine(repo, "scripts", "guard-verification-boundaries.py"), Path.Combine(root, "scripts", "guard-verification-boundaries.py"));
        File.Copy(Path.Combine(repo, "scripts", "lib", "verification_boundaries.py"), Path.Combine(root, "scripts", "lib", "verification_boundaries.py"));
        File.WriteAllText(Path.Combine(root, "scripts", "test_fast.py"), "FILTER = \"Category!=DiskSemantics&Category!=Heavy\"\n");
        File.WriteAllText(Path.Combine(root, "src", "FakeArea", "Widget.cs"), "namespace FakeArea; public sealed class Widget { }");
        File.WriteAllText(Path.Combine(root, "src", "FakeArea", "Other.cs"), "namespace FakeArea; public sealed class Other { }");
        File.WriteAllText(Path.Combine(root, "tests", "FakeA", "FakeA.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root, "tests", "FakeA", "SomeTests.cs"), "namespace FakeA; public sealed class SomeTests { }");
        File.WriteAllText(Path.Combine(root, "tests", "FakeB", "FakeB.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root, "tests", "FakeB", "SomeTests.cs"),
            "namespace FakeB;\nusing Xunit;\n[Trait(\"VerificationId\", \"fake.widget\")]\npublic sealed class SomeTests { }");
        File.WriteAllText(Path.Combine(root, "scripts", "enforcement-registry.v1.json"), """{"schemaVersion":1,"guards":{},"invariants":[]}""");
        File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
            {
              "schemaVersion": 5,
              "projects": { "g": ["tests/FakeA/FakeA.csproj", "tests/FakeB/FakeB.csproj"] },
              "boundaries": [
                { "id": "focused-on-group", "kind": "owner", "paths": ["src/FakeArea/Widget.cs"], "project": "g", "verificationId": "fake.widget", "guards": [], "level": "focused" },
                { "id": "module-on-group", "kind": "owner", "paths": ["src/FakeArea/Other.cs"], "project": "g", "guards": [], "level": "module" },
                { "id": "fakea-tests-fallback", "kind": "owner", "paths": ["tests/FakeA/**"], "project": "g", "guards": [], "level": "module" },
                { "id": "fakeb-tests-fallback", "kind": "owner", "paths": ["tests/FakeB/**"], "project": "g", "guards": [], "level": "module" }
              ]
            }
            """);
        return root;
    }

    [Fact]
    public void T10_a_focused_boundary_on_a_group_plans_only_the_member_holding_the_trait()
    {
        var root = PlantGroupFixture();
        try
        {
            var (exit, stdout, stderr) = RunPlanner(
                $"--paths src/FakeArea/Widget.cs --root \"{root}\" --allow-unscoped --plan-only --format json");

            Assert.True(exit == 0, $"exit={exit}\nstdout:{stdout}\nstderr:{stderr}");
            Assert.Contains("tests/FakeB/FakeB.csproj", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("tests/FakeA/FakeA.csproj", stdout, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void T11_a_module_boundary_on_a_group_plans_every_member()
    {
        var root = PlantGroupFixture();
        try
        {
            var (exit, stdout, stderr) = RunPlanner(
                $"--paths src/FakeArea/Other.cs --root \"{root}\" --allow-unscoped --plan-only --format json");

            Assert.True(exit == 0, $"exit={exit}\nstdout:{stdout}\nstderr:{stderr}");
            Assert.Contains("tests/FakeA/FakeA.csproj", stdout, StringComparison.Ordinal);
            Assert.Contains("tests/FakeB/FakeB.csproj", stdout, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ── T12: a group nested in a group, or a non-.csproj member -> the guard rejects it ─────────────

    [Theory]
    [InlineData("""["tests/FakeA/FakeA.csproj", ["tests/FakeB/FakeB.csproj"]]""")]   // a group nested in a group
    [InlineData("""["tests/FakeA/FakeA.csproj", {"runner": "pytest", "root": "x"}]""")] // a non-.csproj (future pytest-shaped) member
    public void T12_a_malformed_group_member_is_rejected(string groupJson)
    {
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-t12-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            Directory.CreateDirectory(Path.Combine(root, "src", "FakeArea"));
            Directory.CreateDirectory(Path.Combine(root, "tests", "FakeA"));
            Directory.CreateDirectory(Path.Combine(root, "tests", "FakeB"));
            File.WriteAllText(Path.Combine(root, "src", "FakeArea", "Widget.cs"), "namespace FakeArea; public sealed class Widget { }");
            File.WriteAllText(Path.Combine(root, "tests", "FakeA", "FakeA.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "tests", "FakeB", "FakeB.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "scripts", "enforcement-registry.v1.json"), """{"schemaVersion":1,"guards":{},"invariants":[]}""");
            // Plain concatenation, not a $$"""...""" raw-string interpolation: `groupJson` itself
            // contains JSON braces, and a literal `}` immediately after a `{{hole}}` needs more `$`
            // than this file uses elsewhere — string concatenation sidesteps the question entirely.
            var registryJson = "{\"schemaVersion\":5,\"projects\":{\"g\":" + groupJson + "}," +
                "\"boundaries\":[{\"id\":\"bad-group-boundary\",\"kind\":\"owner\"," +
                "\"paths\":[\"src/FakeArea/Widget.cs\"],\"project\":\"g\",\"guards\":[],\"level\":\"module\"}]}";
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), registryJson);

            var (exit, stdout, stderr) = RunBoundaryGuard(root);
            Assert.True(exit != 0, "guard accepted a malformed project group member");
            Assert.Contains("project group member is not a .csproj path", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ── C5: `project` is optional iff `guards` is non-empty (a guard-only boundary) ─────────────────

    [Fact]
    public void T7_a_guard_only_boundary_plans_the_guard_and_no_test_check()
    {
        var repo = RepoRoot();
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-t7-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts", "lib"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Bench"));
            File.Copy(Path.Combine(repo, "scripts", "verify-change.py"), Path.Combine(root, "scripts", "verify-change.py"));
            // The guard and its lib travel together: the Python guard imports
            // `lib/verification_boundaries.py`, so copying one without the other leaves a fixture whose
            // guard cannot start.
            File.Copy(Path.Combine(repo, "scripts", "guard-verification-boundaries.py"), Path.Combine(root, "scripts", "guard-verification-boundaries.py"));
            File.Copy(Path.Combine(repo, "scripts", "lib", "verification_boundaries.py"), Path.Combine(root, "scripts", "lib", "verification_boundaries.py"));
            File.WriteAllText(Path.Combine(root, "scripts", "test_fast.py"), "FILTER = \"Category!=DiskSemantics&Category!=Heavy\"\n");
            File.WriteAllText(Path.Combine(root, "scripts", "fake-guard.ps1"), "Write-Host 'fake guard ran'; exit 0");
            File.WriteAllText(Path.Combine(root, "src", "Bench", "Program.cs"), "namespace Bench; public sealed class Program { }");
            File.WriteAllText(Path.Combine(root, "scripts", "enforcement-registry.v1.json"),
                """{"schemaVersion":1,"guards":{"fake-guard":{"script":"scripts/fake-guard.ps1","tier":"local","status":"gating","localReason":"fixture"}},"invariants":[]}""");
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": {},
                  "boundaries": [
                    { "id": "guard-only-boundary", "kind": "owner", "paths": ["src/Bench/Program.cs"], "guards": ["fake-guard"], "level": "module" }
                  ]
                }
                """);

            var (exit, stdout, stderr) = RunPlanner(
                $"--paths src/Bench/Program.cs --root \"{root}\" --allow-unscoped --plan-only");

            Assert.True(exit == 0, $"exit={exit}\nstdout:{stdout}\nstderr:{stderr}");
            Assert.Contains("guard: fake-guard", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("test:", stdout, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void T8_a_boundary_with_neither_project_nor_guards_is_rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-t8-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Bench"));
            File.WriteAllText(Path.Combine(root, "src", "Bench", "Program.cs"), "namespace Bench; public sealed class Program { }");
            File.WriteAllText(Path.Combine(root, "scripts", "enforcement-registry.v1.json"), """{"schemaVersion":1,"guards":{},"invariants":[]}""");
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": {},
                  "boundaries": [
                    { "id": "bare-boundary", "kind": "owner", "paths": ["src/Bench/Program.cs"], "guards": [], "level": "module" }
                  ]
                }
                """);

            var (exit, stdout, stderr) = RunBoundaryGuard(root);
            Assert.True(exit != 0, "guard accepted a boundary with neither a project nor a guard");
            Assert.Contains("boundary needs a project or at least one guard: bare-boundary", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// solid-remediation T1.7/T1.8 (register entry G3). Both tool trees had no boundary at all, so a
    /// change inside them fell through to "run everything" or "run nothing" — and under `AGENTS.md` an
    /// unmapped production path is itself a verification-boundary defect.
    ///
    /// <para>These assert the mapping <b>contract</b>: the path resolves to its own boundary and selects
    /// that boundary's test id. They never assert how many tests get selected — that is a population
    /// which grows whenever a test ships.</para>
    /// </summary>
    [Theory]
    [InlineData("tools/CombatSim/Analytic.cs", "tools-combat-sim", "core.combat-sim")]
    [InlineData("tools/ProvePredictor/Program.cs", "tools-prove-predictor", "core.prove-predictor")]
    public void A_back_end_tool_tree_selects_its_own_boundary(string path, string boundaryId, string verificationId)
    {
        // A mapping contract, like its boundary-asserting siblings above: the tool trees belong to
        // whichever session is building them, so the plan is unscoped deliberately. Passing the
        // active session's id here coupled "the path resolves to its own boundary" to that one
        // record's `paths` and went red whenever the active record did not happen to claim
        // gk-core/tools/CombatSim/** or gk-core/tools/ProvePredictor/** (seen 2026-09-19). Session-scope acceptance
        // has its own two tests below.
        var (exit, stdout, stderr) = RunPlanner(
            $"--paths {path} --allow-unscoped --plan-only");

        Assert.True(exit == 0, $"tool-tree plan failed for '{path}' exit={exit}; stdout: {stdout}; stderr: {stderr}");
        Assert.Contains(boundaryId, stdout, StringComparison.Ordinal);
        Assert.Contains(verificationId, stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unmapped_tool_tree_still_refuses_rather_than_selecting_nothing()
    {
        // The whole point of mapping two trees is that the third stays a refusal, not a silent pass.
        // A boundary that quietly ignores what it does not know is how the gap appeared in the first
        // place.
        var (exit, stdout, stderr) = RunPlanner(
            "--paths tools/HybridViability/Program.cs --allow-unscoped --plan-only");

        Assert.True(exit != 0, "an unmapped tool path selected a scope");
        // The refusal NAME. The PowerShell form said "VERIFICATION BOUNDARY MISSING"; the port
        // names it BOUNDARY-MISSING, which is the part a caller can branch on.
        Assert.Contains("VERIFY-CHANGE REFUSED", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("BOUNDARY-MISSING", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void A_deleted_file_in_a_mapped_tool_tree_still_resolves_its_former_boundary()
    {
        var (exit, stdout, stderr) = RunPlanner(
            "--deleted-paths tools/CombatSim/Analytic.cs --allow-unscoped --plan-only");

        Assert.True(exit == 0, $"deleted tool path plan failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("combat-sim", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// data-tests-sharding (TVB1.5): a module-level check on a project the shard manifest owns
    /// (`data`) plans through the sharded runner instead of one dotnet test process, so a local
    /// `data-fallback` change gets the same process-boundary parallelism CI uses.
    /// </summary>
    [Fact]
    public void A_data_fallback_module_check_plans_the_sharded_runner()
    {
        var (exit, stdout, stderr) = RunPlanner(
            "--paths src/FusionRpg.Data/Sqlite/RpgStore.cs --allow-unscoped --plan-only");

        Assert.True(exit == 0, $"plan failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("data-fallback", stdout, StringComparison.Ordinal);
        Assert.Contains("(sharded runner)", stdout, StringComparison.Ordinal);
    }

    /// <summary>A focused (VerificationId) check on the same project stays a plain dotnet test — it
    /// is small, and shard bookkeeping (build a manifest, spawn N processes) would be pure overhead.</summary>
    [Fact]
    public void A_focused_data_check_does_not_plan_the_sharded_runner()
    {
        var (exit, stdout, stderr) = RunPlanner(
            "--paths src/FusionRpg.Data/Sqlite/RpgStore.Sockets.cs --allow-unscoped --plan-only");

        Assert.True(exit == 0, $"plan failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("data.item-socket", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("(sharded runner)", stdout, StringComparison.Ordinal);
    }

    // ── T4: a wildcard owner + a broader `/**` owner -> the planner selects the wildcard one ────────

    [Fact]
    public void T4_a_wildcard_pattern_beats_a_broader_double_star_owner()
    {
        const string probe = """
            boundaries = [{"id":"wild","paths":["data/tuning/foo.v*.json"]},
                          {"id":"broad","paths":["data/tuning/**"]}]
            r = vb.resolve_owner('data/tuning/foo.v2.json', boundaries)
            print('owners=' + ','.join(o['id'] for o in r.owners))
            """;
        var (exit, stdout, stderr) = RunLibProbe(probe);

        Assert.True(exit == 0, $"exit={exit}\nstdout:{stdout}\nstderr:{stderr}");
        Assert.Contains("owners=wild", stdout, StringComparison.Ordinal);
    }

    // ── T5: an exact path + a same-target wildcard on DIFFERENT owners -> exact wins, no AMBIGUOUS ──

    [Fact]
    public void T5_an_exact_pattern_beats_a_matching_wildcard_pattern_no_ambiguity()
    {
        const string probe = """
            boundaries = [{"id":"exact","paths":["data/tuning/foo.v2.json"]},
                          {"id":"wild","paths":["data/tuning/foo.v*.json"]}]
            r = vb.resolve_owner('data/tuning/foo.v2.json', boundaries)
            print(f"count={len(r.owners)} owners=" + ','.join(o['id'] for o in r.owners))
            """;
        var (exit, stdout, stderr) = RunLibProbe(probe);

        Assert.True(exit == 0, $"exit={exit}\nstdout:{stdout}\nstderr:{stderr}");
        Assert.Contains("count=1 owners=exact", stdout, StringComparison.Ordinal);
    }

    // ── T6: a '*' in a non-final segment is invalid grammar -> the guard rejects the pattern ────────

    [Theory]
    [InlineData("data/*/tuning/foo.json", false)]      // '*' in a non-final segment
    [InlineData("data/tuning/foo.v*.json", true)]       // '*' confined to the final segment
    [InlineData("data/tuning/**", true)]                // the unrelated '/**' form
    [InlineData("data/tuning/foo.json", true)]          // no wildcard at all
    public void T6_pattern_grammar_confines_a_wildcard_to_the_final_segment(string pattern, bool expectedValid)
    {
        var probe = $"print('valid=' + str(vb.valid_pattern_grammar({System.Text.Json.JsonSerializer.Serialize(pattern)})))";
        var (exit, stdout, stderr) = RunLibProbe(probe);

        Assert.True(exit == 0, $"exit={exit}\nstdout:{stdout}\nstderr:{stderr}");
        Assert.Contains($"valid={expectedValid}", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T6_the_real_guard_rejects_a_planted_pattern_with_a_wildcard_in_a_non_final_segment()
    {
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-t6-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Fake"));
            Directory.CreateDirectory(Path.Combine(root, "tests", "Fake"));
            File.WriteAllText(Path.Combine(root, "src", "Fake", "Sample.cs"), "namespace Fake; public sealed class Sample { }");
            File.WriteAllText(Path.Combine(root, "tests", "Fake", "Fake.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": { "fake": "tests/Fake/Fake.csproj" },
                  "boundaries": [
                    { "id": "fake-boundary", "kind": "owner", "paths": ["src/*/Fake/**"], "project": "fake", "guards": [], "level": "module" }
                  ]
                }
                """);

            var (exit, stdout, stderr) = RunBoundaryGuard(root);
            Assert.True(exit != 0, "guard accepted a wildcard in a non-final segment");
            Assert.Contains("invalid boundary path", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ── Contract version: schemaVersion 5 (registry-contract C3, C4, C5, C7, C8; seam-coverage S3) ──

    /// <summary>
    /// Both the guard and the planner accept exactly `AcceptedVerificationSchemaVersion` (currently
    /// 5): a stale registry that still says 2 refuses instead of being misread under the newer
    /// contract (C3-C5, C7-C8, and seam-coverage's `full` level change what a reader must
    /// understand). `verify-change.py` delegates its own integrity
    /// check to the guard before reading `schemaVersion` itself, so one planted registry proves both
    /// scripts refuse it — the planted root needs real copies of `verify-change.py`,
    /// `guard-verification-boundaries.py` and the lib because `verify-change.py` resolves its own
    /// tooling from `-Root`, not `$PSScriptRoot`.
    /// </summary>
    [Fact]
    public void T14_both_scripts_refuse_a_stale_schemaVersion_2_registry()
    {
        var repo = RepoRoot();
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-t14-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts", "lib"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Fake"));
            Directory.CreateDirectory(Path.Combine(root, "tests", "Fake"));
            File.Copy(Path.Combine(repo, "scripts", "verify-change.py"), Path.Combine(root, "scripts", "verify-change.py"));
            // The guard and its lib travel together: the Python guard imports
            // `lib/verification_boundaries.py`, so copying one without the other leaves a fixture whose
            // guard cannot start.
            File.Copy(Path.Combine(repo, "scripts", "guard-verification-boundaries.py"), Path.Combine(root, "scripts", "guard-verification-boundaries.py"));
            File.Copy(Path.Combine(repo, "scripts", "lib", "verification_boundaries.py"), Path.Combine(root, "scripts", "lib", "verification_boundaries.py"));
            File.WriteAllText(Path.Combine(root, "scripts", "test_fast.py"), "FILTER = \"Category!=DiskSemantics&Category!=Heavy\"\n");
            File.WriteAllText(Path.Combine(root, "src", "Fake", "Sample.cs"), "namespace Fake; public sealed class Sample { }");
            File.WriteAllText(Path.Combine(root, "tests", "Fake", "Fake.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "scripts", "enforcement-registry.v1.json"), """{"schemaVersion":1,"guards":{},"invariants":[]}""");
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 2,
                  "projects": { "fake": "tests/Fake/Fake.csproj" },
                  "boundaries": [
                    { "id": "fake-boundary", "kind": "owner", "paths": ["src/Fake/Sample.cs"], "project": "fake", "guards": [], "level": "module" }
                  ]
                }
                """);

            var (guardExit, guardStdout, guardStderr) = RunBoundaryGuard(root);
            Assert.True(guardExit != 0, "guard accepted a stale schemaVersion 2 registry");
            Assert.Contains("unsupported schemaVersion", guardStdout + guardStderr, StringComparison.Ordinal);

            var (planExit, planStdout, planStderr) = RunPlanner(
                $"--paths src/Fake/Sample.cs --root \"{root}\" --allow-unscoped --plan-only");
            Assert.True(planExit != 0, "planner accepted a stale schemaVersion 2 registry");
            // The refusal NAME. The PowerShell form's message was "verification registry integrity guard
            // failed"; the port names it INTEGRITY-GUARD-FAILED, which is a closed-vocabulary entry a
            // caller can branch on and which prose is not.
            Assert.Contains("INTEGRITY-GUARD-FAILED", planStdout + planStderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ── T15 + the other C5 real-registry consumer: `bench` and `magic-number-audit` are guard-only ──

    /// <summary>
    /// registry-contract C5's first real consumer: `gk-core/tests/FusionRpg.Bench` is an Exe with no tests, so
    /// its boundary carries `guards: ["bench-compile"]` and no `project` at all. The plan must show the
    /// guard and nothing pretending to be a test check.
    /// </summary>
    [Fact]
    public void T15_the_real_bench_boundary_plans_its_compile_guard_and_no_test_check()
    {
        var (exit, stdout, stderr) = RunPlanner(
            "--paths tests/FusionRpg.Bench/Program.cs --allow-unscoped --plan-only");

        Assert.True(exit == 0, $"plan failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("guard: bench-compile", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("test:", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second C5 consumer: `magic-number-audit` names project `guard`, but no Guard test reads
    /// either of its two scripts (map G15) — its only honest check is its own `magic-numbers` guard, so
    /// it drops `project` entirely rather than mapping to a test that never exercises it.
    ///
    /// The path is the guard's REAL script. It used to name `scripts/guard-magic-numbers.ps1`, a
    /// shim retired when the guard became Python; the shim's path stayed in this test and
    /// verify-change correctly refused it with "path does not exist", which is a plan that cannot
    /// be produced rather than a boundary that changed. The assertion is unchanged and still holds.
    /// </summary>
    [Fact]
    public void The_real_magic_number_audit_boundary_is_guard_only()
    {
        var (exit, stdout, stderr) = RunPlanner(
            "--paths scripts/audit-magic-numbers.py --allow-unscoped --plan-only");

        Assert.True(exit == 0, $"plan failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("guard: magic-numbers", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("test:", stdout, StringComparison.Ordinal);
    }

    // ── python-test-lane D1/D2: object-shaped projects, the closed `runner` vocabulary, and the ────
    // ──   testFiles/verificationId/selfSelect pairing rules ──────────────────────────────────────

    /// <summary>
    /// D1: `runner` is a closed vocabulary of exactly three - `dotnet` (implicit for a plain
    /// string/array), `pytest`, `script`. A fourth is a reviewed change to this list, never data: no
    /// runner branch exists to execute it, so accepting an unknown value would only defer the failure
    /// to execution time with a worse error.
    /// </summary>
    [Fact]
    public void P5_an_unsupported_runner_value_is_rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-p5-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": { "fakepy": { "runner": "nose", "root": "tools/fakepy", "tests": "tests" } },
                  "boundaries": []
                }
                """);

            var (exit, stdout, stderr) = RunBoundaryGuard(root);
            Assert.True(exit != 0, "guard accepted an unsupported runner value");
            Assert.Contains("unsupported runner: fakepy: nose", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A planted pytest project (`tools/fakepy`, root + `tests/` real on disk) shared by P3
    /// and P4a.</summary>
    private static string PlantPytestProjectFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-pytest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        Directory.CreateDirectory(Path.Combine(root, "tools", "fakepy", "tests"));
        File.WriteAllText(Path.Combine(root, "tools", "fakepy", "tests", "test_real.py"), "def test_real(): pass\n");
        return root;
    }

    [Fact]
    public void P3_a_testFiles_pattern_matching_no_real_test_file_is_rejected()
    {
        var root = PlantPytestProjectFixture();
        try
        {
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": { "fakepy": { "runner": "pytest", "root": "tools/fakepy", "tests": "tests" } },
                  "boundaries": [
                    { "id": "fakepy-boundary", "kind": "owner", "paths": ["tools/fakepy/**"], "project": "fakepy", "testFiles": ["tools/fakepy/tests/test_missing.py"], "guards": [], "level": "focused" }
                  ]
                }
                """);

            var (exit, stdout, stderr) = RunBoundaryGuard(root);
            Assert.True(exit != 0, "guard accepted a testFiles pattern matching no real test file");
            Assert.Contains("testFiles pattern matches no test file: fakepy-boundary: tools/fakepy/tests/test_missing.py", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void P4a_a_verificationId_on_a_pytest_project_is_rejected()
    {
        var root = PlantPytestProjectFixture();
        try
        {
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": { "fakepy": { "runner": "pytest", "root": "tools/fakepy", "tests": "tests" } },
                  "boundaries": [
                    { "id": "fakepy-boundary", "kind": "owner", "paths": ["tools/fakepy/**"], "project": "fakepy", "verificationId": "fake.py", "guards": [], "level": "focused" }
                  ]
                }
                """);

            var (exit, stdout, stderr) = RunBoundaryGuard(root);
            Assert.True(exit != 0, "guard accepted a verificationId on a pytest project");
            Assert.Contains("verificationId not allowed on a pytest project: fakepy-boundary", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void P4b_testFiles_on_a_dotnet_project_is_rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-p4b-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            Directory.CreateDirectory(Path.Combine(root, "tests", "Fake"));
            File.WriteAllText(Path.Combine(root, "tests", "Fake", "Fake.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": { "fake": "tests/Fake/Fake.csproj" },
                  "boundaries": [
                    { "id": "fake-boundary", "kind": "owner", "paths": ["tests/Fake/**"], "project": "fake", "testFiles": ["tests/Fake/test_something.py"], "guards": [], "level": "focused" }
                  ]
                }
                """);

            var (exit, stdout, stderr) = RunBoundaryGuard(root);
            Assert.True(exit != 0, "guard accepted testFiles on a dotnet project");
            Assert.Contains("testFiles only allowed on a pytest project: fake-boundary", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ── python-test-lane D3: runner branches (plan shape only - no pytest execution here) ───────────

    /// <summary>A planted pytest project `fakepy` (root `tools/fakepy`, `tests`) with two real test
    /// files and a real `conftest.py`, plus a `testFiles` boundary on `adapter.py` and a `selfSelect`
    /// boundary on the whole `tests/` dir - shared by P1, P2 and P2b.</summary>
    private static string PlantPytestVerifyFixture()
    {
        var repo = RepoRoot();
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-pytest-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "scripts", "lib"));
        Directory.CreateDirectory(Path.Combine(root, "tools", "fakepy", "tests"));
        File.Copy(Path.Combine(repo, "scripts", "verify-change.py"), Path.Combine(root, "scripts", "verify-change.py"));
        // The Python guard IMPORTS its lib, so a fixture that copies only the guard fails at import.
        // The PowerShell pair was self-contained because the .ps1 dot-sourced its lib; the Python pair
        // has to be copied as a set or the guard is not runnable in the fixture at all.
        File.Copy(Path.Combine(repo, "scripts", "guard-verification-boundaries.py"), Path.Combine(root, "scripts", "guard-verification-boundaries.py"));
        File.Copy(Path.Combine(repo, "scripts", "lib", "verification_boundaries.py"), Path.Combine(root, "scripts", "lib", "verification_boundaries.py"));
        File.WriteAllText(Path.Combine(root, "scripts", "test_fast.py"), "FILTER = \"Category!=DiskSemantics&Category!=Heavy\"\n");
        File.WriteAllText(Path.Combine(root, "scripts", "enforcement-registry.v1.json"), """{"schemaVersion":1,"guards":{},"invariants":[]}""");
        File.WriteAllText(Path.Combine(root, "tools", "fakepy", "adapter.py"), "def adapt(): pass\n");
        File.WriteAllText(Path.Combine(root, "tools", "fakepy", "tests", "test_adapter.py"), "def test_adapt(): pass\n");
        File.WriteAllText(Path.Combine(root, "tools", "fakepy", "tests", "test_other.py"), "def test_other(): pass\n");
        File.WriteAllText(Path.Combine(root, "tools", "fakepy", "tests", "conftest.py"), "# fixtures\n");
        File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
            {
              "schemaVersion": 5,
              "projects": { "fakepy": { "runner": "pytest", "root": "tools/fakepy", "tests": "tests" } },
              "boundaries": [
                { "id": "fakepy-adapter", "kind": "owner", "paths": ["tools/fakepy/adapter.py"], "project": "fakepy", "testFiles": ["tools/fakepy/tests/test_adapter.py"], "guards": [], "level": "focused" },
                { "id": "fakepy-tests", "kind": "owner", "paths": ["tools/fakepy/tests/**"], "project": "fakepy", "selfSelect": true, "guards": [], "level": "focused" }
              ]
            }
            """);
        return root;
    }

    /// <summary>Every `pytest`-kind check in a `-Format json` plan, as its `targets` array of
    /// strings (empty when the check plans a module run).</summary>
    private static string[] PytestCheckTargets(string planJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(planJson);
        var check = doc.RootElement.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("kind").GetString() == "pytest");
        return check.GetProperty("targets").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
    }

    [Fact]
    public void P1_a_testFiles_boundary_plans_exactly_its_expanded_sorted_files()
    {
        var root = PlantPytestVerifyFixture();
        try
        {
            var (exit, stdout, stderr) = RunPlanner(
                $"--paths tools/fakepy/adapter.py --root \"{root}\" --allow-unscoped --plan-only --format json");

            Assert.True(exit == 0, $"exit={exit}\nstdout:{stdout}\nstderr:{stderr}");
            Assert.Equal(new[] { "tools/fakepy/tests/test_adapter.py" }, PytestCheckTargets(stdout));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void P2_a_changed_test_file_under_selfSelect_plans_only_that_file()
    {
        var root = PlantPytestVerifyFixture();
        try
        {
            var (exit, stdout, stderr) = RunPlanner(
                $"--paths tools/fakepy/tests/test_adapter.py --root \"{root}\" --allow-unscoped --plan-only --format json");

            Assert.True(exit == 0, $"exit={exit}\nstdout:{stdout}\nstderr:{stderr}");
            Assert.Equal(new[] { "tools/fakepy/tests/test_adapter.py" }, PytestCheckTargets(stdout));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void P2b_a_changed_conftest_under_selfSelect_plans_the_module_run()
    {
        var root = PlantPytestVerifyFixture();
        try
        {
            var (exit, stdout, stderr) = RunPlanner(
                $"--paths tools/fakepy/tests/conftest.py --root \"{root}\" --allow-unscoped --plan-only --format json");

            Assert.True(exit == 0, $"exit={exit}\nstdout:{stdout}\nstderr:{stderr}");
            Assert.Empty(PytestCheckTargets(stdout));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ── python-test-lane D4: the real seedsmith + gk-core/tools/tuning boundaries ────────────────────────────

    [Fact]
    public void P6_the_real_registry_resolves_seedsmith_and_tuning()
    {
        var (guardExit, guardStdout, guardStderr) = RunBoundaryGuard(RepoRoot());
        Assert.True(guardExit == 0, $"guard failed exit={guardExit}\nstdout:\n{guardStdout}\nstderr:\n{guardStderr}");

        var (itemsExit, itemsStdout, itemsStderr) = RunPlanner(
            "--paths tools/seedsmith/seedsmith/adapters/items/acquisition.py --allow-unscoped --plan-only");
        Assert.True(itemsExit == 0, $"exit={itemsExit}\nstdout:{itemsStdout}\nstderr:{itemsStderr}");
        Assert.Contains("seedsmith-items", itemsStdout, StringComparison.Ordinal);
        Assert.Contains("test_items_adapter.py", itemsStdout, StringComparison.Ordinal);

        var (publishExit, publishStdout, publishStderr) = RunPlanner(
            "--paths tools/tuning/publish.py --allow-unscoped --plan-only");
        Assert.True(publishExit == 0, $"exit={publishExit}\nstdout:{publishStdout}\nstderr:{publishStderr}");
        Assert.Contains("tuning-publish-tool", publishStdout, StringComparison.Ordinal);
        Assert.Contains("pytest: tuning-py", publishStdout, StringComparison.Ordinal);

        var (strayExit, strayStdout, strayStderr) = RunPlanner(
            "--paths tools/tuning/test_publish_notification_catalog.py --allow-unscoped --plan-only");
        Assert.True(strayExit == 0, $"a tools/tuning file with no specific boundary should still resolve via the fallback exit={strayExit}\nstdout:{strayStdout}\nstderr:{strayStderr}");
    }

    // ── python-test-lane D6: the knownRed outcome, over PLANTED junit result files (never a real ────
    // ──   pytest invocation inside Guard.Tests) ──────────────────────────────────────────────────────

    /// <summary>A one-off planted junit XML with two testcases in `ClassA`: `test_a` fails, `test_b`
    /// passes. `script` runs after dot-sourcing the lib and must write its verdict via `Write-Host` so
    /// the caller can assert on stdout.</summary>
    /// <summary>
    /// A one-off planted junit XML with two testcases in `ClassA`: `test_a` fails, `test_b` passes.
    /// The junit is a REAL FILE on disk because `junit_test_cases` parses one -- it is not faked into a
    /// list -- and it is deleted in a `finally`, so a failed delete fails the test rather than leaking
    /// (`testing-standard.md` R3).
    /// </summary>
    private static (int Exit, string Stdout, string Stderr) RunKnownRedProbe(string body)
    {
        var repo = RepoRoot();
        var junit = Path.Combine(Path.GetTempPath(), "vb-knownred-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(junit,
            "<?xml version=\"1.0\"?><testsuites><testsuite>"
            + "<testcase classname=\"tests.mod.ClassA\" name=\"test_a\"><failure message=\"x\">details</failure></testcase>"
            + "<testcase classname=\"tests.mod.ClassA\" name=\"test_b\" />"
            + "</testsuite></testsuites>");
        try
        {
            var probe =
                "import pathlib\n"
                + $"cases = vb.junit_test_cases(pathlib.Path({System.Text.Json.JsonSerializer.Serialize(junit)}))\n"
                + body + "\n";
            return RunLibProbe(probe);
        }
        finally
        {
            File.Delete(junit);
        }
    }

    [Fact]
    public void P9_only_knownRed_failures_pass_with_a_printed_line_per_entry()
    {
        const string body =
            "entries = [{'project': 'fakepy', 'test': 'tests/mod.py::ClassA::test_a', 'debt': 'SR-99'}]\n"
            + "outcome = vb.resolve_known_red_outcome('fakepy', cases, entries)\n"
            + "print('ok=' + str(outcome.ok))\n"
            + "print('\\n'.join(outcome.messages))";

        var (exit, stdout, stderr) = RunKnownRedProbe(body);

        Assert.True(exit == 0, $"exit={exit}\nstdout:{stdout}\nstderr:{stderr}");
        Assert.Contains("ok=True", stdout, StringComparison.Ordinal);
        Assert.Contains("KNOWN RED (pre-existing) tests/mod.py::ClassA::test_a -> SR-99", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void P10_an_unregistered_failure_fails_the_check()
    {
        const string body =
            "outcome = vb.resolve_known_red_outcome('fakepy', cases, [])\n"
            + "print('ok=' + str(outcome.ok))\n"
            + "print('\\n'.join(outcome.messages))";

        var (exit, stdout, stderr) = RunKnownRedProbe(body);

        Assert.True(exit == 0, $"exit={exit}\nstdout:{stdout}\nstderr:{stderr}");
        Assert.Contains("ok=False", stdout, StringComparison.Ordinal);
        Assert.Contains("UNEXPECTED FAILURE: tests.mod.ClassA::test_a", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void P11_a_knownRed_test_that_passed_fails_as_a_stale_entry()
    {
        const string body =
            "entries = [{'project': 'fakepy', 'test': 'tests/mod.py::ClassA::test_b', 'debt': 'SR-99'}]\n"
            + "outcome = vb.resolve_known_red_outcome('fakepy', cases, entries)\n"
            + "print('ok=' + str(outcome.ok))\n"
            + "print('\\n'.join(outcome.messages))";

        var (exit, stdout, stderr) = RunKnownRedProbe(body);

        Assert.True(exit == 0, $"exit={exit}\nstdout:{stdout}\nstderr:{stderr}");
        Assert.Contains("ok=False", stdout, StringComparison.Ordinal);
        Assert.Contains("stale knownRed entry tests/mod.py::ClassA::test_b: remove it and its red row", stdout, StringComparison.Ordinal);
    }

    /// <summary>A planted root for the guard-side D6 rule 4 checks: a pytest project `fakepy`, a real
    /// test file, and a minimal `stub-register.md` carrying exactly one `red` row (`SR-01`).</summary>
    private static string PlantKnownRedGuardFixture(bool includeTestFile)
    {
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-knownred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        Directory.CreateDirectory(Path.Combine(root, "docs", "architecture"));
        Directory.CreateDirectory(Path.Combine(root, "tools", "fakepy", "tests"));
        if (includeTestFile)
            File.WriteAllText(Path.Combine(root, "tools", "fakepy", "tests", "test_a.py"), "def test_a(): pass\n");
        File.WriteAllText(Path.Combine(root, "docs", "architecture", "stub-register.md"),
            "| id | kind | what | where | waits-on | owner | ships-on-it |\n" +
            "|---|---|---|---|---|---|---|\n" +
            "| `SR-01` | red | a real red test | `tools/fakepy/tests/test_a.py` | a fix | fakepy | no |\n");
        return root;
    }

    [Fact]
    public void P12a_a_knownRed_entry_whose_debt_is_not_a_red_row_is_rejected()
    {
        var root = PlantKnownRedGuardFixture(includeTestFile: true);
        try
        {
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": { "fakepy": { "runner": "pytest", "root": "tools/fakepy", "tests": "tests" } },
                  "boundaries": [],
                  "knownRed": [
                    { "project": "fakepy", "test": "tests/test_a.py::test_a", "debt": "SR-99" }
                  ]
                }
                """);

            var (exit, stdout, stderr) = RunBoundaryGuard(root);
            Assert.True(exit != 0, "guard accepted a knownRed entry whose debt is not a red row");
            Assert.Contains("knownRed entry's debt does not resolve to a red row: SR-99", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void P12b_a_knownRed_entry_whose_test_file_is_missing_is_rejected()
    {
        var root = PlantKnownRedGuardFixture(includeTestFile: false);
        try
        {
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": { "fakepy": { "runner": "pytest", "root": "tools/fakepy", "tests": "tests" } },
                  "boundaries": [],
                  "knownRed": [
                    { "project": "fakepy", "test": "tests/test_a.py::test_a", "debt": "SR-01" }
                  ]
                }
                """);

            var (exit, stdout, stderr) = RunBoundaryGuard(root);
            Assert.True(exit != 0, "guard accepted a knownRed entry whose test file does not exist");
            Assert.Contains("knownRed entry's test file does not exist: tests/test_a.py::test_a", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Ci_runs_the_static_integrity_guard_without_filtering_its_full_test_projects()
    {
        var ci = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "ci.yml"));

        Assert.Contains("name: Verification-boundary integrity", ci, StringComparison.Ordinal);
        // The step RUNS the guard, and the command it names is the contract. A pwsh step pointing at a
        // deleted `.ps1` fails at the first line, which the step's own exit code reports - so this
        // assertion is about the spelling CI uses, not about the guard's behaviour.
        // The FILE NAME, not a path spelling. ci.yml is a pwsh step on Windows and spells the
        // invocation with backslashes, so the first version of this assertion looked for the
        // forward-slash form and failed against a workflow that names the right tool.
        Assert.Contains("guard-verification-boundaries.py", ci, StringComparison.Ordinal);
    }

    // ── seam-coverage: schemaVersion 5 (the `full` evidence level, S1-S4) ───────────────────────────

    /// <summary>S-T2: an owner naming neither `project` nor `guards` derives to `full`
    /// (`Get-DerivedLevel`), and S3 makes that legal only under `data/**` or `gk-core/tests/fixtures/**` -
    /// everywhere else (here, `src/**`) it is still "no evidence at all", the pre-S3 C5 failure.</summary>
    [Fact]
    public void S2_a_full_boundary_outside_data_or_fixtures_fails_the_guard()
    {
        var repo = RepoRoot();
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-s2-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts", "lib"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Fake"));
            File.Copy(Path.Combine(repo, "scripts", "verify-change.py"), Path.Combine(root, "scripts", "verify-change.py"));
            // The guard and its lib travel together: the Python guard imports
            // `lib/verification_boundaries.py`, so copying one without the other leaves a fixture whose
            // guard cannot start.
            File.Copy(Path.Combine(repo, "scripts", "guard-verification-boundaries.py"), Path.Combine(root, "scripts", "guard-verification-boundaries.py"));
            File.Copy(Path.Combine(repo, "scripts", "lib", "verification_boundaries.py"), Path.Combine(root, "scripts", "lib", "verification_boundaries.py"));
            File.WriteAllText(Path.Combine(root, "src", "Fake", "Foo.cs"), "namespace Fake; public sealed class Foo { }");
            File.WriteAllText(Path.Combine(root, "scripts", "enforcement-registry.v1.json"), """{"schemaVersion":1,"guards":{},"invariants":[]}""");
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": {},
                  "boundaries": [
                    { "id": "bad-full-on-src", "kind": "owner", "paths": ["src/Fake/Foo.cs"], "guards": [], "level": "full" }
                  ]
                }
                """);

            var (exit, stdout, stderr) = RunBoundaryGuard(root);
            Assert.True(exit != 0, "guard accepted a full-level boundary outside data/** or tests/fixtures/**");
            Assert.Contains("boundary needs a project or at least one guard: bad-full-on-src", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>S-T3: a `full` boundary under `data/**` is legal, the guard passes it, and the
    /// planner prints the CI-owned line for it while selecting zero checks (S3).</summary>
    [Fact]
    public void S3_a_full_boundary_under_data_prints_the_ci_owned_line_and_selects_nothing()
    {
        var repo = RepoRoot();
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-s3-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts", "lib"));
            Directory.CreateDirectory(Path.Combine(root, "data"));
            File.Copy(Path.Combine(repo, "scripts", "verify-change.py"), Path.Combine(root, "scripts", "verify-change.py"));
            // The guard and its lib travel together: the Python guard imports
            // `lib/verification_boundaries.py`, so copying one without the other leaves a fixture whose
            // guard cannot start.
            File.Copy(Path.Combine(repo, "scripts", "guard-verification-boundaries.py"), Path.Combine(root, "scripts", "guard-verification-boundaries.py"));
            File.Copy(Path.Combine(repo, "scripts", "lib", "verification_boundaries.py"), Path.Combine(root, "scripts", "lib", "verification_boundaries.py"));
            File.WriteAllText(Path.Combine(root, "scripts", "test_fast.py"), "FILTER = \"Category!=DiskSemantics&Category!=Heavy\"\n");
            File.WriteAllText(Path.Combine(root, "data", "foo.json"), "{}");
            File.WriteAllText(Path.Combine(root, "scripts", "enforcement-registry.v1.json"), """{"schemaVersion":1,"guards":{},"invariants":[]}""");
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": {},
                  "boundaries": [
                    { "id": "data-foo-full", "kind": "owner", "paths": ["data/foo.json"], "guards": [], "level": "full" }
                  ]
                }
                """);

            var (guardExit, guardStdout, guardStderr) = RunBoundaryGuard(root);
            Assert.True(guardExit == 0, $"guard rejected a legal full boundary under data/**\n{guardStdout}\n{guardStderr}");

            var (exit, stdout, stderr) = RunPlanner(
                $"--paths data/foo.json --root \"{root}\" --allow-unscoped --plan-only");
            Assert.True(exit == 0, $"exit={exit}\nstdout:{stdout}\nstderr:{stderr}");
            Assert.Contains("data/foo.json -> data-foo-full (full): no local check; CI full evidence owns this input", stdout, StringComparison.Ordinal);
            foreach (var checkKindPrefix in new[] { "  test:", "  guard:", "  script:", "  pytest:", "  doc-citations:" })
                Assert.DoesNotContain(checkKindPrefix, stdout, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>S-T5: the evidence-level vocabulary is closed at exactly four members. A fifth level
    /// changes what a plan means (a new kind of "no local check" claim) and is a reviewed change to
    /// this test and to `Get-DerivedLevel`, never content growth - so both the guard's closed-set
    /// check and a planted 5th value are proven here, not read from a comment.</summary>
    [Fact]
    public void S5_the_level_vocabulary_is_pinned_at_exactly_four()
    {
        // A TEXT contract on the guard's own declaration of the closed vocabulary. It reads the
        // tool's SOURCE and asserts the exact four members, so a fifth value cannot be added without
        // this test noticing - which is the whole point of pinning a closed vocabulary. The assertion
        // names the Python declaration, because a PowerShell fragment here would have gone green
        // against a file that no longer parses the flag it quoted.
        var guardText = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "guard-verification-boundaries.py"));
        Assert.Contains("EVIDENCE_LEVELS = (\"focused\", \"module\", \"seam\", \"full\")", guardText, StringComparison.Ordinal);

        var repo = RepoRoot();
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-s5-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts", "lib"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Fake"));
            File.Copy(Path.Combine(repo, "scripts", "verify-change.py"), Path.Combine(root, "scripts", "verify-change.py"));
            // The guard and its lib travel together: the Python guard imports
            // `lib/verification_boundaries.py`, so copying one without the other leaves a fixture whose
            // guard cannot start.
            File.Copy(Path.Combine(repo, "scripts", "guard-verification-boundaries.py"), Path.Combine(root, "scripts", "guard-verification-boundaries.py"));
            File.Copy(Path.Combine(repo, "scripts", "lib", "verification_boundaries.py"), Path.Combine(root, "scripts", "lib", "verification_boundaries.py"));
            File.WriteAllText(Path.Combine(root, "src", "Fake", "Foo.cs"), "namespace Fake; public sealed class Foo { }");
            File.WriteAllText(Path.Combine(root, "scripts", "enforcement-registry.v1.json"), """{"schemaVersion":1,"guards":{},"invariants":[]}""");
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": {},
                  "boundaries": [
                    { "id": "fifth-level", "kind": "owner", "paths": ["src/Fake/Foo.cs"], "guards": [], "level": "bogus" }
                  ]
                }
                """);

            var (exit, stdout, stderr) = RunBoundaryGuard(root);
            Assert.True(exit != 0, "guard accepted a fifth evidence level");
            Assert.Contains("invalid evidence level: fifth-level", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>S-T4: `$Script:EnforcedRoots` (the shared lib) is code-owned, so a synthetic copy of
    /// the lib with `data/**` added to it proves the "on" behaviour (an unmapped file under an
    /// enforced root fails the guard, naming it) without waiting for a real module to switch a root
    /// on. Swapping back the REAL, stock (empty-list) lib over the SAME planted tree then proves the
    /// "off" half: the identical unmapped file no longer fails the guard (S4: the planner alone
    /// refuses it today; the guard only starts enforcing once a root is fully mapped). Invoked by the
    /// planted root's OWN absolute script path, not the `RunBoundaryGuard` helper — that helper's
    /// relative `gk-core/scripts/guard-verification-boundaries.py` invocation always resolves against the REAL
    /// repo (`RunPlanner`'s fixed `WorkingDirectory`), so it runs the REAL lib via its own
    /// `$PSScriptRoot` regardless of `-Root`; only an absolute path into the planted tree reaches the
    /// copy this test actually modified (found live: the guard passed OK on a stock, exit-code-0 run
    /// before this fix, silently proving nothing about the planted copy at all).</summary>
    [Fact]
    public void S4_an_enforced_root_fails_the_guard_on_an_unmapped_file_the_same_file_outside_one_does_not()
    {
        var repo = RepoRoot();
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-s4-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts", "lib"));
            Directory.CreateDirectory(Path.Combine(root, "data"));
            var guardCopyPath = Path.Combine(root, "scripts", "guard-verification-boundaries.py");
            File.Copy(Path.Combine(repo, "scripts", "guard-verification-boundaries.py"), guardCopyPath);
            var realLibPath = Path.Combine(repo, "scripts", "lib", "verification_boundaries.py");
            var realLibText = File.ReadAllText(realLibPath);
            // Regex, not an exact-text match: `ENFORCED_ROOTS`'s own CONTENTS change as later
            // modules (TVB4.5-4.7) switch on more roots - this test proves the ON/OFF SWITCHING
            // mechanism itself, over a synthetic value, regardless of what the real list currently
            // holds (found live: TVB4.4 switching on `gk-core/data/tuning/**` broke this test's own stale
            // `= @()` literal match, since the real list is no longer empty).
            var enforcedRootsLine = new Regex(@"^ENFORCED_ROOTS = .+$", RegexOptions.Multiline);
            Assert.Matches(enforcedRootsLine, realLibText);
            var enforcedLibText = enforcedRootsLine.Replace(realLibText, "ENFORCED_ROOTS = (\"data/**\",)", 1);
            var libPath = Path.Combine(root, "scripts", "lib", "verification_boundaries.py");
            File.WriteAllText(Path.Combine(root, "data", "foo.json"), "{}");
            File.WriteAllText(Path.Combine(root, "scripts", "enforcement-registry.v1.json"), """{"schemaVersion":1,"guards":{},"invariants":[]}""");
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": {},
                  "boundaries": []
                }
                """);

            File.WriteAllText(libPath, enforcedLibText);
            var (onExit, onStdout, onStderr) = RunPythonBoundaryGuard(root, guardCopyPath);
            Assert.True(onExit != 0, "guard accepted an unmapped file under an enforced root");
            Assert.Contains("unmapped enforced-root file: data/foo.json", onStdout + onStderr, StringComparison.Ordinal);

            File.Copy(realLibPath, libPath, overwrite: true);
            var (offExit, offStdout, offStderr) = RunPythonBoundaryGuard(root, guardCopyPath);
            Assert.True(offExit == 0, $"guard failed on an unmapped file outside any enforced root\n{offStdout}\n{offStderr}");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ── seam-coverage TVB4.4: gk-core/data/tuning/** mapped by S1 evidence, enforced root switched on ──────

    /// <summary>S-T1: a domain owned by a `gk-core/data/tuning/<name>.v*.json` wildcard resolves a NEW
    /// version with no registry edit - the whole point of S1's wildcard shape (a `publish.py`
    /// `v{n+1}` needs nothing done to it).</summary>
    [Fact]
    public void S1_a_new_tuning_version_resolves_through_the_wildcard_with_no_registry_edit()
    {
        var repo = RepoRoot();
        var root = Path.Combine(Path.GetTempPath(), "verification-boundary-s1-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts", "lib"));
            Directory.CreateDirectory(Path.Combine(root, "data", "tuning"));
            File.Copy(Path.Combine(repo, "scripts", "verify-change.py"), Path.Combine(root, "scripts", "verify-change.py"));
            // The guard and its lib travel together: the Python guard imports
            // `lib/verification_boundaries.py`, so copying one without the other leaves a fixture whose
            // guard cannot start.
            File.Copy(Path.Combine(repo, "scripts", "guard-verification-boundaries.py"), Path.Combine(root, "scripts", "guard-verification-boundaries.py"));
            File.Copy(Path.Combine(repo, "scripts", "lib", "verification_boundaries.py"), Path.Combine(root, "scripts", "lib", "verification_boundaries.py"));
            File.WriteAllText(Path.Combine(root, "scripts", "test_fast.py"), "FILTER = \"Category!=DiskSemantics&Category!=Heavy\"\n");
            File.WriteAllText(Path.Combine(root, "data", "tuning", "x.v1.json"), "{}");
            File.WriteAllText(Path.Combine(root, "scripts", "enforcement-registry.v1.json"), """{"schemaVersion":1,"guards":{},"invariants":[]}""");
            File.WriteAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json"), """
                {
                  "schemaVersion": 5,
                  "projects": {},
                  "boundaries": [
                    { "id": "tuning-x", "kind": "owner", "paths": ["data/tuning/x.v*.json"], "guards": [], "level": "full" }
                  ]
                }
                """);

            var (exit1, stdout1, stderr1) = RunPlanner(
                $"--paths data/tuning/x.v1.json --root \"{root}\" --allow-unscoped --plan-only");
            Assert.True(exit1 == 0, $"exit={exit1}\n{stdout1}\n{stderr1}");
            Assert.Contains("data/tuning/x.v1.json -> tuning-x (full)", stdout1, StringComparison.Ordinal);

            // A NEW version, added with no registry edit, resolves through the same wildcard.
            File.WriteAllText(Path.Combine(root, "data", "tuning", "x.v2.json"), "{}");
            var (exit2, stdout2, stderr2) = RunPlanner(
                $"--paths data/tuning/x.v2.json --root \"{root}\" --allow-unscoped --plan-only");
            Assert.True(exit2 == 0, $"exit={exit2}\n{stdout2}\n{stderr2}");
            Assert.Contains("data/tuning/x.v2.json -> tuning-x (full)", stdout2, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>S-T7: the real registry plans a not-yet-published tuning version
    /// (`deployment-hierarchy.v5.json` does not exist on disk today) through the same wildcard owner
    /// that plans `v1`-`v4`, with the SAME `project` and guards a rewrite must never drop ("a rewrite
    /// never verifies less"). `-DeletedPaths` is used for a path that never existed, exactly as it is
    /// for one that no longer does - `resolve_owner` pattern-matches the string either way.</summary>
    [Fact]
    public void S7_the_real_registry_plans_a_not_yet_published_tuning_version_with_the_same_evidence()
    {
        var (exit, stdout, stderr) = RunPlanner(
            "--deleted-paths data/tuning/deployment-hierarchy.v5.json --allow-unscoped --plan-only");

        Assert.True(exit == 0, $"exit={exit}\nstdout:{stdout}\nstderr:{stderr}");
        Assert.Contains("deployment-hierarchy.v5.json -> deployment-hierarchy-tuning (module)", stdout, StringComparison.Ordinal);
        Assert.Contains("guard: magic-numbers", stdout, StringComparison.Ordinal);
    }

    /// <summary>S-T8 (TVB4.6): the real registry plans a file under `gk-data/packs/fusion/data/generated/creatures/` -
    /// the `gen-creature-species` script check plus the `generated-seed` guard, both real (map §3.4:
    /// a seam never replaces the owner's own guards; `generated-seed` attaches to every generated
    /// tree).</summary>
    [Fact]
    public void S8_the_real_registry_plans_a_generated_creature_file_with_its_script_check_and_generated_seed_guard()
    {
        var (exit, stdout, stderr) = RunPlanner(
            "--paths data/generated/creatures/Apple.json --allow-unscoped --plan-only");

        Assert.True(exit == 0, $"exit={exit}\nstdout:{stdout}\nstderr:{stderr}");
        Assert.Contains("data/generated/creatures/Apple.json -> gen-creature-species-data (module)", stdout, StringComparison.Ordinal);
        Assert.Contains("guard: generated-seed", stdout, StringComparison.Ordinal);
        Assert.Contains("script: gen-creature-species", stdout, StringComparison.Ordinal);
    }
}
