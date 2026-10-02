using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// python-test-lane D5: each `scripts/checks/gen-*.py` wrapper's REAL command (the CI command itself,
/// not its own toolchain preflight like `python --version`) must appear in `ci.yml` inside a step
/// whose `working-directory` matches the wrapper's own. Known duplication, named with its fix
/// (python-test-lane D5): each command exists twice, as the CI step and as the wrapper, and this is
/// what keeps the two from drifting silently apart the way <see cref="CiWiringGuardTests"/> exists
/// for `dotnet test` lines.
///
/// THE SPEC IS NOW DECLARED, NOT INFERRED. The .ps1 wrappers exposed no machine-readable command: it
/// was "the line before the LAST `if ($LASTEXITCODE -ne 0) { throw `", a positional convention
/// nothing enforced, so reordering a line or inserting a comment made a wrapper unparseable — and
/// the failure surfaced as a PARITY failure rather than a parse failure, pointing at ci.yml when the
/// defect was in the wrapper. Each Python wrapper declares `CHECK`, `PREFLIGHT`,
/// `WORKING_DIRECTORY` and `FAIL_HINT` as module-level constants, and this reads them. A wrapper
/// missing a declaration is reported by name instead of matching nothing.
/// </summary>
[Trait("VerificationId", "guard.workflows")]
public class GeneratorCheckCiParityTests
{
    [Fact]
    public void Every_gen_wrapper_command_matches_a_ci_step_at_the_same_working_directory()
    {
        var repoRoot = FindRepoRoot();
        var ci = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "ci.yml"));

        Assert.True(ParityViolations(ci, Path.Combine(repoRoot, "scripts", "checks")).Count == 0,
            string.Join("\n  ", ParityViolations(ci, Path.Combine(repoRoot, "scripts", "checks"))));
    }

    /// <summary>SEAM. The parity rule as a pure function of the ci.yml TEXT, so a test can drive it with
    /// a planted workflow instead of only the real one.
    ///
    /// Before this existed the rule lived inside the [Fact] and read the real ci.yml and the real
    /// wrappers, which left it with no seam at all. Two consequences, both measured: the step parser
    /// could not be pinned for a nameless step (the same correction IS pinned in
    /// CiPytestWiringTests.WiredPytestRoots, and a mutation control here proved nothing could detect it),
    /// and a fix for it could not be landed without appearing untestable. The wrapper directory is still
    /// a parameter rather than being hard-coded, so the fake never has to invent one.</summary>
    private static IReadOnlyList<string> ParityViolations(string ciText, string checksDir)
    {
        var jobBase = CiLayout.JobBaseDirectory(ciText);
        var ciSteps = ParseCiSteps(ciText);

        // The glob must actually match. A filter that silently matches nothing reads as PARITY
        // GREEN for fifteen wrappers that are no longer there, which is the exact vacuous-pass shape
        // this test exists to prevent — so the count is asserted, not assumed.
        var wrappers = Directory.GetFiles(checksDir, "gen-*.py");
        Assert.NotEmpty(wrappers);

        var missing = new List<string>();
        foreach (var wrapperPath in wrappers)
        {
            var name = Path.GetFileName(wrapperPath);
            var (command, workingDirectory) = ReadSpec(File.ReadAllText(wrapperPath));
            if (command is null)
            {
                missing.Add($"{name}: no CHECK declaration found (a wrapper that declares nothing would "
                            + "otherwise match nothing and read as parity green)");
                continue;
            }
            // A wrapper declares its working directory the way that tree is named next to the thing
            // that owns it - `.` is gk-core itself, `tools/seedsmith` is gk-forge's - so both it and
            // the CI step are reduced to the workspace-relative spelling a runner would use before
            // being compared. The command itself is still matched literally: that duplication is the
            // thing being kept honest, so the text must be the same text. See <see cref="CiLayout"/>.
            var expected = CiLayout.OwningWorkspaceRelative(workingDirectory);
            if (expected is null)
            {
                missing.Add($"{name}: working-directory '{workingDirectory}' is owned by no repository, "
                            + "so its CI location cannot be decided");
                continue;
            }
            // seam-coverage S1: gen-content-validate's own `--db <scratch dir>` is never the same two
            // places (CI passes `$env:RUNNER_TEMP/atom-validate-db`; the wrapper creates and removes
            // its own temp directory locally, since a runner-only env var resolves to nothing here) -
            // compare only the command UP TO AND INCLUDING `--db` for any wrapper that has one.
            var dbIndex = command.IndexOf(" --db", StringComparison.Ordinal);
            var matchCommand = dbIndex >= 0 ? command[..(dbIndex + " --db".Length)] : command;
            var matched = ciSteps.Any(s => IsParityStep(s, matchCommand, expected, jobBase));
            if (!matched)
                missing.Add($"{name}: no ci.yml step at working-directory '{expected}' runs '{matchCommand}'");
        }

        return missing;
    }

    /// <summary>Drives the parity rule through its seam with a workflow whose wrapper command sits in a
    /// step written WITHOUT a name, immediately after a step that declares a different directory.
    ///
    /// Keying the parser on `- name:` merged the nameless step into its predecessor, so the command was
    /// credited to the wrong repository's directory. The same correction is pinned in
    /// CiPytestWiringTests; this is the falsifier that makes it pinnable HERE, which it was not until
    /// <see cref="ParityViolations"/> existed.</summary>
    [Fact]
    public void A_wrapped_command_in_a_nameless_step_is_not_credited_to_the_previous_step_directory()
    {
        var checksDir = Path.Combine(FindRepoRoot(), "scripts", "checks");
        var wrapper = Path.Combine(checksDir, "gen-resource-ownership.py");
        Assert.True(File.Exists(wrapper), "missing " + wrapper);
        var (command, workingDirectory) = ReadSpec(File.ReadAllText(wrapper));
        Assert.NotNull(command);
        var expected = CiLayout.OwningWorkspaceRelative(workingDirectory);
        Assert.NotNull(expected);

        // The command is in the NAMELESS step. It runs in the job base, not in gk-web.
        var planted =
            "    defaults:\n" +
            "      run:\n" +
            "        working-directory: " + expected + "\n" +
            "    steps:\n" +
            "      - name: unrelated\n" +
            "        working-directory: gk-web\n" +
            "        run: |\n" +
            "          echo nothing to do with the check\n" +
            "      - uses: actions/checkout@v4\n" +
            "        run: |\n" +
            "          " + command + "\n";

        var missing = ParityViolations(planted, checksDir);

        // Only the nameless step runs that command, and it runs in the job base, so parity HOLDS.
        Assert.DoesNotContain(missing, m => m.Contains("gen-resource-ownership.py"));

        // The same workflow with the command on the NAMED step in gk-web is NOT parity, which is what
        // proves the directory is being compared rather than the command merely being present.
        var misplaced =
            "    defaults:\n" +
            "      run:\n" +
            "        working-directory: " + expected + "\n" +
            "    steps:\n" +
            "      - name: misplaced\n" +
            "        working-directory: gk-web\n" +
            "        run: |\n" +
            "          " + command + "\n";
        Assert.Contains(ParityViolations(misplaced, checksDir),
            m => m.Contains("gen-resource-ownership.py"));
    }

    /// <summary>Falsifier for the directory comparison: a step running the wrapper's real command in
    /// some OTHER repository's directory is still a parity failure. The owning lookup decides where a
    /// declared path lives; it must not decide that a command counts from anywhere.</summary>
    [Fact]
    public void A_step_in_a_different_directory_is_not_parity()
    {
        const string planted =
            "    defaults:\n" +
            "      run:\n" +
            "        working-directory: gk-core\n" +
            "    steps:\n" +
            "      - name: fake\n" +
            "        working-directory: gk-web\n" +
            "        run: |\n" +
            "          dotnet run --project ../gk-forge/tools/TreeBinder -- --check\n";

        const string wrapperCommand = "dotnet run --project ../gk-forge/tools/TreeBinder -- --check";
        var jobBase = CiLayout.JobBaseDirectory(planted);
        var expected = CiLayout.OwningWorkspaceRelative(".");
        var stepRunningIt = ParseCiSteps(planted)
            .Single(s => s.Body.Contains(wrapperCommand, StringComparison.Ordinal));

        // Both directions go through the PRODUCTION predicate. The planted step carries the right
        // command in gk-web, which is not the repository that owns `.`, so it is not parity; the same
        // step body placed in the owning directory IS parity. Delete the directory clause from
        // IsParityStep and the first assertion fails - the mutation this test exists to catch. The
        // layout-specific spelling is no longer asserted here on purpose: pinning `gk-core` made the
        // test go red in a legacy single-repository checkout, where the directory it names is `.`.
        Assert.NotNull(expected);
        Assert.False(IsParityStep(stepRunningIt, wrapperCommand, expected!, jobBase),
            "the command is present but in the wrong repository's directory, so this is not parity");
        Assert.True(IsParityStep(new CiStep(expected, stepRunningIt.Body), wrapperCommand, expected!, jobBase),
            "the same command in the owning directory IS parity");
        // And the command is still required: a step in the RIGHT directory running something else is
        // not parity either, or the directory clause would be the only thing under test.
        Assert.False(IsParityStep(new CiStep(expected, "run: |\n  python -m pytest . -q\n"),
            wrapperCommand, expected!, jobBase));
    }

    /// <summary>Guard tests never invoke a generator for real here (python-test-lane Testing note:
    /// a real run can take real, non-trivial time, and belongs to that generator's own program, not
    /// this one) - this only proves the wrapper is a real, non-empty module that names its project's
    /// real check command. Execution is proven once, locally, recorded in the task's own evidence.</summary>
    [Theory]
    [InlineData("gen-resource-ownership.py", "resource_ownership.py --check")]
    [InlineData("gen-creature-species.py", "CreatureSpeciesGen -- --check")]
    public void Each_wrapper_names_its_own_real_check_command(string wrapperName, string expectedFragment)
    {
        var path = Path.Combine(FindRepoRoot(), "scripts", "checks", wrapperName);
        Assert.True(File.Exists(path), $"missing wrapper: {wrapperName}");
        var (command, _) = ReadSpec(File.ReadAllText(path));
        Assert.NotNull(command);
        Assert.Contains(expectedFragment, command!, StringComparison.Ordinal);
    }

    private sealed record CiStep(string? WorkingDirectory, string Body);

    /// <summary>Every `- name:` step in `ci.yml`, as its own `working-directory` — null when the step
    /// declares none, which means the job's `defaults.run.working-directory` and not the repository
    /// root — and the full text between that step and the next.</summary>
    private static List<CiStep> ParseCiSteps(string ciText)
    {
        var lines = ciText.Replace("\r\n", "\n").Split('\n');
        // The indent of the first step entry, so a step is recognised by its position in the list rather
        // than by carrying a name.
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
        var steps = new List<CiStep>();
        string? currentWorkingDirectory = null;
        var body = new System.Text.StringBuilder();
        void Flush()
        {
            if (body.Length > 0) steps.Add(new CiStep(currentWorkingDirectory, body.ToString()));
        }
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            var lineIndent = line.Length - trimmed.Length;
            // A step starts at ANY `- ` at the step list's own indent - `- name:`, `- uses:`, or anything
            // else - not only at a name. Keying on `- name:` alone merged a step written as a bare
            // `- uses:` into its predecessor, so its command was credited to the PREVIOUS step's
            // directory; this file's own self-checkout is that shape. Actions runs each step in its own
            // directory, so a step that declares none runs in the job base - which is what the null
            // WorkingDirectory already means to StepWorkingDirectory. The indent is compared so a nested
            // `- ` inside a `with:` block is not read as a sibling step.
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) && lineIndent == stepIndent)
            {
                Flush();
                currentWorkingDirectory = null;
                body.Clear();
                continue;
            }
            if (trimmed.StartsWith("working-directory:", StringComparison.Ordinal))
                currentWorkingDirectory = trimmed["working-directory:".Length..].Trim();
            body.AppendLine(trimmed);
        }
        Flush();
        return steps;
    }

    /// <summary>Read a wrapper's DECLARED spec: its `CHECK` argv rendered as one line, and its
    /// `WORKING_DIRECTORY`. `.` means the repo root, which is how a plain `Push-Location $Root` read
    /// before.
    ///
    /// The argv is parsed as a Python tuple literal rather than captured as source text, so a token
    /// containing a space or a quote is rendered the way a human would retype it — and the same
    /// rendering `checks/common.py` produces, so a wrapper and this test cannot disagree about the
    /// spelling of their own command.</summary>
    /// <summary>THE parity rule: this step runs the command LITERALLY, in the wrapper's own directory.
    ///
    /// Named rather than inlined so the falsifier below exercises this and not a copy of it. An audit
    /// caught that the first version of that falsifier re-implemented the rule inside the test body, so
    /// deleting the directory clause from the production lambda left the falsifier green - a test named
    /// "a step in a different directory is not parity" that could not fail for exactly that mutation.
    /// Both directions of the falsifier now go through this one method.</summary>
    private static bool IsParityStep(CiStep step, string matchCommand, string expected, string jobBase)
        => step.Body.Contains(matchCommand, StringComparison.Ordinal)
           && CiLayout.Normalize(CiLayout.StepWorkingDirectory(step.WorkingDirectory, jobBase)) == expected;

    private static (string? Command, string WorkingDirectory) ReadSpec(string text)
    {
        var check = Regex.Match(text, @"(?m)^CHECK\s*=\s*\((.*?)\)\s*$", RegexOptions.Singleline);
        string? command = null;
        if (check.Success)
        {
            var tokens = Regex.Matches(check.Groups[1].Value, @"'((?:[^'\\]|\\.)*)'|""((?:[^""\\]|\\.)*)""")
                .Select(m =>
                {
                    var raw = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    return raw.Replace("\\\\", "\\").Replace("\\'", "'").Replace("\\\"", "\"");
                })
                .ToList();
            if (tokens.Count > 0) command = string.Join(" ", tokens);
        }

        var wd = Regex.Match(text, @"(?m)^WORKING_DIRECTORY\s*=\s*['""]([^'""]*)['""]");
        var workingDirectory = wd.Success ? wd.Groups[1].Value.Replace('\\', '/') : ".";
        return (command, workingDirectory);
    }

    private static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
