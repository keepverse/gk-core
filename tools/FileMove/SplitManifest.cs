namespace FusionRpg.Tools.FileMove;

/// <summary>
/// `core-split-apply` A1 — the manifest model: what `gk-core/tests/core-test-projects.v1.json` declares, and
/// the rules checked before a single byte is written. The manifest is authored (the analyzer only
/// proposes it, `core-split-analyzer`'s own boundary: "the tool never decides groupings"); this type
/// is what a human-written or analyzer-drafted file deserializes into and validates against.
/// </summary>
/// <param name="SchemaVersion">Always <c>1</c> today.</param>
/// <param name="AnalyzerCommit">The sha the analyzer report this manifest follows was produced at.</param>
/// <param name="SharedDir">Where the shared files move once, in the first increment
/// (<c>gk-core/tests/FusionRpg.Core.Tests.Shared</c> — no `.csproj`, just linked sources + one `.props`).</param>
/// <param name="Shared">Glob patterns (relative to <c>gk-core/tests/FusionRpg.Core.Tests/</c>) every Core test
/// project needs and no project claims individually.</param>
/// <param name="Residual">The project name that keeps whatever no manifest project claims.</param>
/// <param name="Projects">One entry per new project, in apply order.</param>
public sealed record SplitManifest(
    long SchemaVersion,
    string AnalyzerCommit,
    string SharedDir,
    IReadOnlyList<string> Shared,
    string Residual,
    IReadOnlyList<SplitProject> Projects);

/// <param name="Name">Ends in <c>.Tests</c>; never contains <c>FusionRpg.Data</c> (the Core/Data
/// layering guard substring-scans project names, `InternalsVisibleTo.Fusion.cs`).</param>
/// <param name="Include">Glob patterns (relative to <c>gk-core/tests/FusionRpg.Core.Tests/</c>) claimed by
/// this project — each must match at least one file, and no file may be claimed twice.</param>
/// <param name="References">Repo-relative `.csproj` paths, a subset of what the residual project
/// references today. Never a test project — a split only divides dependencies the one project
/// already had.</param>
/// <param name="Links">Extra linked sources beyond the shared set, as REPO-RELATIVE paths anywhere in
/// the tree (e.g. `gk-fusion/src/FusionRpg.Injector/Hud/ActorHudCache.cs`, `gk-core/tests/FusionRpg.Data.Tests/DataTestStore.cs`)
/// — never assumed to live inside the residual. Named per project because not every project needs them.</param>
/// <param name="Content">`None` items this project needs: `fixtures/…`, `Goldens/…`.</param>
/// <param name="CoreInternals">Whether this project needs an `InternalsVisibleTo` grant from Core.</param>
public sealed record SplitProject(
    string Name,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> References,
    IReadOnlyList<string> Links,
    IReadOnlyList<string> Content,
    bool CoreInternals);

/// <summary>One rule violation, naming the project (or the manifest itself) and why — so a reviewer
/// can act on the message without re-deriving which rule failed.</summary>
public sealed record ManifestViolation(string? Project, string Reason);

public sealed record ManifestValidationResult(bool Ok, IReadOnlyList<ManifestViolation> Violations);

