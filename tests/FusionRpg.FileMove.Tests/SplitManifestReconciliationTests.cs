using System.Text.Json;
using FusionRpg.Tools.FileMove;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.FileMove.Tests;

/// <summary>
/// A1 declares the split's dependency policy: a project's <c>include</c> patterns are relative to
/// <c>gk-core/tests/FusionRpg.Core.Tests/</c> (A1's own example is <c>["World/**"]</c>), so a file under the
/// residual that a pattern matches is claimed by that project and belongs in <c>tests/&lt;project&gt;/</c>.
/// Nothing checked that on disk: the patterns are validated only when an increment is applied, and A1
/// then requires the target directory <b>not</b> to exist, so a file added to a claimed residual folder
/// after its increment can never be moved by the tool.
///
/// This suite is the reconciliation. It walks the real manifest and the real residual, and asserts the
/// divergences are exactly the ones recorded below — each with the commit that added it. A NEW
/// divergence fails here; a repaired one makes its entry stale and fails too, so the register cannot
/// rot silently (the <c>knownRed</c> self-expiry shape).
///
/// It also carries the split's <b>completion contract</b>: every manifest project applied and the shared
/// set moved out of the residual. No other suite asserts it — <c>CoreTestProjectPolicyTests</c> W1–W6 each
/// skip a project whose directory does not exist, so "the increment stream is drained" was a hand reading.
/// </summary>
public class SplitManifestReconciliationTests
{
    /// <summary>
    /// Four Stats-area test files added to the residual <b>after</b> increment 60/68 moved
    /// <c>Stats/**</c> into <c>gk-core/tests/FusionRpg.Core.Stats.Tests/</c> (<c>cb0f048fb</c>). The manifest's
    /// <c>Stats/**</c> still claims them and the tool cannot move them: A1 refuses a target whose
    /// directory exists (<c>gk-core/tests/FusionRpg.Core.Stats.Tests already exists</c>), and
    /// <see cref="SplitManifestTests.The_target_project_directory_must_still_not_exist"/> pins that
    /// rule as a contract. Repairing them therefore needs an erratum on A1 — filed as <c>TVB-F30</c>.
    /// These entries are a debt register, not a blessing: the test below is what stops a fifth one
    /// landing unnoticed.
    /// </summary>
    static readonly (string File, string AddedBy)[] RecordedDivergences =
    {
        ("Stats/ActorLivenessRevisionTests.cs", "6eb250bc4 lawn LW1.5"),
        ("Stats/ActorLivenessRevisionsTests.cs", "fcfc39789 lawn LW1.6"),
        ("Stats/ResourceRegenUnitTests.cs", "423579089 lawn LW2.1"),
        ("Stats/TurnChannelDeclarationTests.cs", "18139aec6 battle T17"),
    };

