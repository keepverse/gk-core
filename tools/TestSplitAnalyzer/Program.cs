using FusionRpg.Tools.TestSplitAnalyzer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// `--production-map` (core-registry-rekey K1) answers a different question from the grouping report:
// for each `gk-core/src/FusionRpg.Core/<Area>`, WHICH TEST PROJECTS reference its symbols. It reads the
// PRODUCTION project with `--project` and the candidate test projects from `--test-projects`
// (default `tests`), and is read-only. `--self-check` runs K-T1's synthetic fixture with no
// repository input at all.
if (Array.IndexOf(args, "--probe-refs") >= 0)
{
    var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
    var parts = tpa switch { string j => j.Split(Path.PathSeparator), string[] a => a, _ => Array.Empty<string>() };
    Console.WriteLine("TPA type=" + (tpa?.GetType().Name ?? "null") + " entries=" + parts.Length);
    var probe = BuildCompilation("tests/FusionRpg.Core.Atoms.Tests/FusionRpg.Core.Atoms.Tests.csproj", "Debug");
    if (probe is null) { Console.WriteLine("probe build returned null"); return 2; }
    var refs = probe.Value.Compilation.References
        .Select(r => Path.GetFileNameWithoutExtension(r.Display ?? "?"))
        .Where(n => n.Length > 0)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    Console.WriteLine("refs=" + refs.Count
        + " hasSystemPrivateCoreLib=" + refs.Contains("System.Private.CoreLib")
        + " hasSystemRuntime=" + refs.Contains("System.Runtime")
        + " hasXunitCore=" + refs.Contains("xunit.core"));
    foreach (var d in probe.Value.Compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).Take(3))
        Console.WriteLine("  " + d.Id + ": " + d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    return 0;
}

var productionMap = Array.IndexOf(args, "--production-map") >= 0;
var selfCheck = Array.IndexOf(args, "--self-check") >= 0;

if (productionMap && selfCheck)
{
    var (fixtureOk, fixtureReport) = ProductionMap.RunFixture();
    Console.WriteLine(fixtureReport);
    return fixtureOk ? 0 : 1;
}

