using System.Text.Json;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// python-test-lane §CI: the Python counterpart of <see cref="CiWiringGuardTests"/>. Every `pytest`
/// runner project in <c>verification-boundaries.v1.json</c> must have a real CI step: a
/// <c>python -m pytest</c> line under a step whose <c>working-directory</c> equals that project's own
/// <c>root</c>. This is what keeps a re-pointed or newly added pytest project (like `tuning-py`, whose
/// tests ran nowhere in CI before the E2 step landed) from going unwired the same way
/// <c>gk-forge/tests/FusionRpg.AtomImporter.Tests</c> once did.
/// </summary>
[Trait("VerificationId", "guard.workflows")]
public class CiPytestWiringTests
{
    [Fact]
    public void Every_pytest_project_has_a_ci_step_running_pytest_in_its_own_root()
    {
        var repoRoot = FindRepoRoot();
        var ci = ReadCi(repoRoot);
        var jobBase = CiLayout.JobBaseDirectory(ci);
        var wiredRoots = WiredPytestRoots(ci, jobBase);

        // The registry declares each project by the path it is known by NEXT TO the thing that owns
        // it - `tools/seedsmith` is gk-forge's - and names that owner in `repo` when it is not this
        // repository. Both sides are reduced to the workspace-relative spelling a runner would
        // actually use before being compared; see <see cref="CiLayout"/> for why the owner is
        // DECLARED rather than looked up, and why that is the honest reading rather than a loosening.
        var missing = PytestProjects(repoRoot)
            .Select(project => (project.Id, project.Root, project.OwningRepo,
                Expected: CiLayout.ExpectedWorkspacePath(jobBase, project.Root, project.OwningRepo)))
            .Where(project => !wiredRoots.Contains(project.Expected))
            .Select(project => project.OwningRepo is null
                ? $"{project.Id} (root '{project.Root}', expected at '{project.Expected}')"
                : $"{project.Id} (root '{project.Root}' is owned by '{project.OwningRepo}', "
                  + $"expected at '{project.Expected}')")
            .ToArray();

        Assert.True(missing.Length == 0,
            "pytest project root(s) with no 'python -m pytest' CI step at that working-directory: " + string.Join(", ", missing));
    }

    [Fact]
    public void A_pytest_line_under_a_different_working_directory_does_not_count()
    {
        const string planted =
            "      - name: fake\n" +
            "        working-directory: tools/other\n" +
            "        run: |\n" +
            "          python -m pytest . -q\n";

        var wired = WiredPytestRoots(planted, CiLayout.JobBaseDirectory(planted));

        Assert.DoesNotContain("tools/seedsmith", wired);
        // And a project's own directory must not count merely because it is spelled plausibly: with
        // no job default the base is the workspace root, so nothing resolves to a sibling.
        Assert.DoesNotContain(CiLayout.ExpectedWorkspacePath(".", "tools/seedsmith", "gk-forge"), wired);
    }

    /// <summary>A step that declares no <c>working-directory</c> of its own runs in the job's
    /// <c>defaults.run.working-directory</c> - a real directory. It used to be uncountable, which
    /// meant a project could be declared at the repository root and wired by the default without
    /// this guard ever seeing it.</summary>
    [Fact]
    public void A_pytest_step_under_the_job_base_directory_counts_as_wired()
    {
        const string planted =
            "    defaults:\n" +
            "      run:\n" +
            "        working-directory: gk-core\n" +
            "    steps:\n" +
            "      - name: fake\n" +
            "        run: |\n" +
            "          python -m pytest tests/tools -q\n";

        var wired = WiredPytestRoots(planted, CiLayout.JobBaseDirectory(planted));

        Assert.Contains("gk-core", wired);
    }

    /// <summary>Where a project is expected to run is decided by its declared owner, so a project
    /// wired at the pre-split spelling is reported rather than satisfied. These are the three cases
    /// the split produced, pinned together so a change to the rule has to change them deliberately.</summary>
    [Fact]
    public void An_owned_project_is_expected_at_its_owner_and_an_unowned_one_at_the_job_base()
    {
        // A project owned by a sibling resolves against that sibling's checkout, NOT against this
        // repository - the whole reason seedsmith's step moved when the split did.
        Assert.Equal("gk-forge/tools/seedsmith",
            CiLayout.ExpectedWorkspacePath("gk-core", "tools/seedsmith", "gk-forge"));
        // Same root, owner not declared: it resolves against the job base, and that is a DIFFERENT
        // directory. This is the falsifier for the owner being honoured at all.
        Assert.Equal("gk-core/tools/seedsmith",
            CiLayout.ExpectedWorkspacePath("gk-core", "tools/seedsmith", null));
        Assert.NotEqual(CiLayout.ExpectedWorkspacePath("gk-core", "tools/seedsmith", null),
            CiLayout.ExpectedWorkspacePath("gk-core", "tools/seedsmith", "gk-forge"));
        // A project declared at the repository root is the job base itself.
        Assert.Equal("gk-core", CiLayout.ExpectedWorkspacePath("gk-core", ".", null));
    }

