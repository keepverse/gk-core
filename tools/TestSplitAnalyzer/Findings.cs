using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FusionRpg.Tools.TestSplitAnalyzer;

/// <summary>A string literal naming a content path (`fixtures/…`, `Goldens/…`) or a tool assembly —
/// the candidate needs that input/tool present at the same relative location after a split.</summary>
public sealed record ContentRequirement(string Candidate, string Literal, string Location);

/// <summary>A string literal naming a path inside the project's OWN tree (e.g.
/// `tests/FusionRpg.Core.Tests/B/x.cs`). <see cref="BlocksSplit"/> is true when the literal's target
/// candidate differs from the candidate holding the literal — a move cannot fix this (split mode never
/// edits content, core-split-apply A2), so it must be fixed in its own commit before either end moves.</summary>
public sealed record OwnPathRequirement(string Candidate, string Literal, string TargetCandidate, string Location, bool BlocksSplit);

/// <summary>A `[CallerFilePath]` parameter — the split keeps these working only because of the depth
/// invariant (new projects sit at the same depth as the residual), and the report is what lets the
/// manifest reviewer check that.</summary>
public sealed record CallerFilePathUsage(string Candidate, string Parameter, string Location);

/// <summary>A file whose declared namespace does not match what its folder path implies. The split
/// carries these unchanged (it never rewrites a namespace), but a later `FileMove move` would rewrite
/// one, so the report lists them.</summary>
public sealed record NamespaceFolderMismatch(string Candidate, string DeclaredNamespace, string ImpliedNamespace, string Location);

/// <summary>Where a `[Trait("VerificationId", …)]` or `[Trait("Category", …)]` lives — feeds
/// `core-registry-rekey` and the CI BalanceGuard step (core-split-analyzer Design).</summary>
public sealed record TraitLocation(string Candidate, string TraitName, string Value, string Location);

public sealed record FindingsResult(
    IReadOnlyList<ContentRequirement> ContentRequirements,
    IReadOnlyList<OwnPathRequirement> OwnPathRequirements,
    IReadOnlyList<CallerFilePathUsage> CallerFilePathUsages,
    IReadOnlyList<NamespaceFolderMismatch> NamespaceFolderMismatches,
    IReadOnlyList<TraitLocation> TraitLocations)
{
    public IEnumerable<OwnPathRequirement> BlockingFindings => OwnPathRequirements.Where(r => r.BlocksSplit);
}

/// <summary>
/// String-keyed and location-sensitive facts a compiler symbol walk (<see cref="ReferenceGraph"/>)
/// cannot see: literal paths, `[CallerFilePath]` parameters, and namespace-vs-folder mismatches
/// (core-split-analyzer Design, "string-keyed inputs a compiler cannot see").
/// </summary>
public static class Findings
{
    static readonly string[] ContentLiteralMarkers = { "fixtures/", "Goldens/" };

