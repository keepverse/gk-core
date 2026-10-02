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
        var checksDir = Path.Combine(repoRoot, "scripts", "checks");
        var ci = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "ci.yml"));
        var jobBase = CiLayout.JobBaseDirectory(ci);
        var ciSteps = ParseCiSteps(ci);

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
            var matched = ciSteps.Any(s =>
                s.Body.Contains(matchCommand, StringComparison.Ordinal)
                && CiLayout.Normalize(CiLayout.StepWorkingDirectory(s.WorkingDirectory, jobBase)) == expected);
            if (!matched)
                missing.Add($"{name}: no ci.yml step at working-directory '{expected}' runs '{matchCommand}'");
        }

        Assert.True(missing.Count == 0, string.Join("\n  ", missing));
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

        // The command IS present in the file, so the only thing that can reject this step is the
        // directory: gk-web is not the repository that owns `.`, which is gk-core.
        Assert.Equal("gk-core", expected);
        Assert.Equal("gk-web", CiLayout.Normalize(CiLayout.StepWorkingDirectory(stepRunningIt.WorkingDirectory, jobBase)));
        Assert.NotEqual(expected, CiLayout.Normalize(CiLayout.StepWorkingDirectory(stepRunningIt.WorkingDirectory, jobBase)));
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
            if (trimmed.StartsWith("- name:", StringComparison.Ordinal))
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
