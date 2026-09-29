using System.Runtime.InteropServices;
using FusionRpg.Tools.TestSplitAnalyzer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace FusionRpg.TestSplitAnalyzer.Tests;

/// <summary>
/// core-split-analyzer: `ReferenceGraph` walks a built <see cref="Microsoft.CodeAnalysis.Compilation"/>
/// and reports every symbol reference that crosses a candidate boundary. Every fixture here is compiled
/// **in memory** (`CSharpSyntaxTree.ParseText` over strings, references from `typeof(object).Assembly`
/// etc.) — no temp directories, no disk (`testing-standard.md` R1). This commit covers A1, A4 and A6;
/// A5/A9/A10/A11 land in TVB1.8, A2/A3/A7 in TVB1.9.
/// </summary>
public sealed class ReferenceGraphTests
{
    static readonly MetadataReference[] BclReferences = BuildBclReferences();

    static MetadataReference[] BuildBclReferences()
    {
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var names = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Linq.dll", "netstandard.dll" };
        var refs = new List<MetadataReference> { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        foreach (var name in names)
        {
            var path = Path.Combine(runtimeDir, name);
            if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
        }
        return refs.ToArray();
    }

    /// <summary>Every path is a repo-relative "file path" a real project would have — `Classify` and
    /// `pathToRelative` in the tests below just treat the tree's own <c>FilePath</c> as already relative.</summary>
    static CSharpCompilation Compile(string assemblyName, IEnumerable<MetadataReference> extraReferences, params (string Path, string Source)[] files)
    {
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Source, path: f.Path)).ToArray();
        var references = BclReferences.Concat(extraReferences).ToArray();
        return CSharpCompilation.Create(assemblyName, trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    static readonly HashSet<string> NoLinkedFiles = new(StringComparer.Ordinal);
    static string Classify(string path) => ReferenceGraph.Classify(path, NoLinkedFiles);
    static string Identity(string path) => path; // fixture paths are already repo-relative

    // ── A1: folder A uses a helper declared in folder B -> edge with symbol + both locations ────────

    [Fact]
    public void A1_folder_a_uses_a_helper_declared_in_folder_b()
    {
        var compilation = Compile("UnderTest", Array.Empty<MetadataReference>(),
            ("B/Helper.cs", "namespace Ns.B; public class Helper { public static void Do() { } }"),
            ("A/Caller.cs", "namespace Ns.A; using Ns.B; public class Caller { public void Run() { Helper.Do(); } }"));

        var result = ReferenceGraph.Build(compilation, Classify, Identity);

        // `Helper.Do()` is two real facts (the type `Helper` and the method `Do`, both declared in
        // B/Helper.cs), not one — every edge found must have this shape, and at least one must.
        Assert.NotEmpty(result.Edges);
        Assert.All(result.Edges, edge =>
        {
            Assert.Equal("A", edge.From);
            Assert.Equal("B", edge.To);
            Assert.Equal("B/Helper.cs", edge.DeclaredIn);
            Assert.StartsWith("A/Caller.cs:", edge.UsedAt, StringComparison.Ordinal);
        });
        Assert.Contains(result.Edges, e => e.Symbol.Contains("Helper", StringComparison.Ordinal));
    }

    [Fact]
    public void A_reference_within_the_same_candidate_is_not_an_edge()
    {
        var compilation = Compile("UnderTest", Array.Empty<MetadataReference>(),
            ("A/Helper.cs", "namespace Ns.A; public class Helper { public static void Do() { } }"),
            ("A/Caller.cs", "namespace Ns.A; public class Caller { public void Run() { Helper.Do(); } }"));

        var result = ReferenceGraph.Build(compilation, Classify, Identity);

        Assert.Empty(result.Edges);
    }

    [Fact]
    public void A_reference_to_a_shared_file_is_not_reported_as_a_coupling_edge()
    {
        var compilation = Compile("UnderTest", Array.Empty<MetadataReference>(),
            ("TestSupport/Shared.cs", "namespace Ns.Shared; public class Shared { public static void Do() { } }"),
            ("A/Caller.cs", "namespace Ns.A; using Ns.Shared; public class Caller { public void Run() { Shared.Do(); } }"));

        var result = ReferenceGraph.Build(compilation, Classify, Identity);

        Assert.Empty(result.Edges); // a reference TO shared is a "shared requirement", not a coupling edge
    }

    // ── A4: a [CollectionDefinition] in A used by B -> reported as a cross-candidate collection edge ─

    [Fact]
    public void A4_a_collection_definition_in_a_used_by_b_is_a_cross_candidate_collection_edge()
    {
        const string definitionSource = """
            namespace Ns.A;
            [Xunit.CollectionDefinition("class-system-baselines")]
            public class BaselineCollection { }
            """;
        const string usageSource = """
            namespace Ns.B;
            [Xunit.Collection("class-system-baselines")]
            public class SomeTests { }
            """;
        var compilation = Compile("UnderTest", Array.Empty<MetadataReference>(),
            ("A/BaselineCollection.cs", definitionSource),
            ("B/SomeTests.cs", usageSource));

        var result = ReferenceGraph.Build(compilation, Classify, Identity);

        var edge = Assert.Single(result.CollectionEdges);
        Assert.Equal("class-system-baselines", edge.CollectionName);
        Assert.Equal("A", edge.DefinedIn);
        Assert.Equal("B", edge.UsedIn);
    }

    [Fact]
    public void A_collection_defined_and_used_in_the_same_candidate_is_not_reported()
    {
        const string source = """
            namespace Ns.A;
            [Xunit.CollectionDefinition("same")]
            public class DefCollection { }
            [Xunit.Collection("same")]
            public class SomeTests { }
            """;
        var compilation = Compile("UnderTest", Array.Empty<MetadataReference>(), ("A/File.cs", source));

        var result = ReferenceGraph.Build(compilation, Classify, Identity);

        Assert.Empty(result.CollectionEdges);
    }

    // ── A6: an internal production symbol used in A -> reported as an internals requirement ─────────

    [Fact]
    public void A6_an_internal_production_symbol_used_in_a_is_reported()
    {
        var productionCompilation = Compile("Production", Array.Empty<MetadataReference>(),
            ("Prod.cs", """
                [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("UnderTest")]
                namespace Prod;
                internal class Widget { public static void Do() { } }
                """));
        using var stream = new MemoryStream();
        var emit = productionCompilation.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics.Select(d => d.ToString())));
        var productionReference = MetadataReference.CreateFromImage(stream.ToArray());