    /// <summary>Every project must name an owner this workspace can actually reach, so a project can
    /// never read as "wired" by naming a repository nobody has checked out.
    ///
    /// The owner counts as reachable in EITHER of the two layouts this workspace is used in, and
    /// both are real: a full workspace has gk-workflow checked out at the workspace ROOT, while CI
    /// checks it out into <c>gk-workflow/</c>. Accepting only one of them makes this guard green in
    /// one place and red in the other, and a gate that only passes where the gate is not being run
    /// is not a gate.</summary>
    [Fact]
    public void Every_pytest_project_declares_an_owner_that_exists()
    {
        var repoRoot = FindRepoRoot();
        var workspace = Path.GetFullPath(KeepverseRoots.Workspace());
        var bad = new List<string>();
        foreach (var project in PytestProjects(repoRoot))
        {
            var expected = CiLayout.ExpectedWorkspacePath(
                CiLayout.JobBaseDirectory(ReadCi(repoRoot)), project.Root, project.OwningRepo);
            var asSibling = Path.Combine(workspace,
                expected.Replace('/', Path.DirectorySeparatorChar));
            // The other layout: the owning repository IS the workspace root, so its own tree sits
            // directly under the workspace rather than under a directory named after it.
            var asWorkspaceRoot = Path.Combine(workspace,
                project.Root.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(asSibling) && !Directory.Exists(asWorkspaceRoot))
                bad.Add($"{project.Id} -> {expected}");
        }

        Assert.True(bad.Count == 0,
            "pytest project(s) whose owner directory is in no checked-out repository: "
            + string.Join(", ", bad));
    }

    /// <summary>`defaults.run.working-directory` is PER JOB. One file-global base applied to a
    /// multi-job workflow reports a step as wired at a directory it does not run in — an audit built
    /// exactly that two-job file and the guard passed a step that only ever ran in gk-web — so the
    /// reader refuses the ambiguity instead of taking the first job's base.
    ///
    /// The single-job case is asserted in the same test, so this cannot be satisfied by refusing every
    /// workflow: a refusal that also rejected a one-job file would hide the real ci.yml behind a
    /// different error.</summary>
    [Fact]
    public void A_workflow_whose_jobs_declare_different_bases_is_refused_rather_than_guessed()
    {
        const string twoJobs =
            "jobs:\n" +
            "  a:\n" +
            "    defaults:\n" +
            "      run:\n" +
            "        working-directory: gk-core\n" +
            "    steps:\n" +
            "      - name: x\n" +
            "        run: |\n" +
            "          python -m pytest tests/tools -q\n" +
            "  b:\n" +
            "    defaults:\n" +
            "      run:\n" +
            "        working-directory: gk-web\n" +
            "    steps:\n" +
            "      - name: y\n" +
            "        run: |\n" +
            "          python -m pytest . -q\n";

        var ex = Assert.Throws<InvalidOperationException>(() => CiLayout.JobBaseDirectory(twoJobs));
        Assert.Contains("2 different defaults", ex.Message);

        const string oneJob =
            "jobs:\n  a:\n    defaults:\n      run:\n        working-directory: gk-core\n"
            + "    steps:\n      - name: x\n        run: |\n          echo hi\n";
        Assert.Equal("gk-core", CiLayout.JobBaseDirectory(oneJob));
    }

    /// <summary>Falsifier for the ownership list: gk-workflow is checked out AT THE WORKSPACE ROOT in a
    /// full workspace, not into a <c>gk-workflow/</c> subdirectory, so a list that names only the
    /// subdirectory resolves nothing and reports a real project's directory as unowned. This is the
    /// tree the split created, and the audit showed the two resolvers disagreeing on exactly it.</summary>
    [Fact]
    public void A_gk_workflow_path_resolves_although_that_repository_IS_the_workspace_root()
    {
        var owned = CiLayout.OwningWorkspaceRelative(".claude/cmdc-agents/scripts");

        Assert.NotNull(owned);
        Assert.True(Directory.Exists(Path.Combine(KeepverseRoots.Workspace(), owned!)),
            $"'{owned}' resolved but does not exist under the workspace root");
    }

    /// <summary>Falsifier for the content-pack entry. The pack is a directory INSIDE gk-data
    /// (gk-data/packs/fusion), so the plain <c>gk-data</c> entry cannot resolve a <c>data/...</c> path,
    /// and without the pack entry every content path resolves to nothing. Pinned so that entry cannot be
    /// deleted silently.</summary>
    [Fact]
    public void A_content_path_resolves_against_the_pack_not_against_gk_data_itself()
    {
        var owned = CiLayout.OwningWorkspaceRelative("data/seed/items");

        Assert.NotNull(owned);
        Assert.True(owned!.StartsWith("gk-data/packs/", StringComparison.Ordinal),
            $"a data/ path must resolve against the pack, got '{owned}'");
        Assert.True(Directory.Exists(Path.Combine(KeepverseRoots.Workspace(), owned)),
            $"'{owned}' resolved but does not exist under the workspace root");
    }

