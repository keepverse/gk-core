using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Creatures.Fusion;

/// <summary>
/// The real, full anchor corpus (~829 species) expanded through the exact production pipeline
/// (anchor -> `SpeciesExpander` -> temp `RpgStore` -> `BuildCreatureSpeciesSnapshot`) and built ONCE
/// for every test in this folder that needs it — `FusionRecipeDistributionIndexTests` (T8.1) and
/// `FusionRecipeReconcileTests` (T8.3) both need the identical real-corpus snapshot, and repeating
/// the ~800-species import per test class would multiply real disk + expansion work for no reason.
/// `SpeciesCatalogDiffTests.cs`'s own `RealStoreBackedSnapshot()` is NOT reused here — that helper
/// imports exactly 2 named species for a narrowly-scoped diff claim, a different shape of fixture
/// entirely.
/// </summary>
internal static class RealCorpusFixture
{
    static string RepoRoot()
    {
        // Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/tests/Shared/KeepverseRoots.cs):
        // the engine repo root, so the `gk-core/data/tuning` reads below stay valid once the injector source
        // moves to gk-fusion. Hunting for that directory was a private, split-fragile root signal.
        return FusionRpg.TestSupport.CoreRoot.Path;
    }

    static string ReadTuning(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));

    static readonly AptitudeTuning RealAptitudes = AptitudeTuningLoader.Parse(ReadTuning("data", "tuning", "aptitudes.v2.json"));
    static readonly CreatureShapeTuning RealShape = CreatureShapeTuningLoader.Parse(ReadTuning("data", "tuning", "creature-shape.v1.json"));
    static readonly CreatureThreatTuning RealThreat = CreatureThreatTuningLoader.Parse(ReadTuning("data", "tuning", "creature-threat.v2.json"));
    static readonly PowerTuning RealPower = PowerTuningLoader.Parse(ReadTuning("data", "tuning", "power-scale.v2.json"));

    public static readonly IReadOnlyList<CreatureSpeciesDef> Snapshot = Build();

    static IReadOnlyList<CreatureSpeciesDef> Build()
    {
        var seedRoot = Path.Combine(KeepverseRoots.Content(), "data", "seed", "creatures", "species");
        var anchors = new List<AnchorRow>();
        foreach (var file in Directory.GetFiles(seedRoot, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;
            anchors.AddRange(AnchorRowReader.ReadAll(File.ReadAllText(file)));
        }

        var species = new List<ConcreteSpecies>();
        foreach (var anchor in anchors)
        {
            if (SpeciesExpander.UnresolvedFields(anchor).Count > 0) continue; // same skip species-import/the CLI tools both apply
            species.Add(SpeciesExpander.Expand(anchor, RealAptitudes, RealPower, RealShape, RealThreat));
        }
        Assert.True(species.Count > 700, $"expected the real corpus to resolve well over 700 species, got {species.Count} — seed data may have moved");

        // The real corpus import runs in memory; only the parsed species list crosses the boundary.
        using var testStore = DataTestStore.Create();
        {
            var store = testStore.Store;
            var outcome = store.ImportSpecies(species);
            Assert.True(outcome.IsOk, string.Join("; ", outcome.Errors));
            return store.BuildCreatureSpeciesSnapshot();
        }
    }
}
