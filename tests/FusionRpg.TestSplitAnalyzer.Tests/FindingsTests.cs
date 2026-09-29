using FusionRpg.Tools.TestSplitAnalyzer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace FusionRpg.TestSplitAnalyzer.Tests;

/// <summary>
/// core-split-analyzer: `Findings` reports the string-keyed and location-sensitive facts a compiler
/// symbol walk cannot see. Every fixture is compiled **in memory** — no disk (`testing-standard.md` R1).
/// </summary>
public sealed class FindingsTests
{
    const string OwnProjectRoot = "tests/FusionRpg.Core.Tests/";
    const string RootNamespace = "FusionRpg.Core.Tests";
    static readonly string[] NoToolNames = Array.Empty<string>();
    static readonly HashSet<string> NoLinkedFiles = new(StringComparer.Ordinal);

    static CSharpCompilation Compile(params (string Path, string Source)[] files)
    {
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Source, path: f.Path)).ToArray();
        var references = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        return CSharpCompilation.Create("UnderTest", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    static string Classify(string path) => ReferenceGraph.Classify(path, NoLinkedFiles);
    static string Identity(string path) => path;

    static FindingsResult Run(CSharpCompilation compilation, IReadOnlyList<string>? toolNames = null, string ownRoot = OwnProjectRoot, string rootNamespace = RootNamespace) =>
        Findings.Build(compilation, Classify, Identity, ownRoot, toolNames ?? NoToolNames, rootNamespace);

    // ── A5: a string literal "fixtures/combat/x.json" in A -> content requirement on A ─────────────

    [Fact]
    public void A5_a_fixtures_literal_is_a_content_requirement()
    {
        var compilation = Compile(("A/Test.cs",
            """namespace Ns.A; public class Test { const string Path = "fixtures/combat/x.json"; }"""));

        var result = Run(compilation);

        var req = Assert.Single(result.ContentRequirements);
        Assert.Equal("A", req.Candidate);
        Assert.Equal("fixtures/combat/x.json", req.Literal);
    }

    [Fact]
    public void A_goldens_literal_is_also_a_content_requirement()
    {
        var compilation = Compile(("A/Test.cs",
            """namespace Ns.A; public class Test { const string Path = "Goldens/battle-1.json"; }"""));

        var result = Run(compilation);

        Assert.Single(result.ContentRequirements);
    }

    [Fact]
    public void A_literal_naming_a_tool_assembly_is_a_content_requirement()
    {
        var compilation = Compile(("A/Test.cs",
            """namespace Ns.A; public class Test { const string Exe = "tools/CombatSim/bin/Release/net8.0/CombatSim.exe"; }"""));

        var result = Run(compilation, toolNames: new[] { "CombatSim" });

        Assert.Single(result.ContentRequirements);
    }

    // ── A9: an own-path literal naming another candidate -> BLOCKS SPLIT ────────────────────────────

    [Fact]
    public void A9_an_own_path_literal_crossing_candidates_blocks_split()
    {
        var compilation = Compile(("A/Test.cs",
            """namespace Ns.A; public class Test { const string Path = "tests/FusionRpg.Core.Tests/B/x.cs"; }"""));

        var result = Run(compilation);

        var req = Assert.Single(result.OwnPathRequirements);
        Assert.Equal("A", req.Candidate);
        Assert.Equal("B", req.TargetCandidate);
        Assert.True(req.BlocksSplit);
        Assert.Contains(req, result.BlockingFindings);
    }

    [Fact]
    public void An_own_path_literal_naming_its_own_candidate_does_not_block_split()
    {
        var compilation = Compile(("A/Test.cs",
            """namespace Ns.A; public class Test { const string Path = "tests/FusionRpg.Core.Tests/A/Sibling.cs"; }"""));

        var result = Run(compilation);

        var req = Assert.Single(result.OwnPathRequirements);
        Assert.False(req.BlocksSplit);
        Assert.Empty(result.BlockingFindings);
    }

    // ── A10: a [CallerFilePath] parameter -> listed as location-sensitive ───────────────────────────

    [Fact]
    public void A10_a_callerfilepath_parameter_is_listed()
    {
        var compilation = Compile(("A/Test.cs", """
            namespace Ns.A;
            using System.Runtime.CompilerServices;
            public class Test
            {
                public static string Root([CallerFilePath] string here = "") => here;
            }
            """));

        var result = Run(compilation);

        var usage = Assert.Single(result.CallerFilePathUsages);
        Assert.Equal("A", usage.Candidate);
        Assert.Equal("here", usage.Parameter);
    }

    // ── A11: a file in folder B/C/ declaring namespace Root.B -> namespace-vs-folder mismatch ───────

    [Fact]
    public void A11_a_namespace_missing_a_folder_segment_is_a_mismatch()
    {
        var compilation = Compile(("B/C/Test.cs", "namespace FusionRpg.Core.Tests.B; public class Test { }"));

        var result = Run(compilation);

        var mismatch = Assert.Single(result.NamespaceFolderMismatches);
        Assert.Equal("FusionRpg.Core.Tests.B", mismatch.DeclaredNamespace);
        Assert.Equal("FusionRpg.Core.Tests.B.C", mismatch.ImpliedNamespace);
    }

    [Fact]
    public void A_namespace_matching_its_folder_is_not_reported()
    {
        var compilation = Compile(("B/C/Test.cs", "namespace FusionRpg.Core.Tests.B.C; public class Test { }"));

        var result = Run(compilation);

        Assert.Empty(result.NamespaceFolderMismatches);
    }

    [Fact]
    public void A_root_files_namespace_is_compared_against_the_bare_root_namespace()
    {
        var compilation = Compile(("RootFile.cs", "namespace FusionRpg.Core.Tests; public class Test { }"));

        var result = Run(compilation);

        Assert.Empty(result.NamespaceFolderMismatches);
    }
}
