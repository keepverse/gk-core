using System.Text.RegularExpressions;

namespace FusionRpg.Tools.TestSplitAnalyzer;

/// <summary>An explicit `&lt;Compile Include="..." Link="..."/&gt;` item. <see cref="Link"/> is null
/// for a plain in-place item (already under the project directory by SDK-default globbing).</summary>
public sealed record CompileItem(string Include, string? Link);

/// <summary>
/// Reads a `.csproj` as TEXT — the same approach `gk-core/tools/FileMove/AssemblyGraph.cs` already uses for
/// `ProjectReference`s, and core-split-analyzer's own rule: Roslyn's COMPILER API reads the sources,
/// not a project system, so nothing here needs MSBuild to parse project files. (`gk-core/tools/FileMove`
/// itself stays untouched — this is a separate tool, per SOLID "O".)
///
/// <para>Extracts exactly what candidate classification (`ReferenceGraph`, TVB1.7) needs from the
/// project file itself: explicit `Compile` items (a `Link`-carrying one is a file physically
/// elsewhere in the tree, linked in — core-split-analyzer Design: "`TestSupport/`, bootstrap,
/// `AssemblyInfo.cs` and linked files are classified shared"), `ProjectReference`s, and `None`
/// content items (fixtures/Goldens paths a project ships as data alongside its code).</para>
/// </summary>
public sealed class CsprojReader
{
    static readonly Regex CompileItemPattern = new(
        @"<Compile\s+Include\s*=\s*""(?<include>[^""]+)""(?:\s+Link\s*=\s*""(?<link>[^""]+)"")?\s*/?>",
        RegexOptions.Compiled);

    static readonly Regex ProjectReferencePattern = new(
        @"<ProjectReference\s+Include\s*=\s*""(?<path>[^""]+)""", RegexOptions.Compiled);

    static readonly Regex NoneItemPattern = new(
        @"<None\s+Include\s*=\s*""(?<include>[^""]+)""", RegexOptions.Compiled);

    static readonly Regex RootNamespacePattern = new(
        @"<RootNamespace>(?<name>[^<]+)</RootNamespace>", RegexOptions.Compiled);

    static readonly Regex AssemblyNamePattern = new(
        @"<AssemblyName>(?<name>[^<]+)</AssemblyName>", RegexOptions.Compiled);

    static readonly Regex ImportPattern = new(
        @"<Import\s+Project\s*=\s*""(?<path>[^""]+)""", RegexOptions.Compiled);

    public IReadOnlyList<CompileItem> CompileItems { get; }
    public IReadOnlyList<string> ProjectReferences { get; }
    public IReadOnlyList<string> NoneItems { get; }

    /// <summary>Every explicit `Compile` item that carries a `Link` — a file linked in from
    /// elsewhere, always a shared-set member (never a per-candidate file), per the Design section.</summary>
    public IEnumerable<CompileItem> LinkedItems => CompileItems.Where(c => c.Link is not null);

    /// <summary>The SDK default when neither `&lt;RootNamespace&gt;` nor `&lt;AssemblyName&gt;` is set
    /// is the project file's own name (without extension) — the same default `dotnet build` uses.</summary>
    public string? RootNamespace { get; }

    CsprojReader(IReadOnlyList<CompileItem> compileItems, IReadOnlyList<string> projectReferences, IReadOnlyList<string> noneItems, string? rootNamespace)
    {
        CompileItems = compileItems;
        ProjectReferences = projectReferences;
        NoneItems = noneItems;
        RootNamespace = rootNamespace;
    }

    public static CsprojReader Parse(string csprojText, string? projectFileNameWithoutExtension = null)
    {
        var compile = CompileItemPattern.Matches(csprojText)
            .Select(m => new CompileItem(m.Groups["include"].Value, m.Groups["link"].Success ? m.Groups["link"].Value : null))
            .ToList();
        var refs = ProjectReferencePattern.Matches(csprojText)
            .Select(m => m.Groups["path"].Value)
            .ToList();
        var none = NoneItemPattern.Matches(csprojText)
            .Select(m => m.Groups["include"].Value)
            .ToList();

        var rootNamespaceMatch = RootNamespacePattern.Match(csprojText);
        var assemblyNameMatch = AssemblyNamePattern.Match(csprojText);
        var rootNamespace = rootNamespaceMatch.Success ? rootNamespaceMatch.Groups["name"].Value
            : assemblyNameMatch.Success ? assemblyNameMatch.Groups["name"].Value
            : projectFileNameWithoutExtension;

        return new CsprojReader(compile, refs, none, rootNamespace);
    }

