using FusionRpg.Tools.FileMove;
using Xunit;

namespace FusionRpg.FileMove.Tests;

/// <summary>
/// `core-split-apply` A1 — the manifest model's own validation rules (F2, F3, F4, F5, F11), run
/// entirely in memory over synthetic file lists and reference sets. `SplitPlanner` (F1, F7, F8) and
/// `SplitExecutor` (F6, F9, F10) build on this and are tested separately.
/// </summary>
public class SplitManifestTests
{
    const string CoreCsproj = "src/FusionRpg.Core/FusionRpg.Core.csproj";
    static readonly string[] ResidualRefsToday = { CoreCsproj, "tools/SomeTool/SomeTool.csproj" };

    static Func<string, IReadOnlyList<string>> FilesMatching(params string[] allFiles) =>
        pattern => allFiles.Where(f => SplitManifestValidator.MatchesPattern(f, pattern)).ToList();

    static SplitProject Project(string name, string[] include, string[]? references = null, bool coreInternals = true) =>
        new(name, include, references ?? new[] { CoreCsproj }, Array.Empty<string>(), Array.Empty<string>(), coreInternals);

    static ManifestValidationResult Validate(
        SplitManifest manifest,
        Func<string, IReadOnlyList<string>> filesMatching,
        Func<string, bool>? referenceExists = null,
        Func<string, bool>? projectDirectoryExists = null,
        IReadOnlyList<string>? residualReferences = null,
        string? targetProject = null) =>
        SplitManifestValidator.Validate(
            manifest,
            filesMatching,
            referenceExists ?? (_ => true),
            projectDirectoryExists ?? (_ => false),
            residualReferences ?? ResidualRefsToday,
            targetProject);

    [Fact]
    public void A_well_formed_manifest_validates_ok()
    {
        var manifest = new SplitManifest(1, "abc123", "tests/FusionRpg.Core.Tests.Shared",
            new[] { "TestSupport/**", "AssemblyInfo.cs" }, "FusionRpg.Core.Tests",
            new[] { Project("FusionRpg.Core.World.Tests", new[] { "World/**" }) });

        var result = Validate(manifest,
            FilesMatching("World/Thing.cs", "World/Other.cs", "TestSupport/Helper.cs", "AssemblyInfo.cs", "Battle/X.cs"));

        Assert.True(result.Ok, string.Join("; ", result.Violations.Select(v => v.Reason)));
    }

    // ---- F2: a file claimed twice -------------------------------------------------------------

    [Fact]
    public void F2_a_file_claimed_by_two_projects_is_refused()
    {
        var manifest = new SplitManifest(1, "abc123", "tests/FusionRpg.Core.Tests.Shared",
            Array.Empty<string>(), "FusionRpg.Core.Tests",
            new[]
            {
                Project("FusionRpg.Core.A.Tests", new[] { "World/**" }),
                Project("FusionRpg.Core.B.Tests", new[] { "World/**" }),
            });

        var result = Validate(manifest, FilesMatching("World/Thing.cs"));

        Assert.False(result.Ok);
        Assert.Contains(result.Violations, v => v.Reason.Contains("claimed by both", StringComparison.Ordinal));
    }

    [Fact]
    public void F2_a_shared_file_also_claimed_by_a_project_is_refused()
    {
        var manifest = new SplitManifest(1, "abc123", "tests/FusionRpg.Core.Tests.Shared",
            new[] { "AssemblyInfo.cs" }, "FusionRpg.Core.Tests",
            new[] { Project("FusionRpg.Core.World.Tests", new[] { "AssemblyInfo.cs" }) });

        var result = Validate(manifest, FilesMatching("AssemblyInfo.cs"));

        Assert.False(result.Ok);
        Assert.Contains(result.Violations, v => v.Reason.Contains("also a shared file", StringComparison.Ordinal));
    }

    // ---- F3: references names a .Tests.csproj -------------------------------------------------

