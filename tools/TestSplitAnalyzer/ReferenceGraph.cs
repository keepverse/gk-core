using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FusionRpg.Tools.TestSplitAnalyzer;

/// <summary>One edge = one fact the manifest reviewer can check by opening the file (core-split-analyzer
/// Code style). <see cref="DeclaredIn"/> and <see cref="UsedAt"/> are both repo-relative locations.</summary>
public sealed record CandidateEdge(string From, string To, string Symbol, string DeclaredIn, string UsedAt);

/// <summary>A `[Collection("Name")]` user in a different candidate than the `[CollectionDefinition("Name")]`
/// that owns it — `[CollectionDefinition]` must sit in the same assembly as its users (core-split-analyzer
/// Objective), so this is exactly what a split would break.</summary>
public sealed record CollectionEdge(string CollectionName, string DefinedIn, string UsedIn, string DefinedAt, string UsedAt);

/// <summary>A candidate reaching an `internal` symbol from a referenced (already-compiled) assembly —
/// the split's internals grant is to one assembly name (core-split-apply A3), so every candidate that
/// needs it must be named.</summary>
public sealed record InternalSymbolUsage(string Candidate, string Symbol, string UsedAt);

/// <summary>A metadata assembly a candidate's symbols resolve into — maps back to the `ProjectReference`
/// (or package) the candidate's own project needs after a split (core-split-analyzer Design: "required
/// assemblies (from each referenced symbol's containing assembly -&gt; maps back to a ProjectReference)").</summary>
public sealed record RequiredAssembly(string Candidate, string AssemblyName);

public sealed record ReferenceGraphResult(
    IReadOnlyList<CandidateEdge> Edges,
    IReadOnlyList<CollectionEdge> CollectionEdges,
    IReadOnlyList<InternalSymbolUsage> InternalUsages,
    IReadOnlyList<RequiredAssembly> RequiredAssemblies);

/// <summary>
/// Walks a built <see cref="Compilation"/>'s syntax trees and classifies every symbol reference that
/// crosses a candidate boundary. Built on the Roslyn COMPILER API only (no <c>Workspaces</c>/
/// <c>MSBuildWorkspace</c> — core-split-analyzer Design), so it never needs the project to be opened by
/// MSBuild, only already built (the compilation's metadata references come from the build output).
/// </summary>
public static class ReferenceGraph
{
    /// <summary>
    /// Candidate classification (core-split-analyzer Design): each top-level folder is one candidate;
    /// each root file is its own candidate (no natural home). <c>TestSupport/</c>, the bootstrap,
    /// <c>AssemblyInfo.cs</c> and linked files are always <c>"Shared"</c>.
    /// </summary>
    public static string Classify(string repoRelativePath, IReadOnlySet<string> linkedRelativePaths)
    {
        var normalized = repoRelativePath.Replace('\\', '/');
        if (linkedRelativePaths.Contains(normalized)) return "Shared";

        var fileName = Path.GetFileName(normalized);
        if (fileName is "AssemblyInfo.cs" or "ContractTuningTestBootstrap.cs") return "Shared";

        var firstSlash = normalized.IndexOf('/');
        if (firstSlash < 0) return Path.GetFileNameWithoutExtension(normalized); // a root file, its own candidate

        var topFolder = normalized[..firstSlash];
        return topFolder == "TestSupport" ? "Shared" : topFolder;
    }

