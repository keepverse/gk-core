using System.Text.Json;
using FusionRpg.Tools.FileMove;

// `solid-remediation` T5.5 — move a C# file and rewire what the move breaks.
// `test-verification-boundary` core-split-apply — split one project out of FusionRpg.Core.Tests.
//
// Dry run is the DEFAULT, not a flag. The module's Boundaries say "always: dry-run first; the tool
// prints what it will change before changing it", and a tool whose destructive mode is the default is
// one people learn to run with --dry-run bolted on from memory. Inverting it means forgetting the flag
// is safe.
//
//   dotnet run --project gk-core/tools/FileMove -- <source> <destination>            # prints the plan
//   dotnet run --project gk-core/tools/FileMove -- <source> <destination> --apply    # writes it
//   dotnet run --project gk-core/tools/FileMove -- split <manifest.json> --project <name>   # prints the plan
//   dotnet run --project gk-core/tools/FileMove -- split <manifest.json> --project <name> --apply
//   dotnet run --project gk-core/tools/FileMove -- split --revert <journal.json>

if (args.Length >= 1 && string.Equals(args[0], "split", StringComparison.Ordinal))
    return RunSplit(args[1..]);

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "usage: FileMove <source.cs> <destination.cs> [--apply]\n" +
        "       FileMove split <manifest.json> --project <name> [--apply]\n" +
        "       prints the full change set; --apply writes it.");
    return 2;
}

var source = Path.GetFullPath(args[0]);
var destination = Path.GetFullPath(args[1]);
var apply = args.Contains("--apply", StringComparer.Ordinal);

var repoRoot = FindRepoRoot(Path.GetDirectoryName(source)!);
if (repoRoot is null)
{
    Console.Error.WriteLine("could not find a repo root (no src/FusionRpg.Core) above " + source);
    return 2;
}

var mover = new FileMover(repoRoot, File.ReadAllText, Directory.EnumerateFiles);
var plan = mover.Plan(source, destination);

Console.WriteLine($"move  {Rel(plan.SourcePath)}");
Console.WriteLine($"  ->  {Rel(plan.DestinationPath)}");
Console.WriteLine($"namespace  {plan.OldNamespace}  ->  {plan.NewNamespace}");
Console.WriteLine();

if (plan.IsRefused)
{
    Console.Error.WriteLine("REFUSED: " + plan.Refusal);
    return 1;
}

foreach (var edit in plan.Edits)
    Console.WriteLine($"  {Rel(edit.Path)}   ({edit.Reason})");

Console.WriteLine();
Console.WriteLine($"{plan.Edits.Count} file(s) change.");

if (!apply)
{
    Console.WriteLine("dry run — nothing written. Re-run with --apply.");
    return 0;
}

mover.Apply(plan);
Console.WriteLine("applied.");
return 0;

string Rel(string p) => Path.GetRelativePath(repoRoot!, p).Replace('\\', '/');

static string? FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}

