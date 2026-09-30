using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// T4.8 step 4, `catalog-runtime`'s own ⛔ acceptance gate (`spec-catalog-runtime.md` §6): "before
/// deleting `CreatureSpeciesCatalog.Generated.cs`, both sources exist. A test loads the store-backed
/// catalog and the compiled one and diffs them field by field." Real end to end: real anchors
/// (`pea.json`/`sunflower.json`) → real `SpeciesExpander` → a real temp `RpgStore` →
/// `BuildCreatureSpeciesSnapshot()` → `SpeciesDiff.Compare` against the real compiled
/// `CreatureSpeciesCatalog.All` (every host in this assembly configures with the compiled default —
/// `ContractTuningTestBootstrap`'s own `[ModuleInitializer]` — so `All` here IS the compiled roster,
/// exactly as every real host reads it today, pre-flip).
///
/// <para><b>What this does NOT claim.</b> `peashooter`/`sunflower` are real overlapping ids between
/// the compiled catalog and the anchor pipeline, so this proves the MECHANISM against real data —
/// but it does not claim the reported differences are "human accepted" (spec §6's own separate,
/// `anchor-emit --diff-legacy`-gated step, not run here). A future full-84-species diff, once T2.11's
/// real classification run lands, is this same mechanism against a bigger input, not new code.</para>
/// </summary>
public class SpeciesCatalogDiffTests
{
    static string RepoRoot()
    {
        // Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/tests/Shared/KeepverseRoots.cs):
        // the engine repo root, so the `gk-core/data/tuning` reads below stay valid once the injector source
        // moves to gk-fusion. Hunting for that directory was a private, split-fragile root signal.
        return FusionRpg.TestSupport.CoreRoot.Path;
    }

