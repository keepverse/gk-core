using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// The checked-in generated file (E23/E11 Step 4) — <c>EffectAtomCatalog.Generated.cs</c>, produced by
/// <c>gk-forge/tools/ElementEnumGen --effect-emit</c> from <c>data/seed/atoms/fx-*.json</c> — proven identical
/// to the hand-written <c>EffectSeedCatalog</c> it replaces at the five call sites, both by DTO shape
/// and by real scenario execution, before anything is repointed at it.
/// </summary>
public class EffectAtomCatalogGeneratedTests
{
    public static IEnumerable<object[]> ScenarioFiles()
    {
        var dir = FindScenariosDir();
        foreach (var path in Directory.GetFiles(dir, "effect-*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            yield return new object[] { path };
    }

    /// <summary>
    /// E43 (spec-family-expand.md §3.3, corrected 2026-09-03): the old assertion was
    /// <c>Assert.Equal(seeded, generated)</c> — set EQUALITY against a frozen 16-id catalog, so a 17th
    /// generated id failed this test however the count was computed ("sixteen" was in the method name
    /// only). The real property this test was written for is narrower and still holds with growth:
    /// every retired hand-written id is reproduced exactly, and the generated catalog is free to be a
    /// SUPERSET of it (fx-*.json growing past the original 16, e.g. once a future generator's output
    /// legitimately ships there).
    /// </summary>
    [Fact]
    public void Reproduces_every_retired_hand_written_id_and_allows_growth()
    {
        var seeded = EffectSeedCatalog.CreateAll().Select(d => d.EffectId).ToList();
        var seededSet = seeded.ToHashSet(StringComparer.Ordinal);
        var generated = EffectAtomCatalog.CreateAll().Select(d => d.EffectId).ToList();

        Assert.Equal(
            seeded.OrderBy(x => x, StringComparer.Ordinal),
            generated.Where(id => seededSet.Contains(id)).OrderBy(x => x, StringComparer.Ordinal));
        Assert.True(generated.ToHashSet(StringComparer.Ordinal).IsSupersetOf(seededSet));
    }

    /// <summary>
    /// E43 test 9 (spec-family-expand.md §5): proves the assertion above actually tolerates growth
    /// past the original 16 — simulated with local lists rather than the real catalog (nothing in the
    /// real fx-*.json corpus has grown past 16 yet), so this exercises the ASSERTION's own shape
    /// directly instead of waiting for real content to prove it.
    /// </summary>
    [Fact]
    public void The_growth_safe_assertion_shape_passes_when_the_generated_side_gains_new_ids()
    {
        var seeded = new List<string> { "fx.a", "fx.b", "fx.c" };
        var seededSet = seeded.ToHashSet(StringComparer.Ordinal);
        var generatedPast16 = new List<string> { "fx.c", "fx.a", "fx.b", "fx.new-17th", "fx.new-18th" };

        Assert.Equal(
            seeded.OrderBy(x => x, StringComparer.Ordinal),
            generatedPast16.Where(id => seededSet.Contains(id)).OrderBy(x => x, StringComparer.Ordinal));
        Assert.True(generatedPast16.ToHashSet(StringComparer.Ordinal).IsSupersetOf(seededSet));
    }

    [Theory]
    [MemberData(nameof(ScenarioFiles))]
    public void Every_effect_scenario_passes_against_the_generated_catalog(string path)
    {
        var goldenRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, ".."));
        var result = EffectScenarioRunner.RunFile(path, goldenRoot, catalog: EffectAtomCatalog.CreateAll());

        Assert.True(result.Ok, $"{result.Id}: {result.Error}");
    }

    static string FindScenariosDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "effects", "scenarios");
            if (Directory.Exists(candidate)) return candidate;
            var up = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "fixtures", "effects", "scenarios"));
            if (Directory.Exists(up)) return up;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }

        throw new DirectoryNotFoundException("fixtures/effects/scenarios");
    }
}
