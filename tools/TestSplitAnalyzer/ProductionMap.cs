using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FusionRpg.Tools.TestSplitAnalyzer;

/// <summary>
/// core-registry-rekey K1: for each `gk-core/src/FusionRpg.Core/&lt;Area&gt;`, which TEST projects reference its
/// symbols. Evidence only — the map is read from compiled symbols in the SAME model the rest of the
/// analyzer uses (Roslyn compiler API over an already-built project's own output; no MSBuild, no
/// Workspaces), so an area owner can be derived from the production map instead of from a project
/// name that happens to match the folder (the "boundary by coincidence" the ideal warns about).
///
/// <para>Two halves, both pure: <see cref="BuildAreaIndex"/> turns the PRODUCTION compilation into a
/// `type display name -> area` table, and <see cref="AreasReferencedBy"/> turns one TEST compilation
/// into the set of areas it actually references. Neither writes anything.</para>
/// </summary>
public static class ProductionMap
{
    /// <summary>
    /// Every type declared under <paramref name="productionRoot"/> (repo-relative, forward slashes,
    /// trailing slash), keyed by its display name. The area is the FIRST path segment below the root,
    /// so `gk-core/src/FusionRpg.Core/Effects/Atoms/ChannelPool.cs` is area `Effects`. Types with no source in
    /// this compilation (metadata) are skipped: the table describes this project's own declarations.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildAreaIndex(
        Compilation production, Func<string, string> pathToRelative, string productionRoot)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in AllNamedTypes(production.Assembly.GlobalNamespace))
        {
            var declared = type.DeclaringSyntaxReferences.FirstOrDefault();
            if (declared is null) continue;
            var relative = pathToRelative(declared.SyntaxTree.FilePath);
            if (!relative.StartsWith(productionRoot, StringComparison.Ordinal)) continue;
            var rest = relative[productionRoot.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0) continue;
            index[type.ToDisplayString()] = rest[..slash];
        }
        return index;
    }

    /// <summary>
    /// The areas (values of <paramref name="areaIndex"/>) whose types <paramref name="test"/> references.
    /// A reference is resolved through the test compilation's own semantic model, so only a symbol the
    /// compiler actually bound counts — a name in a comment or a string never does.
    ///
    /// <para><paramref name="onlySourceFiles"/> restricts the walk to the project's OWN sources. The
    /// compilation also carries the files the project links in from outside its directory (the shared
    /// bootstrap and `gk-core/tests/Shared/KeepverseRoots.cs`) — those are shared TEST INFRASTRUCTURE compiled
    /// into every project, so counting their references as the project's own makes every area look
    /// referenced by every project (measured 2026-09-23: 17 of 35 areas went to 70-71 of 83 projects,
    /// which is the whole `core` group, once they were parsed). `null` keeps the whole compilation, the
    /// shape the K-T1 fixture uses on synthetic trees that have no linked sources at all.</para>
    /// </summary>
    public static IReadOnlyList<string> AreasReferencedBy(
        Compilation test, IReadOnlyDictionary<string, string> areaIndex,
        IReadOnlySet<string>? onlySourceFiles = null)
    {
        var areas = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var tree in test.SyntaxTrees)
        {
            if (onlySourceFiles is not null && !onlySourceFiles.Contains(tree.FilePath)) continue;
            var model = test.GetSemanticModel(tree);
            foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(name).Symbol;
                if (symbol is null) continue;
                var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
                while (type is not null)
                {
                    if (areaIndex.TryGetValue(type.ToDisplayString(), out var area))
                    {
                        areas.Add(area);
                        break;
                    }
                    type = type.ContainingType;
                }
            }
        }
        return areas.ToArray();
    }

    /// <summary>
    /// K-T1's fixture, runnable without a test project: one synthetic production project declaring
    /// `Alpha` and `Beta` areas, and two synthetic test projects that each reference an `Alpha` symbol
    /// (one of them also a `Beta` symbol). Asserts the map lists BOTH test projects for `Alpha`, and
    /// only the referencing one for `Beta`. Returns the areas each synthetic project resolved to, plus
    /// the assertion verdict, so the caller can print a real result instead of an assumed one.
    /// </summary>
    public static (bool Ok, string Report) RunFixture()
    {
        var core = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var production = CSharpCompilation.Create(
            "Fixture.Production",
            new[]
            {
                Tree("src/FusionRpg.Core/Alpha/AlphaWidget.cs", "namespace Fixture.Core.Alpha; public sealed class AlphaWidget { public static int Value => 1; }"),
                Tree("src/FusionRpg.Core/Alpha/AlphaGear.cs", "namespace Fixture.Core.Alpha; public sealed class AlphaGear { public static int Value => 2; }"),
                Tree("src/FusionRpg.Core/Beta/BetaWidget.cs", "namespace Fixture.Core.Beta; public sealed class BetaWidget { public static int Value => 3; }"),
            },
            new[] { core });

        var index = BuildAreaIndex(production, p => p, "src/FusionRpg.Core/");
        var reference = production.ToMetadataReference();

        var testOne = CSharpCompilation.Create("Fixture.TestOne", new[]
        {
            Tree("tests/Fixture.TestOne/AlphaTests.cs",
                "namespace Fixture.TestOne; public static class AlphaTests { public static int Run() => Fixture.Core.Alpha.AlphaWidget.Value; }"),
        }, new MetadataReference[] { core, reference });
        var testTwo = CSharpCompilation.Create("Fixture.TestTwo", new[]
        {
            Tree("tests/Fixture.TestTwo/BetaTests.cs",
                "namespace Fixture.TestTwo; public static class BetaTests { public static int Run() => Fixture.Core.Beta.BetaWidget.Value; }"),
            Tree("tests/Fixture.TestTwo/AlsoAlphaTests.cs",
                "namespace Fixture.TestTwo; public static class AlsoAlphaTests { public static int Run() => Fixture.Core.Alpha.AlphaGear.Value; }"),
        }, new MetadataReference[] { core, reference });

        var one = AreasReferencedBy(testOne, index);
        var two = AreasReferencedBy(testTwo, index);
        var alpha = new[] { "Fixture.TestOne", "Fixture.TestTwo" }
            .Where(p => (p == "Fixture.TestOne" ? one : two).Contains("Alpha", StringComparer.Ordinal))
            .ToArray();
        var beta = new[] { "Fixture.TestOne", "Fixture.TestTwo" }
            .Where(p => (p == "Fixture.TestOne" ? one : two).Contains("Beta", StringComparer.Ordinal))
            .ToArray();

        var ok = alpha.Length == 2
                 && alpha.Contains("Fixture.TestOne", StringComparer.Ordinal)
                 && alpha.Contains("Fixture.TestTwo", StringComparer.Ordinal)
                 && beta.Length == 1
                 && beta[0] == "Fixture.TestTwo";
        var report = string.Join(Environment.NewLine, new[]
        {
            $"K-T1 fixture: area index types={index.Count} (areas: {string.Join(", ", index.Values.Distinct().OrderBy(a => a, StringComparer.Ordinal))})",
            $"  Alpha referenced by: {string.Join(", ", alpha)}",
            $"  Beta  referenced by: {string.Join(", ", beta)}",
            ok ? "K-T1 PASS - the production map lists both synthetic projects that reference Alpha, and only TestTwo for Beta"
               : "K-T1 FAIL - the production map did not match the fixture's own expectations",
        });
        return (ok, report);
    }

    static SyntaxTree Tree(string path, string source) => CSharpSyntaxTree.ParseText(source, path: path);

    static IEnumerable<INamedTypeSymbol> AllNamedTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in Nested(type)) yield return nested;
        }
        foreach (var child in ns.GetNamespaceMembers())
        {
            foreach (var type in AllNamedTypes(child)) yield return type;
        }
    }

    static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type)
    {
        foreach (var child in type.GetTypeMembers())
        {
            yield return child;
            foreach (var inner in Nested(child)) yield return inner;
        }
    }
}