    /// <summary>A step written without a name is still a step, and Actions runs it in the JOB BASE -
    /// not in whatever directory the previous step happened to declare. Keying the scan on `- name:`
    /// merged such a step into its predecessor, so a pytest line in a nameless step was charged to the
    /// previous step's working-directory. This file's self-checkout is a bare `- uses:`, so the shape is
    /// real, not hypothetical.</summary>
    [Fact]
    public void A_nameless_step_is_not_charged_to_the_previous_step_directory()
    {
        const string planted =
            "    defaults:\n" +
            "      run:\n" +
            "        working-directory: gk-core\n" +
            "    steps:\n" +
            "      - name: first\n" +
            "        working-directory: gk-web\n" +
            "        run: |\n" +
            "          echo not a test\n" +
            "      - uses: actions/checkout@v4\n" +
            "        run: |\n" +
            "          python -m pytest . -q\n";

        var wired = WiredPytestRoots(planted, CiLayout.JobBaseDirectory(planted));

        // The pytest line is in the NAMELESS step, so it runs in the job base - gk-web belongs to the
        // first step, which runs no test at all.
        Assert.Contains("gk-core", wired);
        Assert.DoesNotContain("gk-web", wired);
    }

    private sealed record PytestProject(string Id, string Root, string? OwningRepo);

    /// <summary>Every project id whose registry value is an object with <c>runner: "pytest"</c>, as
    /// its <c>root</c> and, when it declares one, the <c>repo</c> that owns that root.</summary>
    static IReadOnlyList<PytestProject> PytestProjects(string repoRoot)
    {
        var registryPath = Path.Combine(repoRoot, "scripts", "verification-boundaries.v1.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(registryPath));
        var projects = new List<PytestProject>();
        foreach (var property in doc.RootElement.GetProperty("projects").EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            if (property.Value.TryGetProperty("runner", out var runner) && runner.GetString() == "pytest"
                && property.Value.TryGetProperty("root", out var root))
            {
                var owner = property.Value.TryGetProperty("repo", out var repo) ? repo.GetString() : null;
                projects.Add(new PytestProject(property.Name, root.GetString()!, owner));
            }
        }
        return projects;
    }

    /// <summary>Every workspace-relative directory of a CI step whose own <c>run:</c> block contains
    /// a <c>python -m pytest</c> line — a step-scoped scan (not "does the whole file contain both
    /// strings somewhere"), so a pytest line in one step and an unrelated working-directory in another
    /// can never be mistaken for a real wire-up.</summary>
    static HashSet<string> WiredPytestRoots(string ciText, string jobBase)
    {
        var lines = ciText.Replace("\r\n", "\n").Split('\n');
        // The indent of the first step entry, used below so a step is recognised by its position in
        // the list rather than by carrying a name.
        var stepIndent = -1;
        foreach (var candidate in lines)
        {
            var trimmedCandidate = candidate.TrimStart();
            if (trimmedCandidate.StartsWith("- ", StringComparison.Ordinal))
            {
                stepIndent = candidate.Length - trimmedCandidate.Length;
                break;
            }
        }
        var wired = new HashSet<string>(StringComparer.Ordinal);
        string? currentWorkingDirectory = null;
        var sawPytestLine = false;
        void Flush()
        {
            // A step with no working-directory of its own still runs somewhere real - the job base -
            // so it is recorded there rather than dropped.
            if (sawPytestLine)
                wired.Add(CiLayout.Normalize(CiLayout.StepWorkingDirectory(currentWorkingDirectory, jobBase)));
        }
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            var lineIndent = line.Length - trimmed.Length;
            // A step starts at ANY `- ` at the step list's own indent - `- name:`, `- uses:`, or anything
            // else - not only at a name. Keying on `- name:` alone let a step written as a bare `- uses:`
            // keep the PREVIOUS step's working-directory, and this file's self-checkout is exactly that
            // shape. Actions runs every step in its own directory, so the truthful attribution for a step
            // that declares none is the job base - which is what the caller's null already means. The
            // indent is compared so a nested `- ` inside a `with:` block is not read as a sibling step.
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) && lineIndent == stepIndent)
            {
                Flush();
                currentWorkingDirectory = null;
                sawPytestLine = false;
                continue;
            }
            if (trimmed.StartsWith("working-directory:", StringComparison.Ordinal))
            {
                currentWorkingDirectory = trimmed["working-directory:".Length..].Trim();
                continue;
            }
            if (trimmed.StartsWith("python -m pytest", StringComparison.Ordinal)) sawPytestLine = true;
        }
        Flush();
        return wired;
    }

    static string ReadCi(string repoRoot)
    {
        var path = Path.Combine(repoRoot, ".github", "workflows", "ci.yml");
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
