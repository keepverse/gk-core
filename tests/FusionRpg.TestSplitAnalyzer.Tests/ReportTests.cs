using Xunit;

namespace FusionRpg.Tools.TestSplitAnalyzer.Tests;

/// <summary>
/// TVB-F14: the markdown report names the cross-candidate SYMBOL edges. The JSON draft has carried them
/// since TVB1.7 (`ReferenceGraph.Edges`), but the markdown never printed them, so a manifest author
/// reading the report could not see the shape that made the Atoms increment need two out-of-folder files
/// (`Atoms/EffectSeedFixtureOracle.cs` and `World/StructureCatalogTestBootstrap.cs`, a
/// `[ModuleInitializer]`) — TVB1.10 reported 0 `BLOCKS SPLIT` because the existing blocker class is
/// literal-based. The section is a reading, never a failure: each edge is answered either by the
/// manifest's `links` field or by keeping both candidates in the residual, which the SCC grouping decides.
/// </summary>
public class ReportTests
{
    static ReferenceGraphResult Graph(params CandidateEdge[] edges) =>
        new(edges, Array.Empty<CollectionEdge>(), Array.Empty<InternalSymbolUsage>(), Array.Empty<RequiredAssembly>());

    static FindingsResult NoFindings() => new(
        Array.Empty<ContentRequirement>(), Array.Empty<OwnPathRequirement>(),
        Array.Empty<CallerFilePathUsage>(), Array.Empty<NamespaceFolderMismatch>(),
        Array.Empty<TraitLocation>());

    static GroupingResult TwoCleanCandidates() =>
        Grouping.Build(
            new[] { "Atoms", "World" }, Array.Empty<CandidateEdge>(), new Dictionary<string, int>(),
            "FusionRpg.Core.Tests");

    [Fact]
    public void The_report_names_every_cross_candidate_symbol_edge()
    {
        var graph = Graph(
            new CandidateEdge("Atoms", "World", "StructureCatalog", "World/B.cs:3", "Atoms/A.cs:9"),
            new CandidateEdge("World", "Atoms", "AlphaGear", "Atoms/A.cs:1", "World/B.cs:4"));

        var markdown = Report.ToMarkdown(TwoCleanCandidates(), graph, NoFindings(), Array.Empty<string>());

        Assert.Contains("## Cross-candidate symbol edges (2 — a reading)", markdown, StringComparison.Ordinal);
        Assert.Contains("`Atoms` uses `StructureCatalog` from `World` (declared at World/B.cs:3, used at Atoms/A.cs:9)",
            markdown, StringComparison.Ordinal);
        Assert.Contains("`World` uses `AlphaGear` from `Atoms` (declared at Atoms/A.cs:1, used at World/B.cs:4)",
            markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_edge_set_says_none_rather_than_printing_an_empty_section()
    {
        var markdown = Report.ToMarkdown(TwoCleanCandidates(), Graph(), NoFindings(), Array.Empty<string>());

        Assert.Contains("## Cross-candidate symbol edges (0 — a reading)", markdown, StringComparison.Ordinal);
        Assert.Contains("(none)", markdown, StringComparison.Ordinal);
    }
}
