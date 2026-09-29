using System.Text;
using FusionRpg.Tools.FileMove;
using Xunit;

namespace FusionRpg.FileMove.Tests;

/// <summary>
/// `core-split-apply` A2–A4 — <see cref="SplitPlanner"/> planning, run entirely in memory over a
/// synthetic file tree via injected delegates (F1, F7, F8). `SplitManifestTests` covers the manifest's
/// own A1 rules (F2–F5, F11); `SplitExecutorTests` covers real disk (F6, F9, F10).
/// </summary>
public class SplitPlannerTests
{
    const string CoreCsproj = "src/FusionRpg.Core/FusionRpg.Core.csproj";
    const string ResidualCsprojPath = "tests/FusionRpg.Core.Tests/FusionRpg.Core.Tests.csproj";
    const string SlnxPath = "FusionRpg.slnx";

    static readonly byte[] ResidualCsprojBytes = Encoding.UTF8.GetBytes(
        "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n"
        + "  <ItemGroup>\r\n"
        + "    <PackageReference Include=\"xunit\" Version=\"2.6.0\" />\r\n"
        + "    <Compile Include=\"TestSupport\\Helper.cs\" />\r\n"
        + "  </ItemGroup>\r\n"
        + "</Project>\r\n");

    static readonly byte[] SlnxBytes = Encoding.UTF8.GetBytes(
        "<Solution>\r\n"
        + "  <Folder Name=\"/tests/\">\r\n"
        + "    <Project Path=\"tests/FusionRpg.Core.Tests/FusionRpg.Core.Tests.csproj\" />\r\n"
        + "  </Folder>\r\n"
        + "</Solution>\r\n");

    static SplitManifest Manifest(params SplitProject[] projects) => new(
        1, "abc123", "tests/FusionRpg.Core.Tests.Shared",
        new[] { "TestSupport/**" }, "FusionRpg.Core.Tests", projects);

    static SplitProject Project(string name, string[] include) =>
        new(name, include, new[] { CoreCsproj }, Array.Empty<string>(), Array.Empty<string>(), true);

    static SplitProject Project(string name, string[] include, string[] links, string[] content) =>
        new(name, include, new[] { CoreCsproj }, links, content, true);

    /// <summary>A residual tree with `World/Thing.cs`, `World/Other.cs` and `Battle/X.cs` — two folders,
    /// exactly as F1 asks for.</summary>
    static Func<string, IReadOnlyList<string>> TwoFolders() =>
        pattern => new[] { "World/Thing.cs", "World/Other.cs", "Battle/X.cs" }
            .Where(f => SplitManifestValidator.MatchesPattern(f, pattern))
            .ToList();

    static readonly byte[] CoreCsprojBytes = Encoding.UTF8.GetBytes(
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
        + "<RootNamespace>FusionRpg.Core</RootNamespace></PropertyGroup></Project>");

    static Func<string, byte[]> ReadBytes(IReadOnlyDictionary<string, byte[]>? extra = null) =>
        path =>
        {
            if (extra is not null && extra.TryGetValue(path, out var bytes)) return bytes;
            if (path == ResidualCsprojPath) return ResidualCsprojBytes;
            if (path == SlnxPath) return SlnxBytes;
            if (path == CoreCsproj) return CoreCsprojBytes;
            throw new FileNotFoundException(path);
        };

