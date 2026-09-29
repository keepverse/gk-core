namespace FusionRpg.Tools.FileMove;

/// <summary>
/// Plans and applies one move. Every decision happens in <see cref="Plan"/>, before a byte is written —
/// so the dry run and the real run compute the identical change set, and a preview that differs from
/// what `--apply` does is impossible by construction rather than by discipline.
/// </summary>
public sealed class FileMover
{
    readonly string _repoRoot;
    readonly Func<string, string> _readText;
    readonly Func<string, string, SearchOption, IEnumerable<string>> _enumerateFiles;

    public FileMover(
        string repoRoot,
        Func<string, string> readText,
        Func<string, string, SearchOption, IEnumerable<string>> enumerateFiles)
    {
        _repoRoot = repoRoot;
        _readText = readText;
        _enumerateFiles = enumerateFiles;
    }

    public MovePlan Plan(string sourcePath, string destinationPath)
    {
        var sourceProject = FindProject(sourcePath);
        var destProject = FindProject(destinationPath);

        if (sourceProject is null || destProject is null)
            return Refuse(sourcePath, destinationPath,
                "could not find the .csproj owning the source or the destination");

        var sourceAssembly = Path.GetFileNameWithoutExtension(sourceProject);
        var destAssembly = Path.GetFileNameWithoutExtension(destProject);

        // The refusal that matters, checked BEFORE any edit is computed: a move that compiles can still
        // be architecturally wrong, and the compiler only objects once the cycle closes.
        if (!string.Equals(sourceAssembly, destAssembly, StringComparison.OrdinalIgnoreCase))
        {
            var graph = new AssemblyGraph(
                FilesUnder("src", "*.csproj").Concat(FilesUnder("tools", "*.csproj")),
                _readText);

            var cycle = graph.CycleFromMove(sourceAssembly, destAssembly);
            if (cycle is not null)
                return Refuse(sourcePath, destinationPath,
                    $"moving into {destAssembly} would create an assembly cycle: "
                    + string.Join(" -> ", cycle) + $" -> {destAssembly}");
        }

        var source = _readText(sourcePath);
        var oldNamespace = MovePlanner.DeclaredNamespace(source) ?? "";
        var newNamespace = MovePlanner.NamespaceForFolder(
            RootNamespaceOf(destProject, destAssembly),
            Path.GetDirectoryName(destProject)!,
            Path.GetDirectoryName(destinationPath)!);

        var edits = new List<PlannedEdit>
        {
            new(destinationPath, source, MovePlanner.RewriteNamespace(source, newNamespace),
                oldNamespace == newNamespace
                    ? "moved; namespace already correct"
                    : $"moved; namespace {oldNamespace} -> {newNamespace}"),
        };

        // Callers only need rewiring when the namespace actually changed. This is why the four real
        // files produce a zero-caller plan: each already declares the namespace its destination implies.
        if (oldNamespace != newNamespace && oldNamespace.Length > 0)
        {
            foreach (var caller in FilesUnder("src", "*.cs")
                         .Concat(FilesUnder("tests", "*.cs"))
                         .Concat(FilesUnder("tools", "*.cs"))
                         .Where(f => !IsBuildOutput(f))
                         .Where(f => !string.Equals(f, sourcePath, StringComparison.OrdinalIgnoreCase)))
            {
                var text = _readText(caller);
                var rewired = MovePlanner.RewireUsings(text, oldNamespace, newNamespace);
                if (!string.Equals(text, rewired, StringComparison.Ordinal))
                    edits.Add(new PlannedEdit(caller, text, rewired, $"using {oldNamespace} -> {newNamespace}"));
            }
        }

        // A project file that lists sources explicitly needs the path updated; one using the SDK's
        // default globs does not, and editing it would add a redundant entry.
        if (!string.Equals(sourceProject, destProject, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var proj in new[] { sourceProject, destProject })
            {
                var text = _readText(proj);
                var relSource = Path.GetRelativePath(Path.GetDirectoryName(proj)!, sourcePath).Replace('/', '\\');
                if (!text.Contains(relSource, StringComparison.OrdinalIgnoreCase)) continue;

                var relDest = Path.GetRelativePath(Path.GetDirectoryName(proj)!, destinationPath).Replace('/', '\\');
                edits.Add(new PlannedEdit(proj, text,
                    text.Replace(relSource, relDest, StringComparison.OrdinalIgnoreCase),
                    "explicit Compile item repointed"));
            }
        }

        return new MovePlan(sourcePath, destinationPath, oldNamespace, newNamespace, edits, Refusal: null);
    }

    public void Apply(MovePlan plan)
    {
        if (plan.IsRefused) throw new InvalidOperationException("refused plan: " + plan.Refusal);

        foreach (var edit in plan.Edits)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(edit.Path)!);
            File.WriteAllText(edit.Path, edit.After);
        }

        if (!string.Equals(plan.SourcePath, plan.DestinationPath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(plan.SourcePath))
            File.Delete(plan.SourcePath);
    }

    static MovePlan Refuse(string source, string destination, string reason) =>
        new(source, destination, "", "", Array.Empty<PlannedEdit>(), reason);

    /// <summary>
    /// Files under a top-level folder, tolerating its absence. A repo need not have `tests/` or
    /// `tools/` — and the first cut of this class assumed all three existed, which made the tool throw
    /// on any repo simpler than this one. That is precisely the shape the module warns about: a move
    /// tool is dangerous where it is untested, and the synthetic fixture found it immediately.
    /// </summary>
    IEnumerable<string> FilesUnder(string topLevelFolder, string pattern)
    {
        var dir = Path.Combine(_repoRoot, topLevelFolder);
        return Directory.Exists(dir)
            ? _enumerateFiles(dir, pattern, SearchOption.AllDirectories)
            : Array.Empty<string>();
    }

    static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    string? FindProject(string filePath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(filePath)!);
        while (dir is not null && dir.FullName.StartsWith(_repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            // The DESTINATION folder routinely does not exist yet — moving a file into a new folder is
            // the ordinary case, not an edge one. Walk past a missing directory rather than letting the
            // enumerator throw, which is what a first cut of this method did.
            if (dir.Exists)
            {
                var proj = _enumerateFiles(dir.FullName, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (proj is not null) return proj;
            }
            dir = dir.Parent;
        }
        return null;
    }

    string RootNamespaceOf(string projectPath, string fallback)
    {
        var text = _readText(projectPath);
        var open = text.IndexOf("<RootNamespace>", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return fallback;

        var start = open + "<RootNamespace>".Length;
        var close = text.IndexOf("</RootNamespace>", start, StringComparison.OrdinalIgnoreCase);
        return close < 0 ? fallback : text[start..close].Trim();
    }
}