    public static CsprojReader ParseFile(string path) => Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

    /// <summary>
    /// Every source file this project LINKS IN from outside its own directory, including the ones its
    /// imported props declare. TVB-F18's second cause, measured 2026-09-23: `BuildCompilation` scans the
    /// project DIRECTORY, so `gk-core/tests/Shared/KeepverseRoots.cs` (`Directory.Build.props:18`) and the four
    /// `CoreTests.Shared.props` sources were never parsed — and the project's own previously-built DLL,
    /// which the analyzer happened to reference, silently supplied those types instead. Excluding that
    /// DLL (the obvious fix) therefore made the map WORSE: 11 erroring projects became 16 and the
    /// residual went from 16 to 57 errors. The fix is to parse the sources, not to keep the crutch.
    ///
    /// <para>Follows `&lt;Import Project="…"&gt;` transitively (props import props), resolves
    /// `$(MSBuildThisFileDirectory)` against the DECLARING file's directory, and ignores wildcard
    /// includes — a glob would need real MSBuild evaluation, and every linked source in this repo is
    /// named explicitly.</para>
    /// </summary>
    public static IReadOnlyList<string> LinkedSourcePaths(string csprojPath)
    {
        var found = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        var start = Path.GetFullPath(csprojPath);
        queue.Enqueue(start);
        // The SDK AUTO-imports `Directory.Build.props` (and `.targets`) from the project's own
        // directory upward — no `<Import>` in the project file names them, so the walk above cannot
        // find `gk-core/tests/Shared/KeepverseRoots.cs` (`Directory.Build.props:18`) without this. Measured
        // 2026-09-23: that one file is the whole `FusionRpg.TestSupport` / `ContentRoot` error class.
        // MSBuild stops at the FIRST `Directory.Build.props` it finds walking up (a file that wants the
        // parent's must import it itself), and the walk must stop there too: this repo's worktrees live
        // under `<main>/.claude/worktrees/<lane>/`, so walking past the worktree root reaches the MAIN
        // checkout's own `Directory.Build.props` and parses a SECOND `gk-core/tests/Shared/KeepverseRoots.cs`
        // from a different absolute path — measured 2026-09-23 as `the namespace 'FusionRpg.TestSupport'
        // already contains a definition for 'KeepverseLayout'` in every project.
        var directory = Path.GetDirectoryName(start);
        while (directory is not null)
        {
            var props = Path.Combine(directory, "Directory.Build.props");
            if (File.Exists(props)) { queue.Enqueue(props); break; }
            directory = Path.GetDirectoryName(directory);
        }
        var targetsDirectory = Path.GetDirectoryName(start);
        while (targetsDirectory is not null)
        {
            var targets = Path.Combine(targetsDirectory, "Directory.Build.targets");
            if (File.Exists(targets)) { queue.Enqueue(targets); break; }
            targetsDirectory = Path.GetDirectoryName(targetsDirectory);
        }
        while (queue.Count > 0)
        {
            var file = queue.Dequeue();
            if (!visited.Add(file) || !File.Exists(file)) continue;
            var declaringDirectory = Path.GetDirectoryName(file)!;
            var text = File.ReadAllText(file);

            foreach (Match m in CompileItemPattern.Matches(text))
            {
                var include = m.Groups["include"].Value;
                if (include.Contains('*')) continue;
                if (!m.Groups["link"].Success) continue; // in-place items are already under the project dir
                var resolved = ResolveMsBuildPath(include, declaringDirectory);
                if (resolved is not null && File.Exists(resolved)) found.Add(resolved);
            }

            foreach (Match m in ImportPattern.Matches(text))
            {
                var resolved = ResolveMsBuildPath(m.Groups["path"].Value, declaringDirectory);
                if (resolved is not null && File.Exists(resolved)) queue.Enqueue(resolved);
            }
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Expands the one property every linked item in this repo uses, resolves a relative
    /// include against the DECLARING file's directory (not the process CWD — a bare `..\shared\X.cs`
    /// include is relative to the file that declares it), and normalises the separator; anything else
    /// (`$(Configuration)` in a path) is refused rather than guessed.</summary>
    static string? ResolveMsBuildPath(string raw, string declaringDirectory)
    {
        var text = raw.Replace("$(MSBuildThisFileDirectory)", declaringDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (text.Contains("$(", StringComparison.Ordinal)) return null;
        var normalised = text.Replace('\\', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(normalised)
            ? Path.GetFullPath(normalised)
            : Path.GetFullPath(Path.Combine(declaringDirectory, normalised));
    }
}