    [Fact]
    public void F3_a_reference_naming_a_test_project_is_refused()
    {
        var manifest = new SplitManifest(1, "abc123", "tests/FusionRpg.Core.Tests.Shared",
            Array.Empty<string>(), "FusionRpg.Core.Tests",
            new[]
            {
                Project("FusionRpg.Core.World.Tests", new[] { "World/**" },
                    references: new[] { "tests/FusionRpg.Other.Tests/FusionRpg.Other.Tests.csproj" }),
            });

        var result = Validate(manifest, FilesMatching("World/Thing.cs"),
            referenceExists: _ => true);

        Assert.False(result.Ok);
        Assert.Contains(result.Violations, v => v.Reason.Contains("is a test project", StringComparison.Ordinal));
    }

    // ---- F4: project name rules ------------------------------------------------------------------

    [Fact]
    public void F4_a_name_containing_FusionRpg_Data_is_refused()
    {
        var manifest = new SplitManifest(1, "abc123", "tests/FusionRpg.Core.Tests.Shared",
            Array.Empty<string>(), "FusionRpg.Core.Tests",
            new[] { Project("FusionRpg.Data.World.Tests", new[] { "World/**" }) });

        var result = Validate(manifest, FilesMatching("World/Thing.cs"));

        Assert.False(result.Ok);
        Assert.Contains(result.Violations, v => v.Reason.Contains("FusionRpg.Data", StringComparison.Ordinal));
    }

    [Fact]
    public void F4_a_name_not_ending_in_Tests_is_refused()
    {
        var manifest = new SplitManifest(1, "abc123", "tests/FusionRpg.Core.Tests.Shared",
            Array.Empty<string>(), "FusionRpg.Core.Tests",
            new[] { Project("FusionRpg.Core.World", new[] { "World/**" }) });

        var result = Validate(manifest, FilesMatching("World/Thing.cs"));

        Assert.False(result.Ok);
        Assert.Contains(result.Violations, v => v.Reason.Contains("does not end in '.Tests'", StringComparison.Ordinal));
    }

    // ---- F5: references a src/ project the residual does not reference today ----------------------

    [Fact]
    public void F5_a_reference_the_residual_does_not_have_today_is_refused()
    {
        var manifest = new SplitManifest(1, "abc123", "tests/FusionRpg.Core.Tests.Shared",
            Array.Empty<string>(), "FusionRpg.Core.Tests",
            new[]
            {
                Project("FusionRpg.Core.World.Tests", new[] { "World/**" },
                    references: new[] { "src/FusionRpg.NeverReferenced/FusionRpg.NeverReferenced.csproj" }),
            });

        var result = Validate(manifest, FilesMatching("World/Thing.cs"),
            referenceExists: _ => true,
            residualReferences: ResidualRefsToday); // does not include NeverReferenced

        Assert.False(result.Ok);
        Assert.Contains(result.Violations, v => v.Reason.Contains("does not reference today", StringComparison.Ordinal));
    }

    // ---- F11: project directory already exists / not a direct child of tests/ ---------------------

    [Fact]
    public void F11_a_project_directory_that_already_exists_is_refused()
    {
        var manifest = new SplitManifest(1, "abc123", "tests/FusionRpg.Core.Tests.Shared",
            Array.Empty<string>(), "FusionRpg.Core.Tests",
            new[] { Project("FusionRpg.Core.World.Tests", new[] { "World/**" }) });

        var result = Validate(manifest, FilesMatching("World/Thing.cs"),
            projectDirectoryExists: name => name == "FusionRpg.Core.World.Tests");

        Assert.False(result.Ok);
        Assert.Contains(result.Violations, v => v.Reason.Contains("already exists", StringComparison.Ordinal));
    }

    // ---- A5: a PARTIALLY applied manifest still validates for the next increment ------------------

    [Fact]
    public void An_already_applied_project_no_longer_blocks_the_next_increment()
    {
        // A5 applies one manifest project per increment. From increment 2 on, project 1's directory
        // exists and its files have left the residual, so a whole-manifest reading of A1's "does not
        // exist yet" / "matches at least one file" refused EVERY later increment (found live: the
        // second real increment exited 1 with "...Tests already exists").
        var manifest = new SplitManifest(1, "abc123", "tests/FusionRpg.Core.Tests.Shared",
            Array.Empty<string>(), "FusionRpg.Core.Tests",
            new[]
            {
                Project("FusionRpg.Core.A.Tests", new[] { "World/**" }),
                Project("FusionRpg.Core.B.Tests", new[] { "Battle/**" }),
            });

        var result = Validate(manifest,
            FilesMatching("Battle/X.cs"), // World/** no longer matches anything: A already applied
            projectDirectoryExists: name => name == "FusionRpg.Core.A.Tests",
            targetProject: "FusionRpg.Core.B.Tests");

        Assert.True(result.Ok, string.Join("; ", result.Violations.Select(v => v.Reason)));
    }

