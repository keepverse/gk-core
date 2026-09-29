using System.Text.RegularExpressions;

namespace FusionRpg.Tools.FileMove;

/// <summary>
/// The `ProjectReference` graph, read from `.csproj` files as text.
///
/// <para>Exists for one job: refusing a move that would create a cycle between assemblies. That is the
/// capability the four real misfiled files do not exercise at all, and the one where a manual move is
/// genuinely dangerous — a move that compiles today can still be architecturally wrong, and the compiler
/// only complains once the cycle is closed, by which point the diff is large.</para>
/// </summary>
public sealed class AssemblyGraph
{
    static readonly Regex ProjectRef = new(
        @"<ProjectReference\s+Include\s*=\s*""(?<path>[^""]+)""", RegexOptions.Compiled);

    readonly Dictionary<string, HashSet<string>> _edges = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="projectFiles">Absolute paths to every `.csproj` in scope.</param>
    public AssemblyGraph(IEnumerable<string> projectFiles, Func<string, string> readText)
    {
        foreach (var proj in projectFiles)
        {
            var name = Path.GetFileNameWithoutExtension(proj);
            var refs = _edges.TryGetValue(name, out var set)
                ? set
                : _edges[name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in ProjectRef.Matches(readText(proj)))
                refs.Add(Path.GetFileNameWithoutExtension(m.Groups["path"].Value.Replace('\\', '/')));
        }
    }

    public IReadOnlyCollection<string> ReferencesOf(string assembly) =>
        _edges.TryGetValue(assembly, out var set) ? set : Array.Empty<string>();

    /// <summary>
    /// The path <paramref name="from"/> → … → <paramref name="to"/>, or null when none exists.
    /// Returned rather than a bool so a refusal can NAME the cycle — "would create a cycle" with no
    /// path is a message that sends the reader back to the graph by hand.
    /// </summary>
    public IReadOnlyList<string>? FindPath(string from, string to)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trail = new List<string>();

        bool Walk(string at)
        {
            if (!seen.Add(at)) return false;
            trail.Add(at);
            if (string.Equals(at, to, StringComparison.OrdinalIgnoreCase)) return true;

            foreach (var next in ReferencesOf(at))
                if (Walk(next)) return true;

            trail.RemoveAt(trail.Count - 1);
            return false;
        }

        return Walk(from) ? trail : null;
    }

    /// <summary>
    /// Would moving a file from <paramref name="sourceAssembly"/> into
    /// <paramref name="destinationAssembly"/> close a cycle?
    ///
    /// <para><b>The direction matters and is easy to get backwards</b> — the first cut of this method
    /// did. Moving a file OUT of the source and INTO the destination puts code that may still need the
    /// source's types inside the destination, which would force a destination → source reference. That
    /// is a cycle exactly when the source ALREADY reaches the destination, because the back edge closes
    /// a loop that is currently one-way.</para>
    ///
    /// <para>Concretely: Server references Core. Moving a Server file into Core risks Core → Server,
    /// closing Server → Core → Server. The same move in the other direction — a Core file into Server —
    /// adds nothing, because Server may depend on Core freely.</para>
    /// </summary>
    public IReadOnlyList<string>? CycleFromMove(string sourceAssembly, string destinationAssembly)
    {
        if (string.Equals(sourceAssembly, destinationAssembly, StringComparison.OrdinalIgnoreCase))
            return null; // within one assembly there is no edge to add

        // Source already reaches destination: a destination → source edge would close the loop.
        return FindPath(sourceAssembly, destinationAssembly);
    }
}