// `core-split-apply` A4 — the split verb: dry-run, --apply (SplitExecutor + build/test gate), --revert.
int RunSplit(string[] splitArgs)
{
    if (splitArgs.Length >= 1 && string.Equals(splitArgs[0], "--revert", StringComparison.Ordinal))
        return RunSplitRevert(splitArgs[1..]);

    string? manifestArg = null;
    string? projectName = null;
    var applySplit = false;

    for (var i = 0; i < splitArgs.Length; i++)
    {
        if (string.Equals(splitArgs[i], "--project", StringComparison.Ordinal) && i + 1 < splitArgs.Length)
            projectName = splitArgs[++i];
        else if (string.Equals(splitArgs[i], "--apply", StringComparison.Ordinal))
            applySplit = true;
        else if (manifestArg is null)
            manifestArg = splitArgs[i];
    }

    if (manifestArg is null || projectName is null)
    {
        Console.Error.WriteLine("usage: FileMove split <manifest.json> --project <name> [--apply]\n"
            + "       FileMove split --revert <journal.json>");
        return 2;
    }

    var manifestPath = Path.GetFullPath(manifestArg);
    if (!File.Exists(manifestPath))
    {
        Console.Error.WriteLine("manifest not found: " + manifestPath);
        return 2;
    }

    var splitRepoRoot = FindRepoRoot(Path.GetDirectoryName(manifestPath)!);
    if (splitRepoRoot is null)
    {
        Console.Error.WriteLine("could not find a repo root (no src/FusionRpg.Core) above " + manifestPath);
        return 2;
    }

    var manifest = JsonSerializer.Deserialize<SplitManifest>(
        File.ReadAllText(manifestPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (manifest is null)
    {
        Console.Error.WriteLine("could not parse manifest: " + manifestPath);
        return 2;
    }

    string SplitAbs(string repoRelative) =>
        Path.Combine(splitRepoRoot!, repoRelative.Replace('/', Path.DirectorySeparatorChar));
    var residualDir = SplitAbs($"tests/{manifest.Residual}");

    IReadOnlyList<string> FilesMatching(string pattern) =>
        Directory.Exists(residualDir)
            ? Directory.EnumerateFiles(residualDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(residualDir, f).Replace('\\', '/'))
                .Where(rel => SplitManifestValidator.MatchesPattern(rel, pattern))
                .ToList()
            : Array.Empty<string>();

    byte[] SplitReadBytes(string repoRelative) => File.ReadAllBytes(SplitAbs(repoRelative));
    bool SplitFileExists(string repoRelative) => File.Exists(SplitAbs(repoRelative));
    bool ProjectDirExists(string name) => Directory.Exists(SplitAbs($"tests/{name}"));

    // A1 -- validate the WHOLE manifest before planning any single project. `references` is checked
    // against what the residual csproj ACTUALLY references today, read fresh off disk rather than
    // trusted from the manifest itself (the manifest could claim anything).
    var residualCsprojPath = $"tests/{manifest.Residual}/{manifest.Residual}.csproj";
    IReadOnlyList<string> ResidualReferencesToday()
    {
        if (!SplitFileExists(residualCsprojPath)) return Array.Empty<string>();
        var text = File.ReadAllText(SplitAbs(residualCsprojPath));
        var matches = System.Text.RegularExpressions.Regex.Matches(
            text, @"<ProjectReference\s+Include\s*=\s*""(?<path>[^""]+)""");
        return matches
            .Select(m => m.Groups["path"].Value.Replace('\\', '/'))
            .Select(p => p.StartsWith("../../", StringComparison.Ordinal) ? p["../../".Length..] : p)
            .ToList();
    }

    var validation = SplitManifestValidator.Validate(
        manifest, FilesMatching, SplitFileExists, ProjectDirExists, ResidualReferencesToday(), projectName);
    if (!validation.Ok)
    {
        Console.Error.WriteLine($"REFUSED: manifest fails {validation.Violations.Count} A1 rule(s):");
        foreach (var violation in validation.Violations)
            Console.Error.WriteLine($"  [{violation.Project}] {violation.Reason}");
        return 1;
    }

    IReadOnlyList<string> DirtyPaths()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", "status --porcelain")
        {
            WorkingDirectory = splitRepoRoot,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null) return Array.Empty<string>();
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Length > 3 ? line[3..].Trim().Replace('\\', '/') : "")
            .Where(p => p.Length > 0)
            .ToList();
    }

    var splitPlan = SplitPlanner.Plan(
        manifest, projectName, FilesMatching, SplitReadBytes, SplitFileExists, DirtyPaths);

    Console.WriteLine($"split  {projectName}");
    Console.WriteLine();

    if (splitPlan.IsRefused)
    {
        Console.Error.WriteLine("REFUSED: " + splitPlan.Refusal);
        return 1;
    }

    foreach (var op in splitPlan.Ops)
        Console.WriteLine(op.Kind switch
        {
            SplitOpKind.CreateDirectory => $"  mkdir   {op.Path}",
            SplitOpKind.CreateFile => $"  create  {op.Path}",
            SplitOpKind.ModifyFile => $"  modify  {op.Path}",
            SplitOpKind.MoveFile => $"  move    {op.From}  ->  {op.Path}",
            _ => $"  ?       {op.Path}",
        });

    Console.WriteLine();
    Console.WriteLine($"{splitPlan.Ops.Count} operation(s).");

    if (!applySplit)
    {
        Console.WriteLine("dry run — nothing written. Re-run with --apply.");
        return 0;
    }

    var executor = new SplitExecutor(splitRepoRoot);
    var journalPath = executor.Apply(splitPlan);
    Console.WriteLine("applied. journal: " + journalPath);

    // A4: build and test the new project AND the residual under the default profile filter (the one
    // gk-core/scripts/test_fast.py owns) before keeping the increment; else revert and exit 1.
    var newProjectCsproj = SplitAbs($"tests/{projectName}/{projectName}.csproj");
    var residualCsprojForBuild = SplitAbs($"tests/{manifest.Residual}/{manifest.Residual}.csproj");
    const string DefaultProfileFilter = "Category!=DiskSemantics&Category!=Heavy";

    var steps = new (string Label, string Args, bool IsTest)[]
    {
        ("build new project", $"build \"{newProjectCsproj}\" -c Release", false),
        ("build residual", $"build \"{residualCsprojForBuild}\" -c Release", false),
        ("test new project", $"test \"{newProjectCsproj}\" -c Release --filter \"{DefaultProfileFilter}\"", true),
        ("test residual", $"test \"{residualCsprojForBuild}\" -c Release --filter \"{DefaultProfileFilter}\"", true),
    };

    foreach (var step in steps)
    {
        Console.WriteLine($"==> dotnet {step.Args}");
        var (exitCode, output) = RunProcess("dotnet", step.Args, splitRepoRoot);

        // A test step must ALSO report that it ran something. `dotnet test` exits 0 when it discovers
        // no tests at all ("No test is available in ... Make sure that test discoverer & executors are
        // registered"), so the exit code alone cannot tell "the moved tests ran" from "this project has
        // no test adapter" — the exact shape of the first real increment's failure. An increment whose
        // gate cannot see a test count is reverted like any other failure.
        var reported = step.IsTest ? SplitExecutor.TestsReported(output) : 1;
        if (exitCode == 0 && reported is > 0) continue;

        Console.Error.WriteLine(exitCode != 0
            ? $"FAILED: {step.Label} (exit {exitCode}) -- reverting."
            : $"FAILED: {step.Label} reported {reported?.ToString() ?? "no"} test(s) -- nothing was "
              + "discovered, so the step cannot prove the moved tests ran -- reverting.");
        Console.Error.WriteLine(output);

        var revertResult = executor.Revert(journalPath);
        if (!revertResult.Ok)
        {
            Console.Error.WriteLine(
                $"REVERT ALSO BLOCKED: '{revertResult.BlockedPath}' {revertResult.Reason}");
            Console.Error.WriteLine("journal: " + journalPath);
            return 3;
        }

        Console.Error.WriteLine("reverted.");
        return 1;
    }

    executor.Keep(journalPath);
    Console.WriteLine("kept.");
    return 0;
}