    [Fact]
    public void The_target_project_directory_must_still_not_exist()
    {
        var manifest = new SplitManifest(1, "abc123", "tests/FusionRpg.Core.Tests.Shared",
            Array.Empty<string>(), "FusionRpg.Core.Tests",
            new[]
            {
                Project("FusionRpg.Core.A.Tests", new[] { "World/**" }),
                Project("FusionRpg.Core.B.Tests", new[] { "Battle/**" }),
            });

        var result = Validate(manifest,
            FilesMatching("World/Thing.cs", "Battle/X.cs"),
            projectDirectoryExists: name => name == "FusionRpg.Core.B.Tests",
            targetProject: "FusionRpg.Core.B.Tests");

        Assert.False(result.Ok);
        Assert.Contains(result.Violations, v => v.Reason.Contains("already exists", StringComparison.Ordinal));
    }

    [Fact]
    public void A_later_projects_empty_include_is_still_refused()
    {
        // Only an APPLIED project is exempt from "matches at least one file"; a later project with a
        // pattern that matches nothing is still the authoring defect that rule exists for.
        var manifest = new SplitManifest(1, "abc123", "tests/FusionRpg.Core.Tests.Shared",
            Array.Empty<string>(), "FusionRpg.Core.Tests",
            new[]
            {
                Project("FusionRpg.Core.A.Tests", new[] { "World/**" }),
                Project("FusionRpg.Core.Ghost.Tests", new[] { "Ghost/**" }),
            });

        var result = Validate(manifest,
            FilesMatching(),
            projectDirectoryExists: name => name == "FusionRpg.Core.A.Tests",
            targetProject: "FusionRpg.Core.Ghost.Tests");

        Assert.False(result.Ok);
        Assert.Contains(result.Violations, v => v.Reason.Contains("matches no file", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Violations, v => v.Reason.Contains("already exists", StringComparison.Ordinal));
    }

    [Fact]
    public void F11_a_project_name_that_is_not_a_direct_child_of_tests_is_refused()
    {
        var manifest = new SplitManifest(1, "abc123", "tests/FusionRpg.Core.Tests.Shared",
            Array.Empty<string>(), "FusionRpg.Core.Tests",
            new[] { Project("Sub/FusionRpg.Core.World.Tests", new[] { "World/**" }) });

        var result = Validate(manifest, FilesMatching("World/Thing.cs"));

        Assert.False(result.Ok);
        Assert.Contains(result.Violations, v => v.Reason.Contains("not a direct child of tests/", StringComparison.Ordinal));
    }

    // ---- an include pattern matching nothing is also refused (part of A1, exercised alongside F2-F11) --

    [Fact]
    public void An_include_pattern_matching_no_file_is_refused()
    {
        var manifest = new SplitManifest(1, "abc123", "tests/FusionRpg.Core.Tests.Shared",
            Array.Empty<string>(), "FusionRpg.Core.Tests",
            new[] { Project("FusionRpg.Core.Ghost.Tests", new[] { "Ghost/**" }) });

        var result = Validate(manifest, FilesMatching("World/Thing.cs"));

        Assert.False(result.Ok);
        Assert.Contains(result.Violations, v => v.Reason.Contains("matches no file", StringComparison.Ordinal));
    }

    [Fact]
    public void A_reference_that_does_not_exist_is_refused()
    {
        var manifest = new SplitManifest(1, "abc123", "tests/FusionRpg.Core.Tests.Shared",
            Array.Empty<string>(), "FusionRpg.Core.Tests",
            new[]
            {
                Project("FusionRpg.Core.World.Tests", new[] { "World/**" },
                    references: new[] { "src/FusionRpg.Ghost/FusionRpg.Ghost.csproj" }),
            });

        var result = Validate(manifest, FilesMatching("World/Thing.cs"), referenceExists: _ => false);

        Assert.False(result.Ok);
        Assert.Contains(result.Violations, v => v.Reason.Contains("does not exist", StringComparison.Ordinal));
    }
}
