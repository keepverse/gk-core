using FusionRpg.Tools.TestSplitAnalyzer;
using Xunit;

namespace FusionRpg.TestSplitAnalyzer.Tests;

/// <summary>
/// core-split-analyzer: `Grouping` computes strongly connected components of the candidate reference
/// graph — a cycle must share a project — and proposes one project per clean SCC (no edge into another
/// SCC), everything else in the residual. `Report` serializes the proposal deterministically.
/// </summary>
public sealed class GroupingTests
{
    static readonly IReadOnlyDictionary<string, int> NoFileCounts = new Dictionary<string, int>();

    // ── A2: A<->B mutual use -> one SCC {A,B}, proposed as one project ──────────────────────────────

    [Fact]
    public void A2_a_mutual_cycle_between_a_and_b_is_proposed_as_one_project()
    {
        var edges = new[]
        {
            new CandidateEdge("A", "B", "Ns.B.Helper", "B/Helper.cs", "A/Caller.cs:1"),
            new CandidateEdge("B", "A", "Ns.A.Other", "A/Other.cs", "B/BackCaller.cs:1"),
        };

        var result = Grouping.Build(new[] { "A", "B" }, edges, NoFileCounts, "FusionRpg.Core.Tests");

        var project = Assert.Single(result.CleanProjects);
        Assert.Equal(new[] { "A", "B" }, project.Candidates);
        Assert.Empty(result.ResidualCandidates);
    }

    // ── A3: A uses only production + shared -> A clean ──────────────────────────────────────────────

    [Fact]
    public void A3_a_candidate_with_no_cross_candidate_edges_is_clean()
    {
        // No edges at all touch "A" -- exactly what "uses only production + shared" looks like from
        // ReferenceGraph's own output, since production/shared references are never candidate edges.
        var result = Grouping.Build(new[] { "A" }, Array.Empty<CandidateEdge>(), NoFileCounts, "FusionRpg.Core.Tests");

        var project = Assert.Single(result.CleanProjects);
        Assert.Equal("A", project.Name);
        Assert.Equal(new[] { "A" }, project.Candidates);
        Assert.Empty(result.ResidualCandidates);
    }

    [Fact]
    public void A_one_way_dependency_puts_only_the_depending_side_in_the_residual()
    {
        // A -> B with no edge back: two separate size-1 SCCs. A has an outbound edge into another
        // SCC (B's), so A is not clean -- splitting A away would need a test-project ProjectReference
        // to B's new project, exactly the shape "test projects never reference test projects" forbids.
        // B has NO outbound edge anywhere, so B itself is clean regardless of who depends on it.
        var edges = new[] { new CandidateEdge("A", "B", "Ns.B.Helper", "B/Helper.cs", "A/Caller.cs:1") };

        var result = Grouping.Build(new[] { "A", "B" }, edges, NoFileCounts, "FusionRpg.Core.Tests");

        var project = Assert.Single(result.CleanProjects);
        Assert.Equal("B", project.Name);
        Assert.Equal(new[] { "A" }, result.ResidualCandidates);
    }

    [Fact]
    public void A_clean_multi_member_scc_is_named_from_its_largest_member()
    {
        var edges = new[]
        {
            new CandidateEdge("Small", "Big", "Ns.Big.Helper", "Big/Helper.cs", "Small/Caller.cs:1"),
            new CandidateEdge("Big", "Small", "Ns.Small.Other", "Small/Other.cs", "Big/BackCaller.cs:1"),
        };
        var fileCounts = new Dictionary<string, int> { ["Small"] = 2, ["Big"] = 20 };

        var result = Grouping.Build(new[] { "Small", "Big" }, edges, fileCounts, "FusionRpg.Core.Tests");

        var project = Assert.Single(result.CleanProjects);
        Assert.Equal("Big", project.Name);
    }

    // ── A7: same input twice, shuffled order -> byte-identical JSON ─────────────────────────────────

    [Fact]
    public void A7_shuffled_input_order_produces_byte_identical_json()
    {
        var candidatesInOrder = new[] { "A", "B", "C" };
        var candidatesShuffled = new[] { "C", "A", "B" };
        var edgesInOrder = new[]
        {
            new CandidateEdge("A", "B", "Ns.B.Helper", "B/Helper.cs", "A/Caller.cs:1"),
            new CandidateEdge("B", "A", "Ns.A.Other", "A/Other.cs", "B/BackCaller.cs:1"),
        };
        var edgesShuffled = edgesInOrder.Reverse().ToArray();
        var findings = new FindingsResult(
            Array.Empty<ContentRequirement>(),
            new[] { new OwnPathRequirement("C", "tests/Root/A/x.cs", "A", "C/File.cs:5", true) },
            Array.Empty<CallerFilePathUsage>(),
            Array.Empty<NamespaceFolderMismatch>(),
            Array.Empty<TraitLocation>());
        var requiredAssembliesInOrder = new[] { new RequiredAssembly("A", "FusionRpg.Core"), new RequiredAssembly("B", "FusionRpg.Data") };
        var requiredAssembliesShuffled = requiredAssembliesInOrder.Reverse().ToArray();
        var referenceGraph = new ReferenceGraphResult(edgesInOrder, Array.Empty<CollectionEdge>(), Array.Empty<InternalSymbolUsage>(), requiredAssembliesInOrder);
        var referenceGraphShuffled = new ReferenceGraphResult(edgesShuffled, Array.Empty<CollectionEdge>(), Array.Empty<InternalSymbolUsage>(), requiredAssembliesShuffled);
        var sharedFilesInOrder = new[] { "TestSupport/A.cs", "TestSupport/B.cs" };
        var sharedFilesShuffled = sharedFilesInOrder.Reverse().ToArray();

        var first = Grouping.Build(candidatesInOrder, edgesInOrder, NoFileCounts, "FusionRpg.Core.Tests");
        var second = Grouping.Build(candidatesShuffled, edgesShuffled, NoFileCounts, "FusionRpg.Core.Tests");

        var firstJson = Report.ToJson(first, referenceGraph, findings, sharedFilesInOrder, analyzerCommit: "abc123");
        var secondJson = Report.ToJson(second, referenceGraphShuffled, findings, sharedFilesShuffled, analyzerCommit: "abc123");

        Assert.Equal(firstJson, secondJson);
    }
}