    /// <param name="ownProjectRoot">The project's own repo-relative directory, trailing slash included
    /// (e.g. <c>"gk-core/tests/FusionRpg.Core.Tests/"</c>) — a literal naming a path under this is the
    /// own-path case (A9); it is never itself a content-requirement match.</param>
    /// <param name="toolAssemblyNames">Tool assembly names a cold-process test might shell out to
    /// (e.g. <c>"CombatSim"</c>) — a literal containing one is a required-tool finding.</param>
    /// <param name="rootNamespace">The project's `RootNamespace`, for the namespace-vs-folder check.</param>
    public static FindingsResult Build(
        Compilation compilation,
        Func<string, string> classify,
        Func<string, string> pathToRelative,
        string ownProjectRoot,
        IReadOnlyList<string> toolAssemblyNames,
        string rootNamespace)
    {
        var contentRequirements = new List<ContentRequirement>();
        var ownPathRequirements = new List<OwnPathRequirement>();
        var callerFilePathUsages = new List<CallerFilePathUsage>();
        var namespaceMismatches = new List<NamespaceFolderMismatch>();
        var traitLocations = new List<TraitLocation>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            var relativeSelf = pathToRelative(tree.FilePath);
            var candidate = classify(relativeSelf);
            var root = tree.GetRoot();

            foreach (var token in root.DescendantTokens())
            {
                if (!token.IsKind(SyntaxKind.StringLiteralToken)) continue;
                var text = token.ValueText;
                var location = $"{relativeSelf}:{Line(token)}";

                if (text.Contains(ownProjectRoot, StringComparison.Ordinal))
                {
                    var index = text.IndexOf(ownProjectRoot, StringComparison.Ordinal);
                    var afterRoot = text[(index + ownProjectRoot.Length)..];
                    var targetCandidate = classify(afterRoot);
                    ownPathRequirements.Add(new OwnPathRequirement(candidate, text, targetCandidate, location, targetCandidate != candidate));
                    continue; // an own-path literal is never also counted as a generic content requirement
                }

                if (ContentLiteralMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal)) ||
                    toolAssemblyNames.Any(name => text.Contains(name, StringComparison.Ordinal)))
                {
                    contentRequirements.Add(new ContentRequirement(candidate, text, location));
                }
            }

            foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>())
            {
                foreach (var attribute in parameter.AttributeLists.SelectMany(l => l.Attributes))
                {
                    var name = SimpleAttributeName(attribute.Name);
                    if (name is "CallerFilePath" or "CallerFilePathAttribute")
                        callerFilePathUsages.Add(new CallerFilePathUsage(candidate, parameter.Identifier.Text, $"{relativeSelf}:{Line(parameter)}"));
                }
            }

            // [Trait("VerificationId"|"Category", "<value>")] on a class or a method — feeds
            // core-registry-rekey and the CI BalanceGuard step (Design section).
            var attributeOwners = root.DescendantNodes()
                .Where(n => n is ClassDeclarationSyntax or MethodDeclarationSyntax);
            foreach (var owner in attributeOwners)
            {
                var attributeLists = owner switch
                {
                    ClassDeclarationSyntax c => c.AttributeLists,
                    MethodDeclarationSyntax m => m.AttributeLists,
                    _ => default
                };
                foreach (var attribute in attributeLists.SelectMany(l => l.Attributes))
                {
                    if (SimpleAttributeName(attribute.Name) is not ("Trait" or "TraitAttribute")) continue;
                    var arguments = attribute.ArgumentList?.Arguments ?? default;
                    if (arguments.Count < 2) continue;
                    if (ArgumentAsString(arguments[0]) is not { } traitName) continue;
                    if (ArgumentAsString(arguments[1]) is not { } traitValue) continue;
                    traitLocations.Add(new TraitLocation(candidate, traitName, traitValue, $"{relativeSelf}:{Line(owner)}"));
                }
            }

            var declaredNamespace = DeclaredNamespace(root);
            if (declaredNamespace is not null)
            {
                var implied = ImpliedNamespace(relativeSelf, rootNamespace);
                if (declaredNamespace != implied)
                    namespaceMismatches.Add(new NamespaceFolderMismatch(candidate, declaredNamespace, implied, relativeSelf));
            }
        }

        return new FindingsResult(contentRequirements, ownPathRequirements, callerFilePathUsages, namespaceMismatches, traitLocations);
    }

    static string? ArgumentAsString(AttributeArgumentSyntax argument) =>
        argument.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal
            ? literal.Token.ValueText
            : null;

    static string? DeclaredNamespace(SyntaxNode root)
    {
        var fileScoped = root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
        if (fileScoped is not null) return fileScoped.Name.ToString();
        var blockScoped = root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        return blockScoped?.Name.ToString();
    }

    /// <summary>`RootNamespace` + every folder segment, joined with `.` — the namespace the file WOULD
    /// declare if it followed the project's folder convention exactly.</summary>
    static string ImpliedNamespace(string relativePath, string rootNamespace)
    {
        var normalized = relativePath.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash < 0) return rootNamespace; // a root file: no folder segment to add
        var folders = normalized[..lastSlash].Split('/');
        return rootNamespace + "." + string.Join('.', folders);
    }

    static string SimpleAttributeName(NameSyntax name) => name switch
    {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
        SimpleNameSyntax simple => simple.Identifier.Text,
        _ => name.ToString()
    };

    static int Line(SyntaxToken token) => token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