if (productionMap)
{
    if (!TryGetArg(args, "--project", out var coreProject))
    {
        Console.Error.WriteLine("usage: TestSplitAnalyzer --project <src/FusionRpg.Core/FusionRpg.Core.csproj> --production-map [--test-projects <dir>] [--configuration <cfg>] [--format json|md] [--out <path>]");
        return 1;
    }
    var mapConfiguration = TryGetArg(args, "--configuration", out var mapCfg) ? mapCfg : "Debug";
    var mapFormat = TryGetArg(args, "--format", out var mapFmt) ? mapFmt : "md";
    if (mapFormat is not ("json" or "md"))
    {
        Console.Error.WriteLine($"--format must be 'json' or 'md', got '{mapFormat}'");
        return 1;
    }

    var built = BuildCompilation(coreProject, mapConfiguration);
    if (built is null) return 2;
    var productionRoot = built.Value.ProjectDirectory.TrimEnd('/') + "/";
    var areaIndex = ProductionMap.BuildAreaIndex(built.Value.Compilation, built.Value.RepoRelative, productionRoot);
    // The production assembly is added to every test compilation EXPLICITLY: the split test projects
    // reference FusionRpg.Core with a ProjectReference whose DLL is not copied into their own output
    // directory, so the output-directory metadata scan alone would leave every Core type unresolved
    // (an error symbol matches no area, and the map would be empty for a reason nothing reports).
    var productionOutputDirectory = FindBuildOutputDirectory(coreProject, mapConfiguration)!;
    var productionReference = MetadataReference.CreateFromFile(
        Path.Combine(productionOutputDirectory, Path.GetFileNameWithoutExtension(coreProject) + ".dll"));

    var testProjectsDirectory = TryGetArg(args, "--test-projects", out var tp) ? tp : "tests";
    if (!Directory.Exists(testProjectsDirectory))
    {
        Console.Error.WriteLine($"test projects directory not found: {testProjectsDirectory}");
        return 2;
    }
    var productionFull = Path.GetFullPath(coreProject);
    var projectPaths = Directory.GetFiles(testProjectsDirectory, "*.Tests.csproj", SearchOption.AllDirectories)
        .Where(p => !string.Equals(Path.GetFullPath(p), productionFull, StringComparison.OrdinalIgnoreCase))
        .OrderBy(p => p, StringComparer.Ordinal)
        .ToArray();

    var areas = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
    var skipped = new List<string>();
    // A test project whose compilation has unresolved references would contribute FEWER areas than it
    // really references, with nothing failing — the exact silent-empty shape that made this task's
    // first real run report "0 referenced areas" for a project known to reference Core. K1 derives
    // owners from these numbers, so an under-reporting project is named, never silently averaged in.
    var withErrors = new List<(string Project, int Errors, string FirstError)>();
    foreach (var candidateProjectPath in projectPaths)
    {
        var test = BuildCompilation(candidateProjectPath, mapConfiguration, productionReference);
        if (test is null) { skipped.Add(candidateProjectPath.Replace('\\', '/')); continue; }
        var errorDiagnostics = test.Value.Compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errorDiagnostics.Length > 0)
        {
            withErrors.Add((candidateProjectPath.Replace('\\', '/'), errorDiagnostics.Length,
                errorDiagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)));
        }
        foreach (var area in ProductionMap.AreasReferencedBy(test.Value.Compilation, areaIndex,
                     new HashSet<string>(test.Value.SourceFiles, StringComparer.OrdinalIgnoreCase)))
        {
            if (!areas.TryGetValue(area, out var owners))
            {
                owners = new SortedSet<string>(StringComparer.Ordinal);
                areas[area] = owners;
            }
            owners.Add(candidateProjectPath.Replace('\\', '/'));
        }
    }

    var mapOutput = mapFormat == "json"
        ? System.Text.Json.JsonSerializer.Serialize(
            new
            {
                areas = areas.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal),
                testProjects = projectPaths.Select(p => p.Replace('\\', '/')).ToArray(),
                skippedTestProjects = skipped.ToArray(),
                testProjectsWithCompilationErrors = withErrors.Select(e => new { project = e.Project, errors = e.Errors, firstError = e.FirstError }).ToArray(),
            },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
        : BuildProductionMapMarkdown(coreProject, areaIndex, areas, projectPaths.Length, skipped, withErrors);

    if (TryGetArg(args, "--out", out var mapOut)) File.WriteAllText(mapOut, mapOutput);
    else Console.WriteLine(mapOutput);
    return 0;
}

if (!TryGetArg(args, "--project", out var projectPath))
{
    Console.Error.WriteLine("usage: TestSplitAnalyzer --project <csproj> [--configuration <cfg>] [--format json|md] [--out <path>] [--production-map]");
    return 1;
}

var configuration = TryGetArg(args, "--configuration", out var cfg) ? cfg : "Debug";
var format = TryGetArg(args, "--format", out var fmt) ? fmt : "md";
if (format is not ("json" or "md"))
{
    Console.Error.WriteLine($"--format must be 'json' or 'md', got '{format}'");
    return 1;
}

var main = BuildCompilation(projectPath, configuration);
if (main is null) return 2;

var reader = main.Value.Reader;
var rootNamespace = reader.RootNamespace ?? Path.GetFileNameWithoutExtension(Path.GetFullPath(projectPath));
var ownProjectRoot = main.Value.ProjectDirectory + "/";
var sourceFiles = main.Value.SourceFiles;
var compilation = main.Value.Compilation;
var pathToRelative = main.Value.PathToRelative;
var classify = main.Value.Classify;

var referenceGraph = ReferenceGraph.Build(compilation, classify, pathToRelative);
var toolAssemblyNames = reader.ProjectReferences
    .Where(r => r.Contains("/tools/", StringComparison.OrdinalIgnoreCase) || r.Contains("\\tools\\", StringComparison.OrdinalIgnoreCase))
    .Select(r => Path.GetFileNameWithoutExtension(r))
    .ToArray();
var findings = Findings.Build(compilation, classify, pathToRelative, ownProjectRoot, toolAssemblyNames, rootNamespace);

var allCandidates = sourceFiles
    .Select(f => classify(pathToRelative(f)))
    .Where(c => c != "Shared")
    .Distinct(StringComparer.Ordinal)
    .ToArray();
