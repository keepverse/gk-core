namespace FusionRpg.Tools.TestSplitAnalyzer;

/// <summary>One proposed project: one clean SCC, named from its largest member (core-split-analyzer
/// "Grouping proposal").</summary>
public sealed record ProjectGroupProposal(string Name, IReadOnlyList<string> Candidates);

public sealed record GroupingResult(
    IReadOnlyList<ProjectGroupProposal> CleanProjects,
    string ResidualName,
    IReadOnlyList<string> ResidualCandidates);

/// <summary>
/// Strongly connected components of the candidate reference graph — a cycle must share a project
/// (core-split-analyzer "Grouping proposal"). An SCC is <b>clean</b> when it has no edge into another
/// SCC; the proposal is one project per clean SCC, everything else falls into the residual.
/// </summary>
public static class Grouping
{
    public static GroupingResult Build(
        IReadOnlyList<string> allCandidates,
        IReadOnlyList<CandidateEdge> edges,
        IReadOnlyDictionary<string, int> fileCountByCandidate,
        string residualName)
    {
        var components = StronglyConnectedComponents(allCandidates, edges);

        var sccIndexByCandidate = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < components.Count; i++)
            foreach (var candidate in components[i]) sccIndexByCandidate[candidate] = i;

        var hasOutsideEdge = new bool[components.Count];
        foreach (var edge in edges)
        {
            if (!sccIndexByCandidate.TryGetValue(edge.From, out var fromIndex)) continue;
            if (!sccIndexByCandidate.TryGetValue(edge.To, out var toIndex)) continue;
            if (fromIndex != toIndex) hasOutsideEdge[fromIndex] = true;
        }

        var clean = new List<ProjectGroupProposal>();
        var residual = new List<string>();
        for (var i = 0; i < components.Count; i++)
        {
            var members = components[i].OrderBy(c => c, StringComparer.Ordinal).ToList();
            if (hasOutsideEdge[i]) { residual.AddRange(members); continue; }
            clean.Add(new ProjectGroupProposal(PickName(members, fileCountByCandidate), members));
        }

        // Deterministic regardless of input/edge enumeration order (A7): sort every list ordinally.
        clean = clean.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        residual.Sort(StringComparer.Ordinal);

        return new GroupingResult(clean, residualName, residual);
    }

    /// <summary>The member with the most files names the group (ties broken ordinally, for A7).</summary>
    static string PickName(IReadOnlyList<string> members, IReadOnlyDictionary<string, int> fileCountByCandidate)
    {
        if (members.Count == 1) return members[0];
        return members
            .OrderByDescending(m => fileCountByCandidate.TryGetValue(m, out var count) ? count : 0)
            .ThenBy(m => m, StringComparer.Ordinal)
            .First();
    }

    static List<List<string>> StronglyConnectedComponents(IReadOnlyList<string> nodes, IReadOnlyList<CandidateEdge> edges)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var node in nodes) adjacency[node] = new List<string>();
        foreach (var edge in edges)
        {
            if (!adjacency.TryGetValue(edge.From, out var list)) { list = new List<string>(); adjacency[edge.From] = list; }
            if (!list.Contains(edge.To, StringComparer.Ordinal)) list.Add(edge.To);
        }

        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowlink = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var result = new List<List<string>>();
        var counter = 0;

        // Tarjan's algorithm. Node order is sorted ordinally so the result is deterministic (A7)
        // regardless of the order candidates or edges were discovered in.
        void StrongConnect(string v)
        {
            index[v] = counter;
            lowlink[v] = counter;
            counter++;
            stack.Push(v);
            onStack.Add(v);

            foreach (var w in adjacency.TryGetValue(v, out var neighbours) ? neighbours : Enumerable.Empty<string>())
            {
                if (!index.ContainsKey(w))
                {
                    if (!adjacency.ContainsKey(w)) adjacency[w] = new List<string>(); // an edge target outside `nodes`
                    StrongConnect(w);
                    lowlink[v] = Math.Min(lowlink[v], lowlink[w]);
                }
                else if (onStack.Contains(w))
                {
                    lowlink[v] = Math.Min(lowlink[v], index[w]);
                }
            }

            if (lowlink[v] != index[v]) return;
            var component = new List<string>();
            string popped;
            do
            {
                popped = stack.Pop();
                onStack.Remove(popped);
                component.Add(popped);
            } while (popped != v);
            result.Add(component);
        }

        foreach (var node in nodes.OrderBy(n => n, StringComparer.Ordinal))
            if (!index.ContainsKey(node)) StrongConnect(node);

        return result;
    }
}