int RunSplitRevert(string[] revertArgs)
{
    if (revertArgs.Length < 1)
    {
        Console.Error.WriteLine("usage: FileMove split --revert <journal.json>");
        return 2;
    }

    var journalPath = Path.GetFullPath(revertArgs[0]);
    if (!File.Exists(journalPath))
    {
        Console.Error.WriteLine("journal not found: " + journalPath);
        return 2;
    }

    var revertRepoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
    if (revertRepoRoot is null)
    {
        Console.Error.WriteLine("could not find a repo root (no src/FusionRpg.Core) above the current directory");
        return 2;
    }

    var result = new SplitExecutor(revertRepoRoot).Revert(journalPath);
    if (!result.Ok)
    {
        Console.Error.WriteLine($"REFUSED: '{result.BlockedPath}' {result.Reason}");
        Console.Error.WriteLine("journal: " + journalPath);
        return 3;
    }

    Console.WriteLine("reverted.");
    return 0;
}

static (int ExitCode, string Output) RunProcess(string fileName, string arguments, string workingDirectory)
{
    var psi = new System.Diagnostics.ProcessStartInfo(fileName, arguments)
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var proc = System.Diagnostics.Process.Start(psi);
    if (proc is null) return (-1, $"could not start '{fileName} {arguments}'");

    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    return (proc.ExitCode, stdout + stderr);
}