var fileCountByCandidate = sourceFiles
    .Select(f => classify(pathToRelative(f)))
    .Where(c => c != "Shared")
    .GroupBy(c => c, StringComparer.Ordinal)
    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

var grouping = Grouping.Build(allCandidates, referenceGraph.Edges, fileCountByCandidate, rootNamespace);

var sharedFiles = sourceFiles
    .Select(pathToRelative)
    .Where(p => classify(p) == "Shared")
    .OrderBy(p => p, StringComparer.Ordinal)
    .ToArray();

var output = format == "json"
    ? Report.ToJson(grouping, referenceGraph, findings, sharedFiles)
    : Report.ToMarkdown(grouping, referenceGraph, findings, sharedFiles);

if (TryGetArg(args, "--out", out var outPath)) File.WriteAllText(outPath, output);
else Console.WriteLine(output);

return 0;

static string BuildProductionMapMarkdown(
    string coreProject,
    IReadOnlyDictionary<string, string> areaIndex,
    SortedDictionary<string, SortedSet<string>> areas,
    int scannedProjects,
    List<string> skipped,
    List<(string Project, int Errors, string FirstError)> withErrors)
{
    var lines = new List<string>
    {
        "# Production map (core-registry-rekey K1)",
        "",
        $"Production: `{coreProject.Replace('\\', '/')}` - {areaIndex.Count} declared types across {areas.Count} referenced areas; {scannedProjects} test projects scanned ({skipped.Count} skipped, no build output).",
        "",
        "| Area | Test projects referencing its symbols |",
        "|---|---|",
    };
    foreach (var (area, owners) in areas) lines.Add($"| {area} | {string.Join(", ", owners)} |");
    var unreferenced = areaIndex.Values.Distinct(StringComparer.Ordinal)
        .Except(areas.Keys, StringComparer.Ordinal)
        .OrderBy(a => a, StringComparer.Ordinal)
        .ToArray();
    lines.Add("");
    lines.Add($"Areas no scanned test project references ({unreferenced.Length}): {string.Join(", ", unreferenced)}");
    if (skipped.Count > 0) lines.Add($"Skipped (no build output): {string.Join(", ", skipped)}");
    lines.Add(withErrors.Count == 0
        ? "Test projects with compilation errors: 0 (every scanned project resolved its references, so the map is complete for them)"
        : $"Test projects with compilation errors (the map UNDER-reports these): {string.Join(" | ", withErrors.Select(e => $"{e.Project} ({e.Errors} errors, first: {e.FirstError})"))}");
    return string.Join(Environment.NewLine, lines);
}

static bool TryGetArg(string[] args, string name, out string value)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name) { value = args[i + 1]; return true; }
    }
    value = "";
    return false;
}