/// <summary>
/// `core-split-apply` A1 rules, checked before `SplitPlanner` ever runs. Every filesystem-shaped
/// question is an injected delegate, so the rules run in memory over a synthetic tree
/// (`core-split-apply` Testing: F2, F3, F4, F5, F11) — the same seam `FileMover` already uses for
/// `readText`/`enumerateFiles`.
/// </summary>
public static class SplitManifestValidator
{
    /// <param name="filesMatching">Given an include/shared glob (relative to the Core test project's
    /// own directory), the repo-relative files it matches. Injected so validation never touches disk.</param>
    /// <param name="referenceExists">Does this repo-relative `.csproj` path exist?</param>
    /// <param name="projectDirectoryExists">Does `tests/&lt;name&gt;` already exist?</param>
    /// <param name="residualReferences">The residual project's own `.csproj` references today — a
    /// split only divides what it already had; it never widens.</param>
    /// <param name="targetProject">The project THIS call is about to apply, when there is one. A1's
    /// "the project directory does not exist yet" and "every include matches at least one file" are
    /// preconditions of the increment being applied, not properties of the manifest as a whole: A5
    /// applies one project per increment in manifest order, so from the second increment on every
    /// earlier project's directory exists and its files have already left the residual. Read as global
    /// rules, they made the manifest invalid the moment the first increment was kept and refused every
    /// later increment (found live: `split --project <2> --apply` exited 1 with
    /// `gk-core/tests/FusionRpg.Core.AchievementTitlesTuningTests.Tests already exists`). Passing the target
    /// exempts every OTHER project whose directory already exists from those two rules; leaving it null
    /// keeps the whole-manifest reading the F11 cases assert.</param>
    public static ManifestValidationResult Validate(
        SplitManifest manifest,
        Func<string, IReadOnlyList<string>> filesMatching,
        Func<string, bool> referenceExists,
        Func<string, bool> projectDirectoryExists,
        IReadOnlyList<string> residualReferences,
        string? targetProject = null)
    {
        var violations = new List<ManifestViolation>();
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // file -> claiming project

        var sharedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in manifest.Shared)
            foreach (var file in filesMatching(pattern))
                sharedFiles.Add(file);

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in manifest.Projects)
        {
            // ---- name rules --------------------------------------------------------------------
            if (!project.Name.EndsWith(".Tests", StringComparison.Ordinal))
                violations.Add(new(project.Name, $"name '{project.Name}' does not end in '.Tests'"));

            if (project.Name.Contains("FusionRpg.Data", StringComparison.Ordinal))
                violations.Add(new(project.Name, $"name '{project.Name}' contains 'FusionRpg.Data' (Core/Data layering guard substring-scans project names)"));

            if (!seenNames.Add(project.Name))
                violations.Add(new(project.Name, $"project name '{project.Name}' is declared more than once"));

            // ---- directory rules ----------------------------------------------------------------
            var alreadyApplied = targetProject is not null
                && !string.Equals(project.Name, targetProject, StringComparison.Ordinal)
                && projectDirectoryExists(project.Name);

            if (project.Name.Contains('/') || project.Name.Contains('\\'))
                violations.Add(new(project.Name, $"'{project.Name}' is not a direct child of tests/ (contains a path separator)"));
            else if (!alreadyApplied && projectDirectoryExists(project.Name))
                violations.Add(new(project.Name, $"tests/{project.Name} already exists"));

            // ---- include rules ------------------------------------------------------------------
            var claimedByThis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in project.Include)
            {
                var matches = filesMatching(pattern);
                if (matches.Count == 0)
                {
                    // An applied project's files have left the residual by definition; a later project's
                    // empty pattern is still the authoring defect the rule exists for.
                    if (!alreadyApplied)
                        violations.Add(new(project.Name, $"include pattern '{pattern}' matches no file"));
                    continue;
                }

                foreach (var file in matches)
                {
                    claimedByThis.Add(file);

                    if (sharedFiles.Contains(file))
                        violations.Add(new(project.Name, $"'{file}' is claimed by '{project.Name}' but is also a shared file"));

                    if (claimed.TryGetValue(file, out var earlier))
                        violations.Add(new(project.Name, $"'{file}' is claimed by both '{earlier}' and '{project.Name}'"));
                    else
                        claimed[file] = project.Name;
                }
            }

            // ---- reference rules ----------------------------------------------------------------
            foreach (var reference in project.References)
            {
                if (!referenceExists(reference))
                {
                    violations.Add(new(project.Name, $"references '{reference}', which does not exist"));
                    continue;
                }

                var underSrcOrTools = reference.StartsWith("src/", StringComparison.Ordinal)
                    || reference.StartsWith("tools/", StringComparison.Ordinal);
                if (!underSrcOrTools)
                    violations.Add(new(project.Name, $"references '{reference}', which is not under src/ or tools/"));

                if (reference.EndsWith(".Tests.csproj", StringComparison.Ordinal))
                    violations.Add(new(project.Name, $"references '{reference}', which is a test project (a split never adds test-to-test references)"));

                if (!residualReferences.Contains(reference, StringComparer.OrdinalIgnoreCase))
                    violations.Add(new(project.Name, $"references '{reference}', which the residual project does not reference today (a split never widens dependencies)"));
            }
        }

        return new ManifestValidationResult(violations.Count == 0, violations);
    }

    /// <summary>The one glob shape this program's manifests ever use: a top-level (or nested) folder
    /// prefix ending in <c>/**</c>, or a bare file name. No general glob library — the analyzer's own
    /// candidate model is "each top-level folder is one candidate; each root file is its own candidate"
    /// (`core-split-analyzer`), so nothing richer is ever needed.</summary>
    public static bool MatchesPattern(string repoRelativeFile, string pattern)
    {
        if (pattern.EndsWith("/**", StringComparison.Ordinal))
        {
            var prefix = pattern[..^"/**".Length];
            return repoRelativeFile.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(repoRelativeFile, prefix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(repoRelativeFile, pattern, StringComparison.OrdinalIgnoreCase);
    }
}
