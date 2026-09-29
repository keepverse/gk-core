using System.Text.Json;

namespace FusionRpg.Tools.TestSplitAnalyzer;

/// <summary>
/// Deterministic json/md serialization of one analyzer run — same tree + same build -&gt; byte-identical
/// output (core-split-analyzer "Determinism"). The json shape is the manifest DRAFT in
/// `core-split-apply`'s A1 schema; the tool never writes it as the actual manifest file
/// (`gk-core/tests/core-test-projects.v1.json` is authored, reviewed and landed by TVB5.5). The md report is
/// the owner's review artifact and lists everything the Design section asks for: the shared set,
/// per-candidate required references, internal-symbol users, collections, string-keyed inputs, and
/// trait locations, plus every `BLOCKS SPLIT`.
/// </summary>
public static class Report
{
    public static string ToJson(
        GroupingResult grouping,
        ReferenceGraphResult referenceGraph,
        FindingsResult findings,
        IReadOnlyList<string> sharedFiles,
        string analyzerCommit = "")
    {
        var draft = new
        {
            schemaVersion = 1,
            analyzerCommit,
            sharedFiles = sharedFiles.OrderBy(f => f, StringComparer.Ordinal).ToArray(),
            residual = grouping.ResidualName,
            residualCandidates = grouping.ResidualCandidates.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
            projects = grouping.CleanProjects
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => new
                {
                    name = p.Name,
                    include = p.Candidates.OrderBy(c => c, StringComparer.Ordinal).Select(c => c + "/**").ToArray(),
                    references = Array.Empty<string>(),
                    links = Array.Empty<string>(),
                    content = Array.Empty<string>(),
                    coreInternals = referenceGraph.InternalUsages.Any(u => p.Candidates.Contains(u.Candidate, StringComparer.Ordinal))
                })
                .ToArray(),
            edges = referenceGraph.Edges
                .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal)
                .ThenBy(e => e.Symbol, StringComparer.Ordinal).ThenBy(e => e.UsedAt, StringComparer.Ordinal)
                .Select(e => new { from = e.From, to = e.To, symbol = e.Symbol, declaredIn = e.DeclaredIn, usedAt = e.UsedAt })
                .ToArray(),
            requiredAssemblies = referenceGraph.RequiredAssemblies
                .OrderBy(r => r.Candidate, StringComparer.Ordinal).ThenBy(r => r.AssemblyName, StringComparer.Ordinal)
                .Select(r => new { candidate = r.Candidate, assembly = r.AssemblyName })
                .ToArray(),
            internalUsages = referenceGraph.InternalUsages
                .OrderBy(u => u.UsedAt, StringComparer.Ordinal)
                .Select(u => new { candidate = u.Candidate, symbol = u.Symbol, usedAt = u.UsedAt })
                .ToArray(),
            collectionEdges = referenceGraph.CollectionEdges
                .OrderBy(c => c.CollectionName, StringComparer.Ordinal)
                .Select(c => new { name = c.CollectionName, definedIn = c.DefinedIn, usedIn = c.UsedIn, definedAt = c.DefinedAt, usedAt = c.UsedAt })
                .ToArray(),
            contentRequirements = findings.ContentRequirements
                .OrderBy(c => c.Location, StringComparer.Ordinal)
                .Select(c => new { candidate = c.Candidate, literal = c.Literal, location = c.Location })
                .ToArray(),
            traitLocations = findings.TraitLocations
                .OrderBy(t => t.TraitName, StringComparer.Ordinal).ThenBy(t => t.Value, StringComparer.Ordinal)
                .Select(t => new { candidate = t.Candidate, trait = t.TraitName, value = t.Value, location = t.Location })
                .ToArray(),
            namespaceFolderMismatches = findings.NamespaceFolderMismatches
                .OrderBy(m => m.Location, StringComparer.Ordinal)
                .Select(m => new { candidate = m.Candidate, declared = m.DeclaredNamespace, implied = m.ImpliedNamespace, location = m.Location })
                .ToArray(),
            callerFilePathUsages = findings.CallerFilePathUsages
                .OrderBy(u => u.Location, StringComparer.Ordinal)
                .Select(u => new { candidate = u.Candidate, parameter = u.Parameter, location = u.Location })
                .ToArray(),
            blocksSplit = findings.BlockingFindings
                .OrderBy(f => f.Location, StringComparer.Ordinal).ThenBy(f => f.Literal, StringComparer.Ordinal)
                .Select(f => new { candidate = f.Candidate, literal = f.Literal, targetCandidate = f.TargetCandidate, location = f.Location })
                .ToArray()
        };
        return JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string ToMarkdown(
        GroupingResult grouping,
        ReferenceGraphResult referenceGraph,
        FindingsResult findings,
        IReadOnlyList<string> sharedFiles)
    {
        var lines = new List<string>
        {
            "# TestSplitAnalyzer report",
            "",
            $"Residual: `{grouping.ResidualName}` ({grouping.ResidualCandidates.Count} candidate(s) — a reading)",
            "",
            "## Proposed clean projects"
        };
        foreach (var project in grouping.CleanProjects.OrderBy(p => p.Name, StringComparer.Ordinal))
            lines.Add($"- `{project.Name}` <- {string.Join(", ", project.Candidates.OrderBy(c => c, StringComparer.Ordinal))}");

        lines.Add("");
        lines.Add($"## Shared set ({sharedFiles.Count} file(s) — a reading)");
        foreach (var file in sharedFiles.OrderBy(f => f, StringComparer.Ordinal)) lines.Add($"- `{file}`");
        if (sharedFiles.Count == 0) lines.Add("(none)");

        lines.Add("");
        lines.Add($"## Required assemblies per candidate ({referenceGraph.RequiredAssemblies.Count} — a reading)");
        foreach (var group in referenceGraph.RequiredAssemblies.GroupBy(r => r.Candidate, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            lines.Add($"- `{group.Key}` -> {string.Join(", ", group.Select(r => r.AssemblyName).OrderBy(a => a, StringComparer.Ordinal))}");

        lines.Add("");
        lines.Add($"## Internal-symbol users ({referenceGraph.InternalUsages.Count} — a reading)");
        foreach (var usage in referenceGraph.InternalUsages.OrderBy(u => u.UsedAt, StringComparer.Ordinal))
            lines.Add($"- `{usage.UsedAt}` ({usage.Candidate}): `{usage.Symbol}`");
        if (referenceGraph.InternalUsages.Count == 0) lines.Add("(none)");

        lines.Add("");
        lines.Add($"## Cross-candidate collections ({referenceGraph.CollectionEdges.Count} — a reading)");
        foreach (var edge in referenceGraph.CollectionEdges.OrderBy(c => c.CollectionName, StringComparer.Ordinal))
            lines.Add($"- `{edge.CollectionName}`: defined in {edge.DefinedIn} ({edge.DefinedAt}), used in {edge.UsedIn} ({edge.UsedAt})");
        if (referenceGraph.CollectionEdges.Count == 0) lines.Add("(none)");

        // TVB-F14: a file in candidate A referenced BY SYMBOL from candidate B. The JSON draft has carried
        // these since TVB1.7 (`edges`), but the markdown never printed them, so a manifest author reading the
        // report could not see the shape that made the Atoms increment need two out-of-folder files
        // (`Atoms/EffectSeedFixtureOracle.cs` and `World/StructureCatalogTestBootstrap.cs`, a
        // `[ModuleInitializer]`): TVB1.10 reported 0 `BLOCKS SPLIT` because the existing blocker class is
        // literal-based. Each edge is either answered by the manifest's `links` field or by keeping both
        // candidates in the residual — the SCC grouping decides that, this section only names the work.
        lines.Add("");
        lines.Add($"## Cross-candidate symbol edges ({referenceGraph.Edges.Count} — a reading)");
        foreach (var edge in referenceGraph.Edges
                     .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal)
                     .ThenBy(e => e.Symbol, StringComparer.Ordinal).ThenBy(e => e.UsedAt, StringComparer.Ordinal))
            lines.Add($"- `{edge.From}` uses `{edge.Symbol}` from `{edge.To}` (declared at {edge.DeclaredIn}, used at {edge.UsedAt})");
        if (referenceGraph.Edges.Count == 0) lines.Add("(none)");

        lines.Add("");
        lines.Add($"## String-keyed content inputs ({findings.ContentRequirements.Count} — a reading)");
        foreach (var req in findings.ContentRequirements.OrderBy(c => c.Location, StringComparer.Ordinal))
            lines.Add($"- `{req.Location}` ({req.Candidate}): `{req.Literal}`");
        if (findings.ContentRequirements.Count == 0) lines.Add("(none)");

        lines.Add("");
        lines.Add($"## Trait locations ({findings.TraitLocations.Count} — a reading)");
        foreach (var trait in findings.TraitLocations.OrderBy(t => t.TraitName, StringComparer.Ordinal).ThenBy(t => t.Value, StringComparer.Ordinal))
            lines.Add($"- `{trait.TraitName}={trait.Value}` at `{trait.Location}` ({trait.Candidate})");
        if (findings.TraitLocations.Count == 0) lines.Add("(none)");

        lines.Add("");
        lines.Add($"## Namespace-vs-folder mismatches ({findings.NamespaceFolderMismatches.Count} — a reading)");
        foreach (var mismatch in findings.NamespaceFolderMismatches.OrderBy(m => m.Location, StringComparer.Ordinal))
            lines.Add($"- `{mismatch.Location}`: declared `{mismatch.DeclaredNamespace}`, implied `{mismatch.ImpliedNamespace}`");
        if (findings.NamespaceFolderMismatches.Count == 0) lines.Add("(none)");

        lines.Add("");
        lines.Add($"## [CallerFilePath] users ({findings.CallerFilePathUsages.Count} — a reading)");
        foreach (var usage in findings.CallerFilePathUsages.OrderBy(u => u.Location, StringComparer.Ordinal))
            lines.Add($"- `{usage.Location}` ({usage.Candidate}): parameter `{usage.Parameter}`");
        if (findings.CallerFilePathUsages.Count == 0) lines.Add("(none)");

        lines.Add("");
        lines.Add("## BLOCKS SPLIT");
        var blocking = findings.BlockingFindings.OrderBy(f => f.Location, StringComparer.Ordinal).ToList();
        if (blocking.Count == 0) lines.Add("(none)");
        foreach (var finding in blocking)
            lines.Add($"- `{finding.Location}` ({finding.Candidate} -> {finding.TargetCandidate}): `{finding.Literal}`");

        return string.Join('\n', lines) + "\n";
    }
}