/// <summary>
/// The one compilation model both modes share (core-split-analyzer Precondition): syntax trees parsed
/// from the project's own sources, metadata references taken from its ALREADY BUILT output directory
/// plus the runtime's trusted-platform-assembly list — never MSBuild, never Workspaces. A null result
/// is a printed, actionable refusal (missing build output), not a crash.
/// <paramref name="ProjectDirectory"/> is repo-relative; <paramref name="PathToRelative"/> is relative
/// to the project directory (the analyzer's own classification basis), and <paramref name="RepoRelative"/>
/// is repo-relative (what the production map's area index keys on).
/// </summary>
static (CsprojReader Reader, string ProjectDirectory, Func<string, string> RepoRelative,
    HashSet<string> LinkedRelativePaths, string[] SourceFiles, Func<string, string> PathToRelative,
    Func<string, string> Classify, Compilation Compilation)? BuildCompilation(
    string projectPath, string configuration, MetadataReference? extraReference = null)
{
    // A native DLL in a reference set is only detected at bind time (CS0009), so test it up front.
    bool IsManagedAssembly(string path)
    {
        try
        {
            _ = System.Reflection.AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    if (!File.Exists(projectPath))
    {
        Console.Error.WriteLine($"project file not found: {projectPath}");
        return null;
    }

    var outputDirectory = FindBuildOutputDirectory(projectPath, configuration);
    if (outputDirectory is null)
    {
        Console.Error.WriteLine(
            $"build output not found for '{projectPath}' ({configuration}) — run: dotnet build {projectPath} -c {configuration}");
        return null;
    }

    var projectFullPath = Path.GetFullPath(projectPath);
    var projectDirectory = Path.GetDirectoryName(projectFullPath)!;
    var reader = CsprojReader.ParseFile(projectFullPath);
    var repoRoot = FindRepoRoot(projectDirectory);
    var projectRootRelative = Path.GetRelativePath(repoRoot, projectDirectory).Replace('\\', '/');

    var linkedRelativePaths = new HashSet<string>(
        reader.LinkedItems.Select(item => (item.Link ?? item.Include).Replace('\\', '/')),
        StringComparer.Ordinal);

    string PathToRelative(string absolute) => Path.GetRelativePath(projectDirectory, absolute).Replace('\\', '/');
    string Classify(string relative) => ReferenceGraph.Classify(relative, linkedRelativePaths);
    string RepoRelative(string absolute) => Path.GetRelativePath(repoRoot, absolute).Replace('\\', '/');

    var sourceFiles = Directory.GetFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                 && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f, StringComparer.Ordinal) // determinism (A7): stable input order regardless of the OS enumeration order
        .ToArray();

    // TVB-F18: the SDK writes `global using` directives into obj/<cfg>/<tfm>/<Name>.GlobalUsings.g.cs when
    // ImplicitUsings is on, and the source scan above deliberately skips obj/. Without that generated file
    // every `Type`/`IEnumerable<>`/`List<>` reference fails with CS0246 no matter how many references are
    // attached (measured: 195 references including System.Private.CoreLib and System.Runtime, still CS0246).
    var globalUsings = Directory.Exists(Path.Combine(projectDirectory, "obj"))
        ? Directory.GetFiles(Path.Combine(projectDirectory, "obj"), "*.GlobalUsings.g.cs", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray()
        : Array.Empty<string>();
    // TVB-F18 (second cause, measured 2026-09-23): the directory scan above never sees the sources a
    // project LINKS IN from outside its own directory (`Directory.Build.props`' `gk-core/tests/Shared/
    // KeepverseRoots.cs`, the shared props' four files, the residual's `..\..\src\...` links). The
    // project's own previously-built DLL used to supply those types by accident, so excluding it — the
    // obvious fix for the CS0122/CS0103 class — made the map worse (11 erroring projects -> 16). These
    // are parsed instead; the crutch reference below is dropped in the same change.
    var linkedSources = CsprojReader.LinkedSourcePaths(projectFullPath)
        .Where(f => !f.StartsWith(projectDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        .ToArray();
    var syntaxTrees = sourceFiles.Concat(linkedSources).Concat(globalUsings)
        .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f))
        .ToArray();

    var metadataReferences = new List<MetadataReference>();
    var seenReferencePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // The project's OWN output assembly sits in the same directory; referencing it makes its `internal`
    // members resolve as a FOREIGN assembly's internals (CS0122 `'ContentRoot' is inaccessible`), and
    // it duplicates the linked sources now parsed above (CS0101). The current compilation IS that
    // assembly, so its own output is not a reference it needs.
    var ownAssemblyName = Path.GetFileNameWithoutExtension(projectFullPath);
    // The project's own build output: every OTHER DLL there is a dependency the project already needed
    // to build (NuGet packages, ProjectReference outputs) — exactly the already-built metadata the
    // Precondition names, never re-resolved through MSBuild.
    foreach (var dll in Directory.GetFiles(outputDirectory, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
    {
        if (string.Equals(Path.GetFileNameWithoutExtension(dll), ownAssemblyName, StringComparison.OrdinalIgnoreCase))
            continue;
        if (!seenReferencePaths.Add(dll)) continue;
        try { metadataReferences.Add(MetadataReference.CreateFromFile(dll)); }
        catch (BadImageFormatException) { /* not a managed assembly — skip */ }
    }
    // The .NET runtime's own trusted-platform-assembly list (this process's own reference set)
    // supplies framework types (System.Runtime etc.) that a framework-dependent build's own output
    // directory does not copy — a standard way to get them without MSBuildWorkspace or a hardcoded path.
    if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedPlatformAssemblies)
    {
        foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!seenReferencePaths.Add(path)) continue;
            try { metadataReferences.Add(MetadataReference.CreateFromFile(path)); }
            catch { /* not loadable as metadata — skip */ }
        }
    }

    // TVB-F18 (third cause, measured 2026-09-23): a web/host test project also needs the ASP.NET Core
    // shared framework, which is NOT in this process's TPA list (the analyzer is a plain console app),
    // so `Microsoft.AspNetCore.*` did not resolve and `FusionRpg.Server.Tests` reported 489 errors.
    // The runtime root beside this process's own `System.Private.CoreLib` names the framework without a
    // hardcoded path or a `dotnet --list-runtimes` shell-out; the version is matched to the PROJECT's
    // own target framework (a mismatched 9.x against a net8.0 project was measured as its own error),
    // and native DLLs (`aspnetcorev2_inprocess.dll`) are refused up front — `CreateFromFile` is lazy and
    // a native image only fails at bind time, as CS0009 in every project.
    var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
    var targetFramework = System.Text.RegularExpressions.Regex
        .Match(File.ReadAllText(projectFullPath), @"<TargetFramework>(?<tfm>[^<]+)</TargetFramework>")
        .Groups["tfm"].Value;
    if (runtimeDirectory is not null && targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
    {
        var major = targetFramework["net".Length..].Split('.')[0];
        var sharedRoot = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDirectory)!)!, "Microsoft.AspNetCore.App");
        if (Directory.Exists(sharedRoot))
        {
            var newest = Directory.GetDirectories(sharedRoot)
                .Where(d => Path.GetFileName(d).StartsWith(major + ".", StringComparison.Ordinal))
                .OrderBy(d => d, StringComparer.Ordinal).LastOrDefault();
            foreach (var dll in newest is null ? Enumerable.Empty<string>() : Directory.GetFiles(newest, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!seenReferencePaths.Add(dll)) continue;
                if (!IsManagedAssembly(dll)) continue;
                try { metadataReferences.Add(MetadataReference.CreateFromFile(dll)); }
                catch { /* not loadable as metadata — skip */ }
            }
        }
    }

    if (extraReference is not null) metadataReferences.Add(extraReference);

    // TVB-F18: the compilation's ASSEMBLY NAME must be the project's real one (the csproj file name is the
    // SDK default), not its root namespace - FusionRpg.Core grants `internal` to test assemblies by that
    // name, so a root-namespace name loses the grant and every internal reference fails CS0122 while the
    // rest of the file compiles (measured: Atoms.Tests 6 errors, all 'ContentRoot' inaccessible).
    var compilation = CSharpCompilation.Create(
        Path.GetFileNameWithoutExtension(projectFullPath), syntaxTrees, metadataReferences,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    return (reader, projectRootRelative, RepoRelative, linkedRelativePaths, sourceFiles, PathToRelative, Classify, compilation);
}

