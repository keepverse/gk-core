using System.Diagnostics;
using System.Text.Json;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Regression coverage for the split-Core production owners that were still routed to the residual
/// project after the Core test split. The first test checks the registry contract; the second runs
/// the real planner so a hand-written matcher cannot make a broken registry look correct.
/// </summary>
[Trait("VerificationId", "guard.verification-boundaries")]
public sealed class SplitCoreVerificationMappingTests
{
    private const string RegistryPath = "scripts/verification-boundaries.v1.json";
    private const string TestFilePath = "tests/FusionRpg.Guard.Tests/SplitCoreVerificationMappingTests.cs";
    private const string ResidualProject = "core-residual";

    private sealed record AreaMapping(
        string Area,
        string Path,
        string BoundaryId,
        string ProjectId,
        bool IsGroup = false);

    private static readonly AreaMapping[] AreaMappings =
    {
        new("Activity", "src/FusionRpg.Core/Activity/PvzActivityKinds.cs", "core-area-activity", "core-area-activity-owners", true),
        new("Delve", "src/FusionRpg.Core/Delve/Report/DelveReport.cs", "core-area-delve", "core-area-delve-owners", true),
        new("Expeditions", "src/FusionRpg.Core/Expeditions/ExpeditionResolver.cs", "core-area-expeditions", "core-area-expeditions-owners", true),
        new("PassiveTree", "src/FusionRpg.Core/PassiveTree/State/TreeNodeSet.cs", "core-area-passivetree", "core-area-passivetree-owners", true),
        new("Progression", "src/FusionRpg.Core/Progression/RpgProgression.cs", "core-area-progression", "core-area-progression-owners", true),
        new("Scope", "src/FusionRpg.Core/Scope/WhoSelector.cs", "core-area-scope", "core-area-scope-owners", true),
        new("Vfx", "src/FusionRpg.Core/Vfx/ElementFxPalette.cs", "core-area-vfx", "core-area-vfx-owners", true),
    };

    // A neighboring, already-correct area is the control: the repair must not steal or rewrite it.
    private static readonly AreaMapping ControlMapping = new(
        "Events",
        "src/FusionRpg.Core/Events/GameEventRing.cs",
        "core-area-events",
        "core-events");

    /// <summary>
    /// A file an area boundary covers that a NARROWER owner has since claimed outright, so the planner's
    /// specificity ranking resolves it to the narrower row. <c>elemental-action-vfx-earth-pilot</c> names
    /// <c>gk-core/src/FusionRpg.Core/Vfx/VfxCatalog.cs</c> as an EXACT path, and an exact path outranks
    /// <c>gk-core/src/FusionRpg.Core/Vfx/**</c>.
    /// <para>
    /// This test used to name <c>VfxCatalog.cs</c> as its Vfx representative and failed, because that row
    /// appeared after it. That is not a registry defect and not a planner defect: the narrower claim is
    /// exactly what specificity ranking exists to honour. It was a representative that stopped being
    /// representative. Repointing the representative alone would have hidden the fact, so the fact is
    /// pinned here instead — a reader who finds a Core file resolving "wrongly" now has the rule and the
    /// exception in the same file rather than having to rediscover both.
    /// </para>
    /// </summary>
    private const string NarrowerClaimedPath = "src/FusionRpg.Core/Vfx/VfxCatalog.cs";
    private const string NarrowerClaimBoundary = "elemental-action-vfx-earth-pilot";

    private static IEnumerable<AreaMapping> AllMappings => AreaMappings.Append(ControlMapping);

    [Fact]
    public void A_narrower_exact_claim_OUTRANKS_the_area_row_and_that_is_the_documented_rule()
    {
        using var document = ReadRegistry();
        var root = document.RootElement;

        // The narrower row exists and really does name the file outright. If either half stops being true
        // the behaviour below stops being an override and becomes something to investigate.
        var narrower = FindBoundary(root, NarrowerClaimBoundary);
        var narrowerPaths = StringArray(narrower, "paths");
        Assert.True(narrowerPaths.Contains(NarrowerClaimedPath),
            "the narrower owner no longer names the file, so the next Vfx file to mis-resolve has lost " +
            $"the explanation this test carries; it now names: {string.Join(", ", narrowerPaths)}");

        // The area row still covers the area, and still routes to the narrow owner group. The override is
        // per-FILE, not per-AREA: an agent editing a Vfx file nobody claimed still gets the area's tests.
        var area = FindBoundary(root, "core-area-vfx");
        Assert.Equal("owner", area.GetProperty("kind").GetString());
        Assert.Equal("core-area-vfx-owners", area.GetProperty("project").GetString());
        var areaPaths = StringArray(area, "paths");
        Assert.True(areaPaths.Contains("src/FusionRpg.Core/Vfx/**"),
            $"the Vfx area row no longer covers its area; it names: {string.Join(", ", areaPaths)}");
    }