    static Func<string, bool> FileExists(params string[] existing)
    {
        var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase)
        {
            ResidualCsprojPath, SlnxPath,
        };
        return path => set.Contains(path);
    }

    static SplitPlan Plan(
        SplitManifest manifest,
        string projectName,
        Func<string, IReadOnlyList<string>>? filesMatching = null,
        Func<string, byte[]>? readBytes = null,
        Func<string, bool>? fileExists = null,
        Func<IReadOnlyList<string>>? dirtyPaths = null) =>
        SplitPlanner.Plan(
            manifest, projectName,
            filesMatching ?? TwoFolders(),
            readBytes ?? ReadBytes(),
            fileExists ?? FileExists(),
            dirtyPaths ?? (() => Array.Empty<string>()));

    // ---- F1: two folders, manifest claims one --------------------------------------------------

    [Fact]
    public void F1_the_plan_moves_exactly_the_claimed_folder_with_bytes_unchanged()
    {
        var manifest = Manifest(Project("FusionRpg.Core.World.Tests", new[] { "World/**" }));

        var plan = Plan(manifest, "FusionRpg.Core.World.Tests");

        Assert.False(plan.IsRefused, plan.Refusal);

        var moves = plan.Ops.Where(o => o.Kind == SplitOpKind.MoveFile).ToList();
        // The two World/** files move into the new project; Battle/X.cs (unclaimed) never appears.
        Assert.Contains(moves, m => m.From == "tests/FusionRpg.Core.Tests/World/Thing.cs"
            && m.Path == "tests/FusionRpg.Core.World.Tests/World/Thing.cs");
        Assert.Contains(moves, m => m.From == "tests/FusionRpg.Core.Tests/World/Other.cs"
            && m.Path == "tests/FusionRpg.Core.World.Tests/World/Other.cs");
        Assert.DoesNotContain(moves, m => m.From!.Contains("Battle", StringComparison.Ordinal));

        // A MoveFile op never carries a text copy of the source (bytes move as-is; see the record's
        // own doc comment) -- Before and After are both null for every move.
        foreach (var move in moves)
        {
            Assert.Null(move.Before);
            Assert.Null(move.After);
        }
    }

    [Fact]
    public void F1_the_plan_also_creates_the_project_directory_and_csproj()
    {
        var manifest = Manifest(Project("FusionRpg.Core.World.Tests", new[] { "World/**" }));

        var plan = Plan(manifest, "FusionRpg.Core.World.Tests");

        Assert.Contains(plan.Ops, o => o.Kind == SplitOpKind.CreateDirectory
            && o.Path == "tests/FusionRpg.Core.World.Tests");
        Assert.Contains(plan.Ops, o => o.Kind == SplitOpKind.CreateFile
            && o.Path == "tests/FusionRpg.Core.World.Tests/FusionRpg.Core.World.Tests.csproj");

        var csprojOp = plan.Ops.Single(o =>
            o.Path == "tests/FusionRpg.Core.World.Tests/FusionRpg.Core.World.Tests.csproj");
        var csprojText = Encoding.UTF8.GetString(csprojOp.After!);
        Assert.Contains("ProjectReference Include=\"..\\..\\src\\FusionRpg.Core\\FusionRpg.Core.csproj\"",
            csprojText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_is_included_from_its_real_repo_relative_location_not_the_residual()
    {
        // Regression: an earlier version of BuildProjectCsproj assumed every linked file lived inside
        // the residual folder, which is wrong for the two real cases this field exists for -- the three
        // Injector files (under src/) and DataTestStore.cs (under a sibling test project).
        var manifest = Manifest(Project("FusionRpg.Core.Hud.Tests", new[] { "Hud/**" },
            links: new[] { "src/FusionRpg.Injector/Hud/ActorHudCache.cs" },
            content: Array.Empty<string>()));

        var plan = Plan(manifest, "FusionRpg.Core.Hud.Tests",
            filesMatching: pattern => new[] { "Hud/ActorHudCacheTests.cs" }
                .Where(f => SplitManifestValidator.MatchesPattern(f, pattern)).ToList());

        Assert.False(plan.IsRefused, plan.Refusal);
        var csprojText = Encoding.UTF8.GetString(
            plan.Ops.Single(o => o.Path.EndsWith(".csproj", StringComparison.Ordinal)
                && o.Path.Contains("FusionRpg.Core.Hud.Tests", StringComparison.Ordinal)).After!);

        Assert.Contains(
            "<Compile Include=\"..\\..\\src\\FusionRpg.Injector\\Hud\\ActorHudCache.cs\" Link=\"Hud\\ActorHudCache.cs\" />",
            csprojText, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_outside_the_project_gets_a_link_but_project_relative_content_does_not()
    {
        var manifest = Manifest(Project("FusionRpg.Core.Atoms.Tests", new[] { "Atoms/**" },
            links: Array.Empty<string>(),
            content: new[] { "../fixtures/effects/**/*" }));

        var plan = Plan(manifest, "FusionRpg.Core.Atoms.Tests",
            filesMatching: pattern => new[] { "Atoms/Thing.cs" }
                .Where(f => SplitManifestValidator.MatchesPattern(f, pattern)).ToList());

        Assert.False(plan.IsRefused, plan.Refusal);
        var csprojText = Encoding.UTF8.GetString(
            plan.Ops.Single(o => o.Path.EndsWith(".csproj", StringComparison.Ordinal)
                && o.Path.Contains("FusionRpg.Core.Atoms.Tests", StringComparison.Ordinal)).After!);

        // Outside the project: needs a Link so CopyToOutputDirectory does not create a literal ".." folder.
        Assert.Contains(
            "<None Include=\"..\\fixtures\\effects\\**\\*\" Link=\"fixtures\\effects\\%(RecursiveDir)%(Filename)%(Extension)\" CopyToOutputDirectory=\"PreserveNewest\" />",
            csprojText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_increment_also_moves_the_shared_files_and_edits_the_residual_csproj()
    {
        var manifest = Manifest(Project("FusionRpg.Core.World.Tests", new[] { "World/**" }));

        var plan = Plan(manifest, "FusionRpg.Core.World.Tests");

        Assert.Contains(plan.Ops, o => o.Kind == SplitOpKind.CreateFile
            && o.Path == "tests/FusionRpg.Core.Tests.Shared/CoreTests.Shared.props");

        var residualEdit = plan.Ops.Single(o => o.Path == ResidualCsprojPath);
        Assert.Equal(SplitOpKind.ModifyFile, residualEdit.Kind);
        var residualAfter = Encoding.UTF8.GetString(residualEdit.After!);
        Assert.Contains("Import Project=\"..\\FusionRpg.Core.Tests.Shared\\CoreTests.Shared.props\"",
            residualAfter, StringComparison.Ordinal);
        // The PackageReference moved into the shared props; it must not remain (and duplicate) here.
        Assert.DoesNotContain("PackageReference", residualAfter, StringComparison.Ordinal);
    }

    [Fact]
    public void A_later_increment_does_not_recreate_the_shared_props_or_edit_the_residual_csproj_again()
    {
        var manifest = Manifest(Project("FusionRpg.Core.World.Tests", new[] { "World/**" }));
        var sharedPropsPath = "tests/FusionRpg.Core.Tests.Shared/CoreTests.Shared.props";

        var plan = Plan(manifest, "FusionRpg.Core.World.Tests",
            fileExists: FileExists(sharedPropsPath)); // shared props already exists -> not the first increment

        Assert.False(plan.IsRefused, plan.Refusal);
        Assert.DoesNotContain(plan.Ops, o => o.Path == sharedPropsPath);
        Assert.DoesNotContain(plan.Ops, o => o.Path == ResidualCsprojPath);
    }

    [Fact]
    public void The_splits_own_writes_keep_the_files_newline_and_leave_no_whitespace_only_line()
    {
        // Regression, found on the first real increment: removing a `PackageReference` element but not
        // its line left a whitespace-only line behind (`.editorconfig`: trim_trailing_whitespace), and
        // the import/slnx inserts hard-coded `\r\n` into files that were LF. Both are 68-increment
        // smells in the same two files, so both are asserted here rather than seen once.
        var manifest = Manifest(Project("FusionRpg.Core.World.Tests", new[] { "World/**" }));

        var plan = Plan(manifest, "FusionRpg.Core.World.Tests");

        var residualAfter = Encoding.UTF8.GetString(plan.Ops.Single(o => o.Path == ResidualCsprojPath).After!);
        // The fixture residual csproj is CRLF: the inserted import line must adopt CRLF too.
        Assert.Contains("<Project Sdk=\"Microsoft.NET.Sdk\">\r\n  <Import", residualAfter, StringComparison.Ordinal);
        foreach (var line in residualAfter.Replace("\r\n", "\n").Split('\n'))
            Assert.False(line.Length > 0 && line.All(char.IsWhiteSpace), "whitespace-only line: '" + line + "'");

        // The fixture slnx is CRLF with a two-space `</Folder>` indent: the new entry is one level
        // deeper, on its own line, in the file's own newline.
        var slnxAfter = Encoding.UTF8.GetString(plan.Ops.Single(o => o.Path == SlnxPath).After!);
        Assert.Contains(
            "\r\n    <Project Path=\"tests/FusionRpg.Core.World.Tests/FusionRpg.Core.World.Tests.csproj\" />\r\n  </Folder>",
            slnxAfter, StringComparison.Ordinal);
    }

    [Fact]
    public void A_paired_package_reference_moves_to_the_shared_props_with_its_children()
    {
        // The residual's own `coverlet.collector` and `xunit.runner.visualstudio` are PAIRED elements
        // (`IncludeAssets`/`PrivateAssets` children), not self-closing. Matching only the self-closing
        // shape left them behind, so the new project had no xunit test adapter, `dotnet test` printed
        // "No test is available" and exited 0, and the first real increment was kept with zero tests
        // run in the new project — the quiet failure this module exists to prevent.
        var csproj =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n"
            + "  <ItemGroup>\r\n"
            + "    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"2.8.2\">\r\n"
            + "      <IncludeAssets>runtime; build; native</IncludeAssets>\r\n"
            + "      <PrivateAssets>all</PrivateAssets>\r\n"
            + "    </PackageReference>\r\n"
            + "    <PackageReference Include=\"xunit\" Version=\"2.9.2\" />\r\n"
            + "  </ItemGroup>\r\n"
            + "</Project>\r\n";
        var manifest = Manifest(Project("FusionRpg.Core.World.Tests", new[] { "World/**" }));

        var plan = Plan(manifest, "FusionRpg.Core.World.Tests",
            readBytes: ReadBytes(new Dictionary<string, byte[]>
            {
                [ResidualCsprojPath] = Encoding.UTF8.GetBytes(csproj),
            }));

        Assert.False(plan.IsRefused, plan.Refusal);

        var propsText = Encoding.UTF8.GetString(
            plan.Ops.Single(o => o.Path.EndsWith("CoreTests.Shared.props", StringComparison.Ordinal)).After!);
        Assert.Contains("    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"2.8.2\">",
            propsText, StringComparison.Ordinal);
        Assert.Contains("      <PrivateAssets>all</PrivateAssets>", propsText, StringComparison.Ordinal);
        Assert.Contains("    </PackageReference>", propsText, StringComparison.Ordinal);
        Assert.Contains("    <PackageReference Include=\"xunit\" Version=\"2.9.2\" />",
            propsText, StringComparison.Ordinal);

        var residualAfter = Encoding.UTF8.GetString(plan.Ops.Single(o => o.Path == ResidualCsprojPath).After!);
        Assert.DoesNotContain("PackageReference", residualAfter, StringComparison.Ordinal);
        foreach (var line in residualAfter.Replace("\r\n", "\n").Split('\n'))
            Assert.False(line.Length > 0 && line.All(char.IsWhiteSpace), "whitespace-only line: '" + line + "'");
    }

    // ---- F7: a dirty path in the plan is refused ------------------------------------------------

    [Fact]
    public void F7_a_dirty_moved_file_refuses_the_plan()
    {
        var manifest = Manifest(Project("FusionRpg.Core.World.Tests", new[] { "World/**" }));

        var plan = Plan(manifest, "FusionRpg.Core.World.Tests",
            dirtyPaths: () => new[] { "tests/FusionRpg.Core.Tests/World/Thing.cs" });

        Assert.True(plan.IsRefused);
        Assert.Contains("World/Thing.cs", plan.Refusal!, StringComparison.Ordinal);
        Assert.Empty(plan.Ops);
    }

    [Fact]
    public void F7_a_dirty_residual_csproj_refuses_the_plan()
    {
        var manifest = Manifest(Project("FusionRpg.Core.World.Tests", new[] { "World/**" }));

        var plan = Plan(manifest, "FusionRpg.Core.World.Tests",
            dirtyPaths: () => new[] { ResidualCsprojPath });

        Assert.True(plan.IsRefused);
        Assert.Contains(ResidualCsprojPath, plan.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void F7_a_dirty_slnx_refuses_the_plan()
    {
        var manifest = Manifest(Project("FusionRpg.Core.World.Tests", new[] { "World/**" }));

        var plan = Plan(manifest, "FusionRpg.Core.World.Tests",
            dirtyPaths: () => new[] { SlnxPath });

        Assert.True(plan.IsRefused);
        Assert.Contains(SlnxPath, plan.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void F7_a_dirty_path_outside_the_plan_does_not_refuse_it()
    {
        var manifest = Manifest(Project("FusionRpg.Core.World.Tests", new[] { "World/**" }));

        var plan = Plan(manifest, "FusionRpg.Core.World.Tests",
            dirtyPaths: () => new[] { "docs/architecture/unrelated.md" });

        Assert.False(plan.IsRefused, plan.Refusal);
    }

    // ---- F8: two runs, same manifest -> identical plan ------------------------------------------

    [Fact]
    public void F8_two_runs_of_the_same_manifest_produce_an_identical_plan()
    {
        var manifest = Manifest(Project("FusionRpg.Core.World.Tests", new[] { "World/**" }));

        var first = Plan(manifest, "FusionRpg.Core.World.Tests");
        var second = Plan(manifest, "FusionRpg.Core.World.Tests");

        Assert.False(first.IsRefused, first.Refusal);
        Assert.Equal(first.Ops.Count, second.Ops.Count);
        for (var i = 0; i < first.Ops.Count; i++)
        {
            Assert.Equal(first.Ops[i].Kind, second.Ops[i].Kind);
            Assert.Equal(first.Ops[i].Path, second.Ops[i].Path);
            Assert.Equal(first.Ops[i].From, second.Ops[i].From);
            Assert.Equal(first.Ops[i].Before, second.Ops[i].Before);
            Assert.Equal(first.Ops[i].After, second.Ops[i].After);
        }
    }

    // ---- the defensive cycle check (A4 extension 1) actually runs -------------------------------

    [Fact]
    public void A_reference_whose_own_graph_already_reaches_the_residual_is_refused()
    {
        // Rigged on purpose: FusionRpg.Core.csproj here declares a ProjectReference back to the
        // residual test project, which real src/tools projects never do. This is the one shape that
        // proves the graph check in SplitPlanner.Plan is executed rather than a no-op comment -- the
        // spec is explicit that the real protection is the manifest's own reference rules and that this
        // check can never fire against a legitimate manifest.
        var riggedCoreCsproj = Encoding.UTF8.GetBytes(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
            + "<ProjectReference Include=\"..\\..\\tests\\FusionRpg.Core.Tests\\FusionRpg.Core.Tests.csproj\" />"
            + "</ItemGroup></Project>");

        var manifest = Manifest(Project("FusionRpg.Core.World.Tests", new[] { "World/**" }));

        var plan = Plan(manifest, "FusionRpg.Core.World.Tests",
            readBytes: ReadBytes(new Dictionary<string, byte[]> { [CoreCsproj] = riggedCoreCsproj }));

        Assert.True(plan.IsRefused);
        Assert.Contains("already reaches the residual", plan.Refusal!, StringComparison.Ordinal);
    }

    // ---- an unknown project name is refused, not a null-reference -------------------------------

    [Fact]
    public void An_unknown_project_name_is_refused()
    {
        var manifest = Manifest(Project("FusionRpg.Core.World.Tests", new[] { "World/**" }));

        var plan = Plan(manifest, "FusionRpg.Core.Ghost.Tests");

        Assert.True(plan.IsRefused);
        Assert.Contains("no project named", plan.Refusal!, StringComparison.Ordinal);
    }
}
