using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// `catalog-runtime`'s Injector-side flip (2026-09-06) — <see cref="ConcreteSpeciesSeedReader"/> is
/// the read half of <see cref="ConcreteSpeciesSerializer.Canonical"/>, proven two ways: a direct
/// parse-shape test against a hand-built fixture (every field, both element slots, both
/// present/absent), and a real end-to-end diff against the Server's own SQL-backed roster for the
/// FULL real committed tree — the same <see cref="SpeciesDiff"/> mechanism
/// <c>SpeciesCatalogDiffTests</c> already established, run here at the real 829-species scale rather
/// than two hand-picked anchors.
/// </summary>
public class ConcreteSpeciesSeedReaderTests
{
    const string FullFixture = """
        {
          "speciesId": "TestSpecies",
          "rarity": "Cultivated",
          "theta": 13,
          "pTheta": 452,
          "attackIntervalMs": 1500,
          "attackIntervalSource": "classified",
          "rangeCells": 5,
          "variantCount": 2,
          "side": "plant",
          "gameTypeId": 7,
          "elementPrimary": "Earth",
          "elementSecondary": "Fire",
          "deployMode": "PlantAvatar",
          "acquisition": "Summonable",
          "variants": ["mutated", "normal"],
          "traitPool": ["defensive-line", "projectile-launching"],
          "magnitudes": {
            "resource.max.hp": 2712,
            "resource.regen.hp": 136
          }
        }
        """;

    [Fact]
    public void Every_field_the_writer_serialises_round_trips_through_the_reader()
    {
        var s = ConcreteSpeciesSeedReader.Parse(FullFixture);

        Assert.Equal("TestSpecies", s.SpeciesId);
        Assert.Equal(Core.Creatures.CreatureRarity.Cultivated, s.Rarity);
        Assert.Equal(13, s.Theta);
        Assert.Equal(452, s.PTheta);
        Assert.Equal(1500, s.AttackIntervalMs);
        Assert.Equal("classified", s.AttackIntervalSource);
        Assert.Equal(5, s.RangeCells);
        Assert.Equal(2, s.VariantCount);
        Assert.Equal("plant", s.Side);
        Assert.Equal(7, s.GameTypeId);
        Assert.Equal(ElementTypeId.Earth, s.ElementPrimary);
        Assert.Equal(ElementTypeId.Fire, s.ElementSecondary);
        Assert.Equal(Core.Creatures.CreatureDeployMode.PlantAvatar, s.DeployMode);
        Assert.Equal(Core.Creatures.CreatureAcquisition.Summonable, s.Acquisition);
        Assert.Equal(new[] { "mutated", "normal" }, s.Variants);
        Assert.Equal(new[] { "defensive-line", "projectile-launching" }, s.TraitPool);
        Assert.Null(s.Name); // never written by the serialiser — see the class doc
        Assert.Equal(2712, s.Magnitudes["resource.max.hp"]);
        Assert.Equal(136, s.Magnitudes["resource.regen.hp"]);
    }

    [Fact]
    public void ToCreatureSpeciesDef_carries_Magnitudes_through_not_just_the_reader_side()
    {
        // creature-lawn-deploy T1.5: unlike this file's own read-side round-trip above,
        // ConcreteSpeciesMapper.ToCreatureSpeciesDef is the SEPARATE step that turns a parsed
        // ConcreteSpecies into the LIVE CreatureSpeciesDef CreatureSpeciesCatalog.Get actually serves —
        // BuildCreatureSpeciesSnapshot's own diff test compares two calls of this SAME method against each
        // other, so it can never catch a bug in the method's own field-copy; this proves that directly.
        var concrete = ConcreteSpeciesSeedReader.Parse(FullFixture);
        var def = ConcreteSpeciesMapper.ToCreatureSpeciesDef(concrete);

        Assert.Equal(2712, def.Magnitudes["resource.max.hp"]);
        Assert.Equal(136, def.Magnitudes["resource.regen.hp"]);
        Assert.Equal(concrete.Magnitudes.Count, def.Magnitudes.Count);
    }

    [Fact]
    public void A_null_secondary_element_parses_to_null_not_an_exception()
    {
        var json = FullFixture.Replace("\"elementSecondary\": \"Fire\"", "\"elementSecondary\": null");
        var s = ConcreteSpeciesSeedReader.Parse(json);
        Assert.Null(s.ElementSecondary);
    }

    [Fact]
    public void A_combined_flags_acquisition_value_round_trips_through_Enum_Parse()
    {
        // Acquisition is [Flags] — ToString() on a combined value comma-joins ("Summonable,
        // CaptureOnly"), and Enum.Parse must accept that same shape back, not just a single value.
        var json = FullFixture.Replace("\"Summonable\"", "\"Summonable, CaptureOnly\"");
        var s = ConcreteSpeciesSeedReader.Parse(json);
        Assert.True(s.Acquisition.HasFlag(Core.Creatures.CreatureAcquisition.Summonable));
        Assert.True(s.Acquisition.HasFlag(Core.Creatures.CreatureAcquisition.CaptureOnly));
    }

    [Fact]
    public void A_missing_required_field_refuses_with_the_fields_own_name_not_a_generic_error()
    {
        var json = FullFixture.Replace("\"speciesId\": \"TestSpecies\",", "");
        var ex = Assert.Throws<FormatException>(() => ConcreteSpeciesSeedReader.Parse(json));
        Assert.Contains("speciesId", ex.Message, StringComparison.Ordinal);
    }

    // ---- the real end-to-end proof: every one of the 829 real committed files, mapped two ways ------