    [Fact]
    public void Every_residual_file_a_manifest_include_pattern_claims_is_recorded()
    {
        var manifest = ReadManifest(out var residualDir);

        var residualFiles = Directory
            .EnumerateFiles(residualDir, "*.cs", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(residualDir, p).Replace('\\', '/'))
            .Where(IsSource)
            .ToList();

        var patterns = manifest.Projects.SelectMany(p => p.Include).ToList();
        var divergences = residualFiles
            .Where(f => patterns.Any(pattern => SplitManifestValidator.MatchesPattern(f, pattern)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var recorded = RecordedDivergences
            .Select(d => d.File)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(recorded, divergences);
    }

    [Fact]
    public void The_recorded_divergences_are_still_claimed_by_the_Stats_project()
    {
        var manifest = ReadManifest(out _);

        // If `Stats/**` were ever narrowed to an explicit list, the test above would be enforcing
        // nothing and these four would silently become ordinary residual files.
        var claimants = manifest.Projects
            .Where(p => p.Include.Any(pattern =>
                SplitManifestValidator.MatchesPattern(RecordedDivergences[0].File, pattern)))
            .Select(p => p.Name)
            .ToList();

        Assert.Equal(new[] { "FusionRpg.Core.Stats.Tests" }, claimants);
    }

    [Fact]
    public void Every_manifest_project_is_applied_and_the_shared_set_has_left_the_residual()
    {
        var manifest = ReadManifest(out var residualDir);
        var root = RepoRoot();
        var unapplied = new List<string>();

        // A5: "the split is done when every project in the approved manifest is applied". This is the
        // only place that asserts it: `CoreTestProjectPolicyTests` W1-W6 each skip a project whose
        // directory does not exist yet, so before this test the completion claim was a hand reading.
        // The count is read from the manifest, never pinned -- adding a project here without applying
        // it is exactly the red this is for.
        foreach (var project in manifest.Projects)
        {
            if (!Directory.Exists(Path.Combine(root, "tests", project.Name)))
            {
                unapplied.Add($"tests/{project.Name}");
            }
        }

        // The shared set moves once, into `SharedDir`, and must not be left behind in the residual:
        // a copy in both places is the same divergence seen from the other side.
        var sharedDir = Path.Combine(root, manifest.SharedDir.Replace('/', Path.DirectorySeparatorChar));
        foreach (var pattern in manifest.Shared)
        {
            var inShared = Directory.Exists(sharedDir)
                && Directory.EnumerateFiles(sharedDir, "*", SearchOption.AllDirectories)
                    .Any(p => SplitManifestValidator.MatchesPattern(
                        Path.GetRelativePath(sharedDir, p).Replace('\\', '/'), pattern));
            if (!inShared)
            {
                unapplied.Add($"{manifest.SharedDir}/{pattern} (shared pattern matches nothing)");
            }

            var leftInResidual = Directory.EnumerateFiles(residualDir, "*", SearchOption.AllDirectories)
                .Any(p => SplitManifestValidator.MatchesPattern(
                    Path.GetRelativePath(residualDir, p).Replace('\\', '/'), pattern));
            if (leftInResidual)
            {
                unapplied.Add($"tests/{manifest.Residual}/{pattern} (a shared entry must not remain in the residual)");
            }
        }

        Assert.True(unapplied.Count == 0,
            "manifest entries not applied (A5: the split is done when every project in the approved manifest "
            + "is applied; whatever the manifest leaves in the residual stays there) — "
            + string.Join(", ", unapplied));
    }

    [Fact]
    public void Every_manifest_projects_declared_content_resolves_in_its_own_directory()
    {
        var manifest = ReadManifest(out _);
        var root = RepoRoot();
        var unresolved = new List<string>();

        foreach (var project in manifest.Projects)
        {
            var projectDir = Path.Combine(root, "tests", project.Name);
            var files = Directory.Exists(projectDir)
                ? Directory.EnumerateFiles(projectDir, "*", SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(projectDir, p).Replace('\\', '/'))
                    .Where(IsSource)
                    .ToList()
                : new List<string>();

            // A1's own rule -- "each [include pattern] must match at least one file" -- carried to the
            // post-apply state. A1 validates it against the residual, and after the increment the residual
            // no longer holds those files, so nothing checked the applied form until here.
            foreach (var pattern in project.Include)
            {
                if (!files.Any(f => SplitManifestValidator.MatchesPattern(f, pattern)))
                {
                    unresolved.Add($"{project.Name}: include '{pattern}' matches nothing under tests/{project.Name}/");
                }
            }

            // `links` are REPO-relative (SplitProject's own contract); a missing one breaks the build.
            foreach (var link in project.Links)
            {
                if (!File.Exists(Path.Combine(root, link.Replace('/', Path.DirectorySeparatorChar))))
                {
                    unresolved.Add($"{project.Name}: link '{link}' does not exist");
                }
            }

            // `content` are MSBuild globs relative to the PROJECT directory, and a missing one fails at
            // run time rather than build time -- so no build witnesses it. Checked as its directory prefix
            // holding at least one file, not by re-implementing MSBuild's glob engine.
            foreach (var content in project.Content)
            {
                var normalized = content.Replace('\\', '/');
                var prefix = normalized.Split('*')[0].TrimEnd('/');
                if (prefix.Length == 0)
                {
                    continue;
                }

                var target = Path.GetFullPath(Path.Combine(projectDir, prefix));
                var any = Directory.Exists(target)
                    && Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Any();
                if (!any)
                {
                    unresolved.Add($"{project.Name}: content '{content}' resolves to no file under '{prefix}'");
                }
            }
        }

        Assert.True(unresolved.Count == 0,
            "manifest declarations that resolve to nothing: " + string.Join("; ", unresolved));
    }

    static bool IsSource(string relative)
    {
        var segments = relative.Split('/');
        return !segments.Any(s => s is "bin" or "obj" or "TestResults");
    }

    static SplitManifest ReadManifest(out string residualDir)
    {
        var root = RepoRoot();
        var manifestPath = Path.Combine(root, "tests", "core-test-projects.v1.json");
        var manifest = JsonSerializer.Deserialize<SplitManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(manifest);
        residualDir = Path.Combine(root, "tests", manifest!.Residual);
        return manifest;
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