/// <summary>The build-output directory for a project, found by looking for a `&lt;ProjectName&gt;.dll`
/// under `bin/&lt;configuration&gt;/*` — not a hardcoded `net8.0`, since projects in this repo target
/// more than one framework.</summary>
static string? FindBuildOutputDirectory(string projectPath, string configuration)
{
    var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
    if (projectDirectory is null) return null;
    var configurationDirectory = Path.Combine(projectDirectory, "bin", configuration);
    if (!Directory.Exists(configurationDirectory)) return null;

    var projectName = Path.GetFileNameWithoutExtension(projectPath);
    foreach (var frameworkDirectory in Directory.GetDirectories(configurationDirectory))
    {
        if (File.Exists(Path.Combine(frameworkDirectory, projectName + ".dll"))) return frameworkDirectory;
    }
    return null;
}

/// <summary>Best-effort repo root — walks up looking for a `.git` directory; falls back to the
/// directory's own parent if none is found (e.g. running against a project outside any git checkout).</summary>
static string FindRepoRoot(string absoluteDirectory)
{
    var walk = new DirectoryInfo(absoluteDirectory);
    while (walk is not null)
    {
        if (Directory.Exists(Path.Combine(walk.FullName, ".git"))) return walk.FullName;
        walk = walk.Parent;
    }
    return Directory.GetParent(absoluteDirectory)?.FullName ?? absoluteDirectory;
}
