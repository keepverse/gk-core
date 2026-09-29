using System.Text;
using FusionRpg.Tools.FileMove;
using Xunit;

namespace FusionRpg.FileMove.Tests;

/// <summary>
/// `core-split-apply` A4 items 2–5 — <see cref="SplitExecutor"/> against a REAL temp tree, the disk
/// being genuinely the subject here (`testing-standard.md` R2), matching the existing
/// <see cref="FileMoverTests"/> fixture style: a throwing delete in <see cref="Dispose"/>, never a
/// swallowed `catch`.
///
/// <para>Plans here are built by hand rather than through <see cref="SplitPlanner"/> — the executor's
/// contract is "apply/revert this list of ops correctly", which does not need a real manifest or a
/// generated csproj to exercise.</para>
/// </summary>
public class SplitExecutorTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "filemove-split-exec-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    string Abs(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    string Write(string relative, string text)
    {
        var path = Abs(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    SplitExecutor Executor() => new(_root);

    // ---- F6: Apply then Revert -------------------------------------------------------------------

    [Fact]
    public void F6_apply_then_revert_restores_every_original_file_and_removes_everything_created()
    {
        Write("tests/FusionRpg.Core.Tests/World/Thing.cs", "namespace FusionRpg.Core.Tests.World;\n");
        var residualCsproj = Write("tests/FusionRpg.Core.Tests/FusionRpg.Core.Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var plan = new SplitPlan("FusionRpg.Core.World.Tests",
            new SplitOp[]
            {
                new(SplitOpKind.CreateDirectory, "tests/FusionRpg.Core.World.Tests", null, null, null),
                new(SplitOpKind.CreateFile, "tests/FusionRpg.Core.World.Tests/FusionRpg.Core.World.Tests.csproj",
                    null, null, Bytes("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")),
                new(SplitOpKind.ModifyFile, "tests/FusionRpg.Core.Tests/FusionRpg.Core.Tests.csproj",
                    null, Bytes("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"),
                    Bytes("<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"x\" /></Project>")),
                new(SplitOpKind.MoveFile, "tests/FusionRpg.Core.World.Tests/World/Thing.cs",
                    "tests/FusionRpg.Core.Tests/World/Thing.cs", null, null),
            },
            Array.Empty<PlannedEdit>(), Refusal: null);

        var executor = Executor();
        var journalPath = executor.Apply(plan);

        Assert.True(File.Exists(journalPath), "journal must exist while the increment is not yet kept");
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"x\" /></Project>",
            File.ReadAllText(residualCsproj));
        Assert.False(File.Exists(Abs("tests/FusionRpg.Core.Tests/World/Thing.cs")));
        Assert.True(File.Exists(Abs("tests/FusionRpg.Core.World.Tests/World/Thing.cs")));

        // A build step would drop bin/obj inside the new project directory -- Revert's directory
        // removal must be recursive enough to take a planted one with it (A4 item 3).
        Write("tests/FusionRpg.Core.World.Tests/bin/Debug/Thing.dll", "not-real-binary");

        var result = executor.Revert(journalPath);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>", File.ReadAllText(residualCsproj));
        Assert.Equal("namespace FusionRpg.Core.Tests.World;\n",
            File.ReadAllText(Abs("tests/FusionRpg.Core.Tests/World/Thing.cs")));
        Assert.False(File.Exists(Abs("tests/FusionRpg.Core.World.Tests/World/Thing.cs")));
        Assert.False(Directory.Exists(Abs("tests/FusionRpg.Core.World.Tests")),
            "the new project directory, including the planted bin/, must be gone");
    }

    // ---- F9: an injected I/O failure on the third op ------------------------------------------

    [Fact]
    public void F9_a_fault_on_the_third_op_undoes_the_first_two_and_the_tree_equals_the_start()
    {
        Write("tests/FusionRpg.Core.Tests/World/Thing.cs", "original content\n");

        var plan = new SplitPlan("FusionRpg.Core.World.Tests",
            new SplitOp[]
            {
                new(SplitOpKind.CreateDirectory, "tests/FusionRpg.Core.World.Tests", null, null, null),
                new(SplitOpKind.CreateFile, "tests/FusionRpg.Core.World.Tests/FusionRpg.Core.World.Tests.csproj",
                    null, null, Bytes("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")),
                new(SplitOpKind.MoveFile, "tests/FusionRpg.Core.World.Tests/World/Thing.cs",
                    "tests/FusionRpg.Core.Tests/World/Thing.cs", null, null),
            },
            Array.Empty<PlannedEdit>(), Refusal: null);

        var executor = Executor();

        var ex = Assert.Throws<IOException>(() => executor.Apply(plan,
            onBeforeOp: (index, _) =>
            {
                if (index == 2) throw new IOException("injected failure on the third op");
            }));
        Assert.Contains("injected failure", ex.Message, StringComparison.Ordinal);

        // The first two ops (mkdir, create csproj) are undone; the third (the move) never ran.
        Assert.False(Directory.Exists(Abs("tests/FusionRpg.Core.World.Tests")));
        Assert.True(File.Exists(Abs("tests/FusionRpg.Core.Tests/World/Thing.cs")));
        Assert.Equal("original content\n", File.ReadAllText(Abs("tests/FusionRpg.Core.Tests/World/Thing.cs")));
    }

    // ---- F10: Revert when a moved file was edited after Apply ---------------------------------

    [Fact]
    public void F10_revert_stops_at_a_file_edited_after_apply_and_leaves_it_untouched()
    {
        Write("tests/FusionRpg.Core.Tests/World/Thing.cs", "original content\n");

        var plan = new SplitPlan("FusionRpg.Core.World.Tests",
            new SplitOp[]
            {
                new(SplitOpKind.CreateDirectory, "tests/FusionRpg.Core.World.Tests", null, null, null),
                new(SplitOpKind.MoveFile, "tests/FusionRpg.Core.World.Tests/World/Thing.cs",
                    "tests/FusionRpg.Core.Tests/World/Thing.cs", null, null),
            },
            Array.Empty<PlannedEdit>(), Refusal: null);

        var executor = Executor();
        var journalPath = executor.Apply(plan);

        // Someone edits the moved file after Apply kept it in place.
        File.WriteAllText(Abs("tests/FusionRpg.Core.World.Tests/World/Thing.cs"), "edited after apply\n");

        var result = executor.Revert(journalPath);

        Assert.False(result.Ok);
        Assert.Equal("tests/FusionRpg.Core.World.Tests/World/Thing.cs", result.BlockedPath);
        Assert.Contains("modified", result.Reason!, StringComparison.OrdinalIgnoreCase);

        // That file is untouched -- Revert never clobbers a change it did not make.
        Assert.Equal("edited after apply\n",
            File.ReadAllText(Abs("tests/FusionRpg.Core.World.Tests/World/Thing.cs")));
        // The journal stays on disk -- a blocked revert is not a kept increment either.
        Assert.True(File.Exists(journalPath));
    }

    // ---- Apply refuses a refused plan without touching disk ------------------------------------

    [Fact]
    public void Apply_throws_for_a_refused_plan_and_writes_nothing()
    {
        var refused = new SplitPlan("FusionRpg.Core.World.Tests", Array.Empty<SplitOp>(),
            Array.Empty<PlannedEdit>(), Refusal: "dirty path");

        Assert.Throws<InvalidOperationException>(() => Executor().Apply(refused));
        Assert.False(Directory.Exists(_root));
    }

    // ---- Keep deletes the journal -----------------------------------------------------------------

    [Fact]
    public void Keep_deletes_the_journal()
    {
        Write("tests/FusionRpg.Core.Tests/World/Thing.cs", "content\n");
        var plan = new SplitPlan("FusionRpg.Core.World.Tests",
            new SplitOp[]
            {
                new(SplitOpKind.CreateDirectory, "tests/FusionRpg.Core.World.Tests", null, null, null),
                new(SplitOpKind.MoveFile, "tests/FusionRpg.Core.World.Tests/World/Thing.cs",
                    "tests/FusionRpg.Core.Tests/World/Thing.cs", null, null),
            },
            Array.Empty<PlannedEdit>(), Refusal: null);

        var executor = Executor();
        var journalPath = executor.Apply(plan);
        Assert.True(File.Exists(journalPath));

        executor.Keep(journalPath);

        Assert.False(File.Exists(journalPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(journalPath)));
        // Keeping the increment leaves the applied state alone.
        Assert.True(File.Exists(Abs("tests/FusionRpg.Core.World.Tests/World/Thing.cs")));
    }

    // ---- A successful Revert deletes the journal too (TVB-F4) -------------------------------------

    /// <summary>
    /// TVB-F4: the auto-revert after a failed build/test left `<temp>/filemove-split-&lt;guid&gt;/` behind —
    /// `%TEMP%` held 47 of them. A revert that COMPLETED puts the tree back exactly as it was, so the
    /// journal is a temp leak and is now deleted; a BLOCKED revert keeps it, which is the exit-3
    /// recovery path (pinned by F10, whose assertion is unchanged).
    /// </summary>
    [Fact]
    public void A_successful_revert_deletes_the_journal_directory()
    {
        Write("tests/FusionRpg.Core.Tests/World/Thing.cs", "original content\n");
        var plan = new SplitPlan("FusionRpg.Core.World.Tests",
            new SplitOp[]
            {
                new(SplitOpKind.CreateDirectory, "tests/FusionRpg.Core.World.Tests", null, null, null),
                new(SplitOpKind.MoveFile, "tests/FusionRpg.Core.World.Tests/World/Thing.cs",
                    "tests/FusionRpg.Core.Tests/World/Thing.cs", null, null),
            },
            Array.Empty<PlannedEdit>(), Refusal: null);

        var executor = Executor();
        var journalPath = executor.Apply(plan);
        Assert.True(File.Exists(journalPath));

        var result = executor.Revert(journalPath);

        Assert.True(result.Ok, result.Reason);
        Assert.False(File.Exists(journalPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(journalPath)));
        // And the revert really did restore the tree, not just delete its bookkeeping.
        Assert.Equal("original content\n", File.ReadAllText(Abs("tests/FusionRpg.Core.Tests/World/Thing.cs")));
        Assert.False(Directory.Exists(Abs("tests/FusionRpg.Core.World.Tests")));
    }

    // ---- The generated csproj actually builds (regression: TVB5.7's first live apply, exit 1) -----

    /// <summary>
    /// A live `--apply` on the real repo (TVB5.7) failed `dotnet build` with `NETSDK1013: The
    /// TargetFramework value '' was not recognized` — <see cref="SplitPlanner"/>'s generated
    /// `PropertyGroup` had never set one. Every other test of the generator only string-matches its
    /// output (<c>SplitPlannerTests</c>'s own doc comment says so explicitly), which is exactly why a
    /// malformed-but-plausible-looking csproj shipped unnoticed until a real `dotnet build` caught it.
    /// This test closes that gap: it runs `Plan` → `Apply` → a REAL `dotnet build` against a synthetic
    /// but structurally faithful tree, so a future property omission fails here instead of live.
    /// </summary>
    [Fact]
    public void The_generated_project_csproj_actually_builds()
    {
        Write("src/FusionRpg.Core/FusionRpg.Core.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework>"
            + "<RootNamespace>FusionRpg.Core</RootNamespace></PropertyGroup></Project>");
        Write("tests/FusionRpg.Core.Tests/AssemblyInfo.cs", "// shared\n");
        Write("tests/FusionRpg.Core.Tests/FusionRpg.Core.Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>"
            + "<ItemGroup><ProjectReference Include=\"..\\..\\src\\FusionRpg.Core\\FusionRpg.Core.csproj\" /></ItemGroup></Project>");
        Write("tests/FusionRpg.Core.Tests/World/Thing.cs",
            "namespace FusionRpg.Core.Tests.World;\npublic class Thing { }\n");
        Write("FusionRpg.slnx",
            "<Solution>\r\n  <Folder Name=\"/tests/\">\r\n"
            + "    <Project Path=\"tests/FusionRpg.Core.Tests/FusionRpg.Core.Tests.csproj\" />\r\n"
            + "  </Folder>\r\n</Solution>\r\n");

        var manifest = new SplitManifest(1, "test", "tests/FusionRpg.Core.Tests.Shared",
            new[] { "AssemblyInfo.cs" }, "FusionRpg.Core.Tests",
            new[]
            {
                new SplitProject("FusionRpg.Core.World.Tests", new[] { "World/**" },
                    new[] { "src/FusionRpg.Core/FusionRpg.Core.csproj" },
                    Array.Empty<string>(), Array.Empty<string>(), CoreInternals: false),
            });

        var residualDir = Abs("tests/FusionRpg.Core.Tests");
        IReadOnlyList<string> FilesMatching(string pattern) =>
            Directory.GetFiles(residualDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(residualDir, f).Replace('\\', '/'))
                .Where(rel => SplitManifestValidator.MatchesPattern(rel, pattern))
                .ToList();

        var plan = SplitPlanner.Plan(manifest, "FusionRpg.Core.World.Tests", FilesMatching,
            path => File.ReadAllBytes(Abs(path)), path => File.Exists(Abs(path)), () => Array.Empty<string>());

        Assert.False(plan.IsRefused, plan.Refusal);

        Executor().Apply(plan);

        var (exitCode, output) = RunDotnetBuild(
            Abs("tests/FusionRpg.Core.World.Tests/FusionRpg.Core.World.Tests.csproj"));

        Assert.True(exitCode == 0, "generated csproj failed to build:\n" + output);
    }

    // ---- the apply gate's own reading: a zero-test run is not a pass ---------------------------

    [Theory]
    // The real shape of a green run: `dotnet test`'s console summary.
    [InlineData("Passed!  - Failed: 0, Passed: 14822, Skipped: 0, Total: 14822, Duration: 50 s - x.dll (net8.0)", 14822)]
    [InlineData("Failed!  - Failed: 1, Passed: 14, Skipped: 0, Total: 15, Duration: 1 s - x.dll (net8.0)", 15)]
    public void A_test_run_reports_how_many_tests_it_ran(string output, int expected) =>
        Assert.Equal(expected, SplitExecutor.TestsReported(output));

    [Fact]
    public void A_test_run_that_discovered_nothing_reports_no_count_rather_than_a_pass()
    {
        // Verbatim shape from the first real increment's new project: no xunit adapter, so nothing was
        // discovered and `dotnet test` exited 0. The gate must not read that as "the moved tests ran".
        const string noTests =
            "Test run for D:\\x\\FusionRpg.Core.AchievementTitlesTuningTests.Tests.dll (.NETCoreApp,Version=v8.0)\r\n"
            + "A total of 1 test files matched the specified pattern.\r\n"
            + "No test is available in D:\\x\\FusionRpg.Core.AchievementTitlesTuningTests.Tests.dll. "
            + "Make sure that test discoverer & executors are registered and platform & framework "
            + "version settings are appropriate and try again.\r\n";

        // "A total of 1 test files matched" is deliberately NOT read as a test count.
        Assert.Null(SplitExecutor.TestsReported(noTests));
        Assert.Null(SplitExecutor.TestsReported(""));
        Assert.Equal(0, SplitExecutor.TestsReported("Passed!  - Failed: 0, Passed: 0, Skipped: 0, Total: 0, Duration: 1 s"));
    }

    static (int ExitCode, string Output) RunDotnetBuild(string csprojPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet",
            $"build \"{csprojPath}\" -c Release --nologo -v quiet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null) return (-1, "could not start dotnet build");

        // Drain BOTH pipes CONCURRENTLY. Sequential `ReadToEnd()` on stdout then stderr is the
        // documented pipe-deadlock hazard `SubprocessPipeDrainGuardTests` exists to ban, and it flagged
        // this helper (TVB-F2): if the child fills the stderr pipe buffer while the parent is still
        // blocked draining stdout to EOF, neither finishes and `WaitForExit` can never fire. This project
        // has no shared `TestSupport/ExternalProcess.Run` (test projects here do not reference each
        // other), so the same concurrent-drain shape is inlined: start both reads, wait for the process,
        // then join the reads with a bound so a grandchild holding a duplicated handle cannot hang the
        // test forever.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        Task.WaitAll(new Task[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(30));

        return (proc.ExitCode,
            (stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "")
            + (stderrTask.IsCompletedSuccessfully ? stderrTask.Result : ""));
    }
}