    static string ReadTuning(params string[] relative) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));

    /// <summary>The seed corpus, which the split put in the gk-data pack. A separate reader rather
    /// than a changed <see cref="ReadTuning"/>: that one is right for data/tuning/**, which is
    /// gk-core's own, and re-rooting it would break every tuning read that shares it.</summary>
    static string ReadContent(params string[] relative) => File.ReadAllText(Path.Combine(new[] { KeepverseRoots.Content() }.Concat(relative).ToArray()));

    static readonly AptitudeTuning RealAptitudes = AptitudeTuningLoader.Parse(ReadTuning("data", "tuning", "aptitudes.v2.json"));
    static readonly CreatureShapeTuning RealShape = CreatureShapeTuningLoader.Parse(ReadTuning("data", "tuning", "creature-shape.v1.json"));
    static readonly CreatureThreatTuning RealThreat = CreatureThreatTuningLoader.Parse(ReadTuning("data", "tuning", "creature-threat.v2.json"));
    static readonly PowerTuning RealPower = PowerTuningLoader.Parse(ReadTuning("data", "tuning", "power-scale.v2.json"));

    /// <summary>Resolves a species' CURRENT real anchor file via `_index.json` rather than a
    /// hardcoded path — found broken live, 2026-09-04 (creature-corpus-self-heal): a species' family
    /// bucket is model-decided and moves across reclassifications, so a test hardcoding
    /// `"pea.json"` breaks the moment the pipeline it exercises does its own job correctly.</summary>
    static AnchorRow RealAnchor(string speciesId)
    {
        var indexPath = Path.Combine(KeepverseRoots.Content(), "data", "seed", "creatures", "species", "_index.json");
        var index = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(indexPath))!;
        var relPath = index[speciesId];
        return AnchorRowReader.ReadAll(ReadContent("data", "seed", "creatures", "species", relPath.Replace('/', Path.DirectorySeparatorChar)))
            .Single(a => a.SpeciesId == speciesId);
    }

    /// <summary>A real store with the two real classified anchors imported through the full, real
    /// `SpeciesExpander` -> `RpgStore.ImportSpecies` pipeline — not a hand-built fixture. Runs in
    /// memory: the pipeline is identical, only the substrate differs.</summary>
    static IReadOnlyList<CreatureSpeciesDef> RealStoreBackedSnapshot()
    {
        using var testStore = DataTestStore.Create();
        {
            var store = testStore.Store;

            var species = new[]
            {
                SpeciesExpander.Expand(RealAnchor("Peashooter"), RealAptitudes, RealPower, RealShape, RealThreat),
                SpeciesExpander.Expand(RealAnchor("SunFlower"), RealAptitudes, RealPower, RealShape, RealThreat),
            };
            var outcome = store.ImportSpecies(species);
            Assert.True(outcome.IsOk, string.Join("; ", outcome.Errors));

            return store.BuildCreatureSpeciesSnapshot();
        }
    }

    [Fact]
    public void The_diff_mechanism_finds_the_real_known_differences_for_peashooter_and_sunflower()
    {
        var storeBacked = RealStoreBackedSnapshot();
        var diffs = SpeciesDiff.Compare(CreatureSpeciesCatalog.All, storeBacked);

        // peashooter/sunflower are the two real ids the OLD hash-based generator and the NEW
        // anchor-based pipeline both produced — the only species this diff can run against until
        // T2.11's real classification run covers the rest.
        var peashooterDiffs = diffs.Where(d => d.SpeciesId == "peashooter").Select(d => d.Field).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(peashooterDiffs); // real differences exist — this is not a no-op comparison

        // creatureTypeId used to differ by construction — RpgStore.BuildCreatureSpeciesSnapshot originally
        // computed it as `GameTypeId + CreatureTypeIdFloor` with no side split, unlike the old compiled
        // generator's plant/zombie-split space (60000+/10000+). That was a real, undiscovered bug, not
        // a documented divergence: found running the real catalog-runtime flip 2026-09-05 when a plant
        // and a zombie sharing the same raw GameTypeId (BigWallNut/255, BlackTrainZombie/255)
        // collided on the same CreatureTypeId and CreatureSpeciesCatalog.Validate correctly refused to
        // start. Fixed by reproducing the old generator's own side split in
        // BuildCreatureSpeciesSnapshot — the two formulas are now IDENTICAL, so creatureTypeId is no longer
        // a diff for any species whose gameTypeId round-trips unchanged (every species here).
        Assert.DoesNotContain("creatureTypeId", peashooterDiffs);
        // traitPool no longer differs (2026-09-06, trait-roll): CreatureTraitPoolCuration now ports the
        // compiled catalog's own TraitPool forward verbatim for every species it still covers,
        // peashooter included — the two rosters agree on this field by construction, a real positive
        // change from the prior "always empty" snapshot this assertion used to document.
        Assert.DoesNotContain("traitPool", peashooterDiffs);
        // variants still differs: the compiled catalog's own hash-based VariantsFor(rarity, typeId)
        // and the anchor pipeline's real anchor.Variants are two independently-computed lists that
        // were never unified — the real, remaining divergence this test now proves instead.
        Assert.Contains("variants", peashooterDiffs);
    }

    [Fact]
    public void Fields_that_genuinely_match_are_never_reported_as_differences()
    {
        // side/baseRarity/deployMode/acquisition are real, independently-verified matches between
        // pea.json's own values and the old compiled entry (both "plant"/"cultivated"/
        // "PlantAvatar"/"Summonable") — the diff must stay silent on a field that actually agrees,
        // proving this isn't a mechanism that just flags everything.
        var storeBacked = RealStoreBackedSnapshot();
        var diffs = SpeciesDiff.Compare(CreatureSpeciesCatalog.All, storeBacked);
        var peashooterFields = diffs.Where(d => d.SpeciesId == "peashooter").Select(d => d.Field).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("side", peashooterFields);
        Assert.DoesNotContain("baseRarity", peashooterFields);
        Assert.DoesNotContain("deployMode", peashooterFields);
        Assert.DoesNotContain("acquisition", peashooterFields);
    }

    [Fact]
    public void A_species_present_in_only_one_roster_is_reported_as_coverage_not_a_field_diff()
    {
        var storeBacked = RealStoreBackedSnapshot();
        var (onlyCompiled, onlyStoreBacked) = SpeciesDiff.Coverage(CreatureSpeciesCatalog.All, storeBacked);

        // The compiled roster has ~84 species; the store today has only the two real anchors this
        // test imported — so "only in compiled" is large and real, "only in store-backed" is empty.
        Assert.True(onlyCompiled.Count > 2);
        Assert.Empty(onlyStoreBacked);
        Assert.DoesNotContain("peashooter", onlyCompiled); // present in both, not a coverage gap
    }

    [Fact]
    public void Comparing_a_roster_against_itself_finds_nothing()
    {
        var storeBacked = RealStoreBackedSnapshot();
        Assert.Empty(SpeciesDiff.Compare(storeBacked, storeBacked));
    }

    [Fact]
    public void The_store_backed_snapshot_itself_passes_CreatureSpeciesCatalog_Validate()
    {
        // The snapshot RpgStore.BuildCreatureSpeciesSnapshot() produces must be a legal
        // CreatureSpeciesCatalog roster on its own terms — proven by actually calling Configure with
        // it (UseScoped, so this never leaks into a test running beside it) rather than assuming
        // Validate would pass.
        var storeBacked = RealStoreBackedSnapshot();
        using (CreatureSpeciesCatalog.UseScoped(storeBacked))
        {
            Assert.Equal(2, CreatureSpeciesCatalog.All.Count);
            Assert.True(CreatureSpeciesCatalog.IsKnown("peashooter"));
            Assert.True(CreatureSpeciesCatalog.IsKnown("sunflower"));
        }

        // Restored to the compiled roster outside the scope — proves UseScoped's own isolation.
        Assert.True(CreatureSpeciesCatalog.All.Count > 2);
    }
}