        var compilation = Compile("UnderTest", new[] { productionReference },
            ("A/Caller.cs", "namespace Ns.A; using Prod; public class Caller { public void Run() { Widget.Do(); } }"));

        var result = ReferenceGraph.Build(compilation, Classify, Identity);

        var usage = Assert.Single(result.InternalUsages);
        Assert.Equal("A", usage.Candidate);
        Assert.Contains("Widget", usage.Symbol, StringComparison.Ordinal);
        Assert.StartsWith("A/Caller.cs:", usage.UsedAt, StringComparison.Ordinal);
        Assert.Empty(result.Edges); // a metadata symbol has no source in THIS compilation -> no candidate edge
    }

    // ── Classify (used by ReferenceGraph.Build and the real analyzer alike) ─────────────────────────

    [Theory]
    [InlineData("Combat/CombatTests.cs", "Combat")]
    [InlineData("Combat/Sub/DeepTests.cs", "Combat")]
    [InlineData("RootFile.cs", "RootFile")]
    [InlineData("TestSupport/Helper.cs", "Shared")]
    [InlineData("AssemblyInfo.cs", "Shared")]
    [InlineData("ContractTuningTestBootstrap.cs", "Shared")]
    public void Classify_assigns_the_expected_candidate(string path, string expected)
    {
        Assert.Equal(expected, ReferenceGraph.Classify(path, NoLinkedFiles));
    }

    [Fact]
    public void Classify_treats_a_linked_file_as_shared()
    {
        var linked = new HashSet<string>(StringComparer.Ordinal) { "DataTestStore.cs" };
        Assert.Equal("Shared", ReferenceGraph.Classify("DataTestStore.cs", linked));
    }
}