    [Fact]
    public void Registry_maps_each_split_area_to_its_existing_narrow_owner_group()
    {
        using var document = ReadRegistry();
        var root = document.RootElement;
        var projects = root.GetProperty("projects");
        var boundaries = root.GetProperty("boundaries").EnumerateArray().ToArray();

        foreach (var mapping in AreaMappings)
        {
            var boundary = FindBoundary(root, mapping.BoundaryId);
            Assert.Equal("owner", boundary.GetProperty("kind").GetString());
            Assert.Equal(mapping.ProjectId, boundary.GetProperty("project").GetString());
            Assert.Contains(
                $"src/FusionRpg.Core/{mapping.Area}/**",
                StringArray(boundary, "paths"));
            Assert.NotEqual(ResidualProject, mapping.ProjectId);

            Assert.True(
                projects.TryGetProperty(mapping.ProjectId, out var project),
                $"missing existing owner group: {mapping.ProjectId}");
            Assert.Equal(JsonValueKind.Array, project.ValueKind);
            Assert.All(project.EnumerateArray(), member =>
                Assert.True(
                    File.Exists(Path.Combine(RepoRoot(), member.GetString() ?? "")),
                    $"owner-group member does not exist: {member.GetString()}"));
        }

        var fallback = FindBoundary(root, "core-fallback");
        Assert.Equal("core", fallback.GetProperty("project").GetString());
        Assert.Contains("src/FusionRpg.Core/**", StringArray(fallback, "paths"));
        var fallbackIndex = Array.FindIndex(boundaries, boundary => boundary.GetProperty("id").GetString() == "core-fallback");
        Assert.All(AreaMappings, mapping =>
        {
            var areaIndex = Array.FindIndex(boundaries, boundary => boundary.GetProperty("id").GetString() == mapping.BoundaryId);
            Assert.True(areaIndex >= 0, $"missing area boundary: {mapping.BoundaryId}");
            Assert.True(fallbackIndex < areaIndex, $"fallback moved after {mapping.BoundaryId}");
        });

        var control = FindBoundary(root, ControlMapping.BoundaryId);
        Assert.Equal(ControlMapping.ProjectId, control.GetProperty("project").GetString());
        Assert.Equal("src/FusionRpg.Core/Events/**", Assert.Single(StringArray(control, "paths")));

        var focusedTests = FindBoundary(root, "guard-verification-boundary-tests");
        Assert.Contains(TestFilePath, StringArray(focusedTests, "paths"));
    }

    [Fact]
    public void Planner_resolves_representative_split_core_files_to_area_owners_not_residual()
    {
        var paths = AllMappings.Select(mapping => mapping.Path).ToArray();
        var (exit, stdout, stderr) = RunPlanner(paths);
        Assert.True(exit == 0,
            $"verification planner failed exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

        using var plan = JsonDocument.Parse(stdout);
        var selections = plan.RootElement.GetProperty("selections").EnumerateArray().ToArray();
        var testChecks = plan.RootElement.GetProperty("checks").EnumerateArray()
            .Where(check => check.GetProperty("kind").GetString() == "test")
            .ToArray();

        foreach (var mapping in AllMappings)
        {
            var selection = Assert.Single(selections, item =>
                item.GetProperty("path").GetString() == mapping.Path &&
                item.GetProperty("boundary").GetString() == mapping.BoundaryId);
            Assert.Equal(mapping.ProjectId, selection.GetProperty("project").GetString());
            Assert.NotEqual(ResidualProject, selection.GetProperty("project").GetString());
            Assert.Contains(
                testChecks,
                check => check.GetProperty("id").GetString() == mapping.ProjectId);
        }

        Assert.DoesNotContain(
            testChecks,
            check => check.GetProperty("id").GetString() == ResidualProject);
    }

    private static JsonDocument ReadRegistry()
    {
        var path = Path.Combine(RepoRoot(), RegistryPath.Replace('/', Path.DirectorySeparatorChar));
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static JsonElement FindBoundary(JsonElement root, string id) =>
        root.GetProperty("boundaries").EnumerateArray()
            .Single(boundary => boundary.GetProperty("id").GetString() == id);

    private static string[] StringArray(JsonElement value, string property) =>
        value.GetProperty(property).EnumerateArray().Select(item => item.GetString() ?? "").ToArray();

    private static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    private static (int Exit, string Stdout, string Stderr) RunPlanner(IEnumerable<string> paths)
    {
        // Spawned under PYTHON with an ArgumentList, not as a PowerShell command line. The old form
        // built `--paths @('a','b') --allow-unscoped --plan-only --format json"` and handed it to a
        // NATIVE `pwsh`, which cannot read PowerShell array syntax in argv and cannot read the stray
        // trailing quote -- so it printed pwsh's usage banner and exited 64 on every run. The test had
        // been reporting "the planner said something I do not understand" while never reaching the
        // planner, which is the shape of a failure that hides its own cause.
        //
        // Repointed because the port's own dispatch rule says so: resolve the interpreter by what the
        // tool IS, not by what it was. `gk-core/scripts/verify-change.py` is Python, its repo-root probe in this
        // same file already looked for the `.py`, and only the invocation was left behind -- the
        // half-landed replace that the ordering rule ("make the dispatchers dialect-aware, then
        // replace") exists to prevent.
        var process = new ProcessStartInfo
        {
            FileName = "python",
            WorkingDirectory = RepoRoot(),
            CreateNoWindow = true,
        };
        process.ArgumentList.Add(Path.Combine(RepoRoot(), "scripts", "verify-change.py"));
        process.ArgumentList.Add("--paths");
        foreach (var path in paths) process.ArgumentList.Add(path);
        process.ArgumentList.Add("--allow-unscoped");
        process.ArgumentList.Add("--plan-only");
        process.ArgumentList.Add("--format");
        process.ArgumentList.Add("json");
        return ExternalProcess.Run(process, 300_000, "split-Core verification planner timed out");
    }
}