    static string RepoRoot()
    {
        // Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/tests/Shared/KeepverseRoots.cs):
        // the content root, so every `gk-data/packs/fusion/data/seed` / `gk-data/packs/fusion/data/generated` read here stays valid once content
        // moves to the gk-data pack. Hunting for that directory was a private, split-fragile root signal.
        return FusionRpg.TestSupport.ContentRoot.Path;
    }

    static IReadOnlyList<Core.Creatures.Generation.ConcreteSpecies> RealCommittedSpecies()
    {
        var dir = Path.Combine(KeepverseRoots.Content(), "data", "generated", "creatures");
        var files = Directory.EnumerateFiles(dir, "*.json")
            .Where(p => !Path.GetFileName(p).StartsWith('_')) // skip _species-build-plan.json, _fusion-recipes.json
            .ToList();
        Assert.True(files.Count > 800, $"expected the real ~829-species tree, found {files.Count} files under {dir}");
        return files.Select(ConcreteSpeciesSeedReader.ParseFile).ToList();
    }

    [Fact]
    public void Every_real_committed_species_file_parses_without_throwing()
    {
        var species = RealCommittedSpecies();
        Assert.True(species.Count > 800);
    }

    [Fact]
    public void The_reader_plus_mapper_path_matches_the_real_store_backed_roster_for_every_real_species()
    {
        var concrete = RealCommittedSpecies();

        // Runs in memory: the subject is the reader-vs-store roster comparison, not the substrate, and
        // `RealCorpusFixture` runs the identical ImportSpecies -> BuildCreatureSpeciesSnapshot pipeline
        // on `DataTestStore.Create()`. A file store here would be needless disk churn (standard R1).
        using var testStore = DataTestStore.Create();
        {
            var store = testStore.Store;
            var outcome = store.ImportSpecies(concrete);
            Assert.True(outcome.IsOk, string.Join("; ", outcome.Errors));

            var serverRoster = store.BuildCreatureSpeciesSnapshot();
            var injectorRoster = concrete.Select(ConcreteSpeciesMapper.ToCreatureSpeciesDef).ToList();

            Assert.Equal(serverRoster.Count, injectorRoster.Count);

            var diffs = Core.Creatures.Generation.SpeciesDiff.Compare(serverRoster, injectorRoster);
            // "name" is the one documented, expected divergence: BuildCreatureSpeciesSnapshot's own
            // ConcreteSpecies.Name comes from a database join (almanac_seed) this reader never
            // performs — both sides fall back to the raw SpeciesId identically when Name is null,
            // so a real divergence here would mean the Server actually resolved a DIFFERENT display
            // name for some species, a fact worth knowing, not a bug in either mapping.
            var nonNameDiffs = diffs.Where(d => d.Field != "name").ToList();
            Assert.Empty(nonNameDiffs);

            var (onlyServer, onlyInjector) = Core.Creatures.Generation.SpeciesDiff.Coverage(serverRoster, injectorRoster);
            Assert.Empty(onlyServer);
            Assert.Empty(onlyInjector);
        }
    }

    // ---- rank (spec-species-rank.md §1, creature-seed Task 6) -----------------------------------------

    [Fact]
    public void Rank_reads_back_from_the_committed_tree()
    {
        var json = FullFixture.Replace(
            "\"rarity\": \"Cultivated\",",
            "\"rarity\": \"Cultivated\",\n          \"rank\": \"Chimeric\",");

        Assert.Equal(Core.Creatures.CreatureRank.Chimeric, ConcreteSpeciesSeedReader.Parse(json).Rank);
    }

    [Fact]
    public void A_skipped_rank_reads_null_never_a_default()
    {
        // The fixture has no `rank` key at all — a species whose rank the pipeline skipped, and a
        // pre-rank file. Both must read as null (no rank), never as the bottom rung.
        Assert.Null(ConcreteSpeciesSeedReader.Parse(FullFixture).Rank);
    }

    [Fact]
    public void The_real_committed_tree_carries_each_species_own_anchor_rank()
    {
        // The Injector's own path at real scale (`Injector/Host/RpgHost.cs:145` configures the roster
        // from exactly these files through this reader): a RECONCILIATION between the two committed
        // trees, never a count — the concrete row's rank must be the anchor's own derived rank for the
        // same species, and a species whose anchor has none must read null here. Stable across
        // generations whatever the corpus size, and it fails the moment the reader or the serializer
        // drops the field.
        var anchorRank = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var anchorDir = Path.Combine(KeepverseRoots.Content(), "data", "seed", "creatures", "species");
        foreach (var file in Directory.EnumerateFiles(anchorDir, "*.json", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "_index.json") continue;
            foreach (var anchor in AnchorRowReader.ReadAll(File.ReadAllText(file)))
                anchorRank[anchor.SpeciesId] = anchor.Rank;
        }
        Assert.NotEmpty(anchorRank);

        var concreteDir = Path.Combine(KeepverseRoots.Content(), "data", "generated", "creatures");
        var checkedFiles = 0;
        var disagreements = new List<string>();
        foreach (var file in Directory.EnumerateFiles(concreteDir, "*.json"))
        {
            if (Path.GetFileName(file).StartsWith("_", StringComparison.Ordinal)) continue;
            var species = ConcreteSpeciesSeedReader.ParseFile(file);
            checkedFiles++;
            anchorRank.TryGetValue(species.SpeciesId, out var expected);
            var actual = species.Rank?.ToString().ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                disagreements.Add($"{species.SpeciesId}: concrete={actual ?? "(absent)"} anchor={expected ?? "(absent)"}");
        }
        Assert.True(checkedFiles > 0, "no committed species file was read");
        Assert.True(disagreements.Count == 0,
            "concrete rank disagrees with the anchor's own rank: " + string.Join(", ", disagreements.Take(5)));
    }
}
