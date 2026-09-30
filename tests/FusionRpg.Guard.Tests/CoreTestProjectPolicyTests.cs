using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// `core-split-wiring` "New guard tests" (W1–W6) — reads `gk-core/tests/core-test-projects.v1.json`, the
/// declared dependency policy for the Core test split (`core-split-apply` A1), and checks it against
/// the REAL repo tree: membership, subset and set-equality only, never a population count.
///
/// <para>Every case below is scoped to what <b>physically exists on disk today</b>. Before
/// `core-split-apply`'s first real increment (`TVB5.7`) the only Core test project on disk is the
/// residual, `FusionRpg.Core.Tests` — so W1, W2, W4 and (for the 67 not-yet-split projects) W5 pass
/// vacuously. That is not a weaker test: as each future increment moves a manifest project from
/// declared to real, these same assertions start checking it for real, with no test edit required.</para>
/// </summary>
public class CoreTestProjectPolicyTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static JsonElement Manifest()
    {
        var path = Path.Combine(RepoRoot(), "tests", "core-test-projects.v1.json");
        Assert.True(File.Exists(path), "missing " + path);
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    static IEnumerable<JsonElement> ManifestProjects(JsonElement manifest) => manifest.GetProperty("projects").EnumerateArray();

    static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    static string ReadWorkflow(string name)
    {
        var path = Path.Combine(RepoRoot(), ".github", "workflows", name);
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    /// <summary>A line whose TRIMMED text starts with `dotnet test &lt;path&gt;` — a YAML comment
    /// naming the path does not count (the same rule `CiWiringGuardTests` already applies).</summary>
    static bool IsWiredWithDotnetTest(string workflowText, string relativeCsprojPath) =>
        workflowText.Replace("\r\n", "\n").Split('\n')
            .Any(line => line.TrimStart().StartsWith("dotnet test " + relativeCsprojPath, StringComparison.Ordinal));

    // ---- W1: every Core test csproj on disk is either a manifest project or the residual --------

    [Fact]
    public void W1_every_core_test_csproj_on_disk_is_a_manifest_project_or_the_residual()
    {
        var manifest = Manifest();
        var residual = manifest.GetProperty("residual").GetString()!;
        var manifestNames = ManifestProjects(manifest)
            .Select(p => p.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        var testsDir = Path.Combine(RepoRoot(), "tests");
        var unrecognized = GuardWiring.SafeFiles(testsDir, "FusionRpg.Core.*.csproj")
            .Where(csproj => !IsBuildOutput(csproj))
            .Select(csproj => Path.GetFileNameWithoutExtension(csproj))
            .Where(name => name != residual && !manifestNames.Contains(name))
            .ToList();

        Assert.True(unrecognized.Count == 0,
            "Core test csproj(s) on disk that are neither the residual nor a manifest project: "
            + string.Join(", ", unrecognized));
    }

    // ---- W2: each existing manifest project's ProjectReferences <= its manifest `references` -----

    [Fact]
    public void W2_each_existing_manifest_projects_references_are_a_subset_of_its_manifest_entry()
    {
        var manifest = Manifest();
        var repoRoot = RepoRoot();
        var violations = new List<string>();

        foreach (var project in ManifestProjects(manifest))
        {
            var name = project.GetProperty("name").GetString()!;
            var csprojPath = Path.Combine(repoRoot, "tests", name, name + ".csproj");
            if (!File.Exists(csprojPath)) continue; // not split off yet -- nothing to check

            var declared = project.GetProperty("references").EnumerateArray()
                .Select(r => r.GetString()!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in Regex.Matches(File.ReadAllText(csprojPath),
                         @"<ProjectReference\s+Include\s*=\s*""(?<path>[^""]+)"""))
            {
                var raw = m.Groups["path"].Value.Replace('\\', '/');
                var normalized = raw.StartsWith("../../", StringComparison.Ordinal) ? raw["../../".Length..] : raw;
                if (!declared.Contains(normalized))
                    violations.Add($"{name} references '{normalized}', not declared in its manifest entry");
            }
        }

        Assert.True(violations.Count == 0, string.Join("; ", violations));
    }

    // ---- W3: no *.Tests.csproj anywhere under tests/ or tools/ references another one -----------

    [Fact]
    public void W3_no_test_project_references_another_test_project()
    {
        var repoRoot = RepoRoot();
        var testCsprojFiles = GuardWiring.SafeFiles(Path.Combine(repoRoot, "tests"), "*.Tests.csproj")
            .Concat(GuardWiring.SafeFiles(Path.Combine(repoRoot, "tools"), "*.Tests.csproj"))
            .Where(csproj => !IsBuildOutput(csproj));

        var violations = new List<string>();
        foreach (var csproj in testCsprojFiles)
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(csproj),
                         @"<ProjectReference\s+Include\s*=\s*""(?<path>[^""]+)"""))
            {
                if (m.Groups["path"].Value.EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase))
                    violations.Add($"{Path.GetFileName(csproj)} references test project {m.Groups["path"].Value}");
            }
        }

        Assert.True(violations.Count == 0, string.Join("; ", violations));
    }

    // ---- W4: an InternalsVisibleTo line exists for a project iff its manifest coreInternals is true

    [Fact]
    public void W4_internals_visible_to_lines_match_coreinternals_for_projects_that_exist()
    {
        var manifest = Manifest();
        var repoRoot = RepoRoot();
        var attributeFile = Path.Combine(repoRoot, "src", "FusionRpg.Core", "InternalsVisibleTo.CoreTests.cs");
        var attributeText = File.Exists(attributeFile) ? File.ReadAllText(attributeFile) : "";

        var violations = new List<string>();
        foreach (var project in ManifestProjects(manifest))
        {
            var name = project.GetProperty("name").GetString()!;
            var csprojPath = Path.Combine(repoRoot, "tests", name, name + ".csproj");
            if (!File.Exists(csprojPath)) continue; // not split off yet -- nothing to check

            var wantsInternals = project.GetProperty("coreInternals").GetBoolean();
            var hasLine = attributeText.Contains($"InternalsVisibleTo(\"{name}\")", StringComparison.Ordinal);
            if (wantsInternals != hasLine)
                violations.Add($"{name}: coreInternals={wantsInternals} but InternalsVisibleTo present={hasLine}");
        }

        Assert.True(violations.Count == 0, string.Join("; ", violations));
    }

    // ---- W5: every existing Core test csproj is wired into BOTH ci.yml and release.yml ------------

    [Trait("VerificationId", "guard.workflows")]
    [Fact]
    public void W5_every_existing_core_test_project_is_wired_in_ci_and_release()
    {
        var manifest = Manifest();
        var repoRoot = RepoRoot();
        var residual = manifest.GetProperty("residual").GetString()!;

        var names = new List<string> { residual };
        names.AddRange(ManifestProjects(manifest).Select(p => p.GetProperty("name").GetString()!));

        var ci = ReadWorkflow("ci.yml");
        var release = ReadWorkflow("release.yml");

        var missing = new List<string>();
        foreach (var name in names)
        {
            if (!File.Exists(Path.Combine(repoRoot, "tests", name, name + ".csproj"))) continue;

            var path = $"tests/{name}/{name}.csproj";
            if (!IsWiredWithDotnetTest(ci, path)) missing.Add($"ci.yml: {path}");
            if (!IsWiredWithDotnetTest(release, path)) missing.Add($"release.yml: {path}");
        }

        Assert.True(missing.Count == 0, string.Join(", ", missing));
    }

    // ---- W6: the BalanceGuard trait set equals the set of ci.yml --filter lines --------------------

    [Trait("VerificationId", "guard.workflows")]
    [Fact]
    public void W6_balanceguard_trait_projects_equal_ci_yml_filter_lines()
    {
        var manifest = Manifest();
        var repoRoot = RepoRoot();
        var residual = manifest.GetProperty("residual").GetString()!;

        var names = new List<string> { residual };
        names.AddRange(ManifestProjects(manifest).Select(p => p.GetProperty("name").GetString()!));

        var withTrait = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var dir = Path.Combine(repoRoot, "tests", name);
            if (!Directory.Exists(dir)) continue;
            var hasTrait = GuardWiring.SafeFiles(dir, "*.cs")
                .Any(f => File.ReadAllText(f).Contains("[Trait(\"Category\", \"BalanceGuard\")]", StringComparison.Ordinal));
            if (hasTrait) withTrait.Add(name);
        }

        var ci = ReadWorkflow("ci.yml");
        var wiredForFilter = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in ci.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("dotnet test ", StringComparison.Ordinal)) continue;
            if (!trimmed.Contains("--filter \"Category=BalanceGuard\"", StringComparison.Ordinal)) continue;

            foreach (var name in names)
                if (trimmed.Contains($"tests/{name}/{name}.csproj", StringComparison.Ordinal))
                    wiredForFilter.Add(name);
        }

        var traitWithoutFilterLine = withTrait.Except(wiredForFilter).ToList();
        var filterLineWithoutTrait = wiredForFilter.Except(withTrait).ToList();

        Assert.True(traitWithoutFilterLine.Count == 0 && filterLineWithoutTrait.Count == 0,
            $"BalanceGuard trait with no matching ci.yml filter line: [{string.Join(",", traitWithoutFilterLine)}]; "
            + $"ci.yml filter line with no project holding the trait: [{string.Join(",", filterLineWithoutTrait)}]");
    }
}