    /// <param name="compilation">Already built (real run: from the project's build output metadata).</param>
    /// <param name="classify">Repo-relative path -&gt; candidate id, or <c>"Shared"</c>.</param>
    /// <param name="pathToRelative">Absolute (or tree) file path -&gt; repo-relative path with forward slashes.</param>
    public static ReferenceGraphResult Build(Compilation compilation, Func<string, string> classify, Func<string, string> pathToRelative)
    {
        var edges = new List<CandidateEdge>();
        var internalUsages = new List<InternalSymbolUsage>();
        var requiredAssemblies = new HashSet<RequiredAssembly>();
        var definitions = new Dictionary<string, (string Candidate, string Location)>(StringComparer.Ordinal);
        var usages = new List<(string Name, string Candidate, string Location)>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            var relativeSelf = pathToRelative(tree.FilePath);
            var from = classify(relativeSelf);
            var root = tree.GetRoot();

            foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                foreach (var attribute in classDecl.AttributeLists.SelectMany(l => l.Attributes))
                {
                    // The simple (rightmost) name, so both `[Collection(...)]` (via `using Xunit;`)
                    // and `[Xunit.Collection(...)]` (fully qualified) are recognised the same way.
                    var attributeName = SimpleAttributeName(attribute.Name);
                    var location = $"{relativeSelf}:{Line(classDecl)}";
                    if (attributeName is "CollectionDefinition" or "CollectionDefinitionAttribute")
                    {
                        var name = ExtractStringArgument(attribute);
                        if (name is not null) definitions[name] = (from, location);
                    }
                    else if (attributeName is "Collection" or "CollectionAttribute")
                    {
                        var name = ExtractStringArgument(attribute);
                        if (name is not null) usages.Add((name, from, location));
                    }
                }
            }

            if (from == "Shared") continue; // a shared file's own references are not candidate coupling

            var model = compilation.GetSemanticModel(tree);
            foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(identifier).Symbol;
                if (symbol is null) continue;
                // A namespace symbol (e.g. every segment of a `using Ns.B;` directive) is not "declared
                // in one file" in any meaningful sense — many files across many candidates can
                // contribute to the same namespace, so it is not a coupling fact a manifest reviewer
                // can check by opening a file. Only member/type symbols are reported.
                if (symbol.Kind == SymbolKind.Namespace) continue;

                var declaredPath = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
                if (declaredPath is null)
                {
                    // No source in THIS compilation -> a metadata symbol from a referenced (already
                    // built) assembly. `internal` there is exactly the internals-grant case (A6); every
                    // such symbol's containing assembly is also a required-reference reading, regardless
                    // of accessibility.
                    if (symbol.DeclaredAccessibility == Accessibility.Internal)
                        internalUsages.Add(new InternalSymbolUsage(from, symbol.ToDisplayString(), $"{relativeSelf}:{Line(identifier)}"));
                    var assemblyName = symbol.ContainingAssembly?.Name;
                    if (assemblyName is not null) requiredAssemblies.Add(new RequiredAssembly(from, assemblyName));
                    continue;
                }

                var declaredRelative = pathToRelative(declaredPath);
                var to = classify(declaredRelative);
                if (to == from || to == "Shared") continue; // same candidate, or a reference TO shared (tracked separately, not a coupling edge)

                edges.Add(new CandidateEdge(from, to, symbol.ToDisplayString(), declaredRelative, $"{relativeSelf}:{Line(identifier)}"));
            }
        }

        var collectionEdges = new List<CollectionEdge>();
        foreach (var (name, candidate, location) in usages)
        {
            if (!definitions.TryGetValue(name, out var definition)) continue;
            if (definition.Candidate == candidate) continue;
            collectionEdges.Add(new CollectionEdge(name, definition.Candidate, candidate, definition.Location, location));
        }

        var sortedRequiredAssemblies = requiredAssemblies
            .OrderBy(r => r.Candidate, StringComparer.Ordinal).ThenBy(r => r.AssemblyName, StringComparer.Ordinal)
            .ToList();

        return new ReferenceGraphResult(edges, collectionEdges, internalUsages, sortedRequiredAssemblies);
    }

    static string SimpleAttributeName(NameSyntax name) => name switch
    {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
        SimpleNameSyntax simple => simple.Identifier.Text,
        _ => name.ToString()
    };

    static string? ExtractStringArgument(AttributeSyntax attribute)
    {
        var argument = attribute.ArgumentList?.Arguments.FirstOrDefault();
        return argument?.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal
            ? literal.Token.ValueText
            : null;
    }

    static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
