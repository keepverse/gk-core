using System.IO;
using FusionRpg.Core.World;
using FusionRpg.Core.World.StructureSeed;
using Xunit;
using FusionRpg.TestSupport;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// base-defense `structure-catalog-import` (module 25, spec-structure-catalog-import.md).
/// `StructureCatalog.Configure` is static/shared, so every test that calls it MUST restore the REAL
/// corpus (never `null`) in a `finally` — task 25.4 deleted the C# `Seed` literal, so `Configure(null)`
/// no longer means "revert to a working default," it means "break the catalog for every other test
/// in this process from this point on" (confirmed the hard way: an earlier draft of this file left
/// `Configure(null)` in every `finally`, which passed in isolation but threw
/// `InvalidOperationException` inside unrelated `DistrictAssaultResolverTests` whenever the full
/// suite ran these tests first — shared static state, ordering-dependent, exactly the failure mode
/// this comment now exists to prevent a second time). Restoring the real corpus matches what
/// `StructureCatalogTestBootstrap`'s own `[ModuleInitializer]` already configures at assembly load,
/// so a test that restores it is simply putting back what was there before it ran.
/// </summary>
public class StructureCatalogImportTests
{
    static string RealCorpusRoot() => Path.Combine(KeepverseRoots.Content(), "data", "seed", "structures");

    static void RestoreRealCorpus() => StructureCatalog.Configure(StructureCorpus.Load(RealCorpusRoot()));

    [Fact]
    public void The_eight_shipped_rows_are_byte_identical_through_the_corpus()
    {
        var before = StructureCatalog.All.ToDictionary(s => s.StructureId);
        try
        {
            StructureCatalog.Configure(StructureCorpus.Load(RealCorpusRoot()));
            var after = StructureCatalog.All.ToDictionary(s => s.StructureId);

            var shippedIds = new[]
            {
                "loam-source-placeholder", "well", "waystation", "granary",
                "soul-conduit", "extractor", "hatchery", "moat",
            };
            Assert.Equal(8, shippedIds.Length);

            foreach (var id in shippedIds)
            {
                var b = before[id];
                var a = after[id];
                Assert.Equal(b.Name, a.Name);
                Assert.Equal(b.Kind, a.Kind);
                Assert.Equal(b.RequiredSlotKind, a.RequiredSlotKind);
                Assert.Equal(b.Cost, a.Cost);
                Assert.Equal(b.YieldMultiplierMilli, a.YieldMultiplierMilli);
                Assert.Equal(b.BuildTurns, a.BuildTurns);
                Assert.Equal(b.CapacityBonus, a.CapacityBonus);
                Assert.Equal(b.FlatYieldPerTurn, a.FlatYieldPerTurn);
                Assert.Equal(b.ConstructRubbleCost, a.ConstructRubbleCost);
                Assert.Equal(b.ConstructIronworkCost, a.ConstructIronworkCost);
                Assert.Equal(b.MaterialTier, a.MaterialTier);
                Assert.Equal(b.BlocksMovement, a.BlocksMovement);
                Assert.Equal(b.BlocksLineOfFire, a.BlocksLineOfFire);
                Assert.Equal(b.Obstacle, a.Obstacle);
                Assert.Equal(b.CoverPowerMilli, a.CoverPowerMilli);
                Assert.Equal(b.CoverRadius, a.CoverRadius);
                Assert.Equal(b.EntryStaminaMultiplierMilli, a.EntryStaminaMultiplierMilli);
                Assert.Equal(b.VisionRangeTiles, a.VisionRangeTiles);
                Assert.Equal(b.AcquisitionPaths.OrderBy(p => p), a.AcquisitionPaths.OrderBy(p => p));

                // MaxHp is the ONE intentional difference (spec §4) -- both sides compute it the
                // same LIVE way (through the SAME StructureDef.MaxHpOf), so if every field above
                // matched, this must already agree too; asserted anyway as a direct proof.
                Assert.Equal(StructureDef.MaxHpOf(b, developmentLevel: 5), StructureDef.MaxHpOf(a, developmentLevel: 5));
            }
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void Anchor_only_rows_are_not_catalog_loadable_yet()
    {
        try
        {
            var corpus = StructureCorpus.Load(RealCorpusRoot());
            // The anchor-only count (17 today) is a growing seed population -- rows move to
            // catalog-loadable as they get authored -- never pinned (population-pin SE3.3,
            // 2026-09-19). The real contract is the membership check below: not one of these ids has
            // leaked into the real catalog early.
            var anchorOnly = corpus.Rows.Where(r => !r.IsCatalogLoadable).ToList();
            Assert.NotEmpty(anchorOnly);

            StructureCatalog.Configure(corpus);
            var ids = StructureCatalog.All.Select(s => s.StructureId).ToHashSet(StringComparer.Ordinal);
            foreach (var row in anchorOnly)
                Assert.DoesNotContain(row.StructureId, ids); // identity-registered, not yet a real StructureDef
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void Configure_resets_the_cache()
    {
        var firstAll = StructureCatalog.All;
        try
        {
            StructureCatalog.Configure(StructureCorpus.Load(RealCorpusRoot()));
            var secondAll = StructureCatalog.All;
            Assert.NotSame(firstAll, secondAll); // a stale cached list would be the exact bug this guards

            RestoreRealCorpus();
            var thirdAll = StructureCatalog.All;
            Assert.NotSame(secondAll, thirdAll); // reverting also rebuilds, not just configuring forward
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void An_unconfigured_catalog_is_unchanged_from_before_this_module()
    {
        // Every existing test in this repo that never calls Configure itself still sees a real,
        // working catalog -- not because of a Seed literal any more (25.4 deleted it), but because
        // StructureCatalogTestBootstrap's own [ModuleInitializer] already configured the real corpus
        // before any test in this assembly runs. This test's own name predates that fix and is kept
        // because the OUTWARD behavior it asserts is still exactly true, just for a different reason.
        //
        // Asserts a SUPERSET, never an exact count (validation-ssot: corpus size is a derived
        // population, not a closed vocabulary): hand-authored content rows landed after this test
        // was written — the two 4B.1 wonder rows plus `relic-vault` (4B.2,
        // empire-inventory-surfaces `storage-content`) — so pinning 8 fails on any tree that ships
        // real content, which is the normal case, not drift.
        var shippedIds = new[]
        {
            "loam-source-placeholder", "well", "waystation", "granary",
            "soul-conduit", "extractor", "hatchery", "moat",
        };
        foreach (var id in shippedIds)
            Assert.True(StructureCatalog.IsKnown(id));
    }

    [Fact]
    public void An_unknown_role_derived_kind_throws_at_load()
    {
        // The malformed row is this fixture's own text: it is parsed through the loader's in-memory entrance,
        // so the case proves the parse/validate refusal without writing a corpus to disk.
        try
        {
            var badJson = """
            {
              "kind": "structure-anchor",
              "_meta": {"partition": "Extract"},
              "entries": [{
                "id": "test-bad-kind",
                "name": "Test Bad Kind",
                "anchor": {
                  "structureId": "test-bad-kind", "family": "loam-structures", "role": "Extract",
                  "roleSecondary": "none", "requiredSlotKind": "Wildland", "elementPrimary": "none",
                  "elementSecondary": "none", "tempo": "none", "reach": "melee",
                  "strengthBand": "rubble", "rarity": "sprout", "traits": [], "costProfile": "cheap",
                  "targetPreference": "none", "variants": [], "acquisitionPaths": ["built"],
                  "footprint": "one-cell", "coverTier": "none", "controlPoint": true,
                  "obstacleVerbs": [], "reason": "test"
                },
                "_provenance": {"source": "AUTHORED", "citation": "test"},
                "magnitudes": {
                  "structureKind": "NotARealStructureKind", "cost": 0, "yieldMultiplierMilli": 1000,
                  "buildTurns": 0, "capacityBonus": 0, "flatYieldPerTurn": 0,
                  "constructRubbleCost": 0, "constructIronworkCost": 0, "materialTier": 0,
                  "blocksMovement": false, "blocksLineOfFire": false, "obstacleKind": "None",
                  "coverPowerMilli": 0, "coverRadius": 0, "entryStaminaMultiplierMilli": 1000,
                  "visionRangeTiles": null
                }
              }]
            }
            """;

            StructureCatalog.Configure(StructureCorpus.FromJson(badJson));
            Assert.ThrowsAny<Exception>(() => StructureCatalog.All);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void Adding_a_row_needs_no_rebuild()
    {
        // The module's own purpose, asserted directly: a brand-new structure id becomes real and loadable with
        // zero C# changes and zero rebuild -- only Configure(a corpus containing it). The same JSON the loader
        // reads from a file is handed over in memory, so this case writes no corpus to disk; the
        // directory-READ path is what the real-corpus cases above (and the server's startup) cover.
        try
        {
            var newJson = """
            {
              "kind": "structure-anchor",
              "_meta": {"partition": "Store"},
              "entries": [{
                "id": "test-brand-new-structure",
                "name": "Test Brand New Structure",
                "anchor": {
                  "structureId": "test-brand-new-structure", "family": "loam-structures",
                  "role": "Store", "roleSecondary": "none", "requiredSlotKind": "Wildland",
                  "elementPrimary": "none", "elementSecondary": "none", "tempo": "none",
                  "reach": "melee", "strengthBand": "rubble", "rarity": "sprout", "traits": [],
                  "costProfile": "cheap", "targetPreference": "none", "variants": [],
                  "acquisitionPaths": ["built"], "footprint": "one-cell", "coverTier": "none",
                  "controlPoint": true, "obstacleVerbs": [], "reason": "test"
                },
                "_provenance": {"source": "AUTHORED", "citation": "test"},
                "magnitudes": {
                  "structureKind": "Storage", "cost": 42, "yieldMultiplierMilli": 1000,
                  "buildTurns": 1, "capacityBonus": 99, "flatYieldPerTurn": 0,
                  "constructRubbleCost": 0, "constructIronworkCost": 0, "materialTier": 0,
                  "blocksMovement": false, "blocksLineOfFire": false, "obstacleKind": "None",
                  "coverPowerMilli": 0, "coverRadius": 0, "entryStaminaMultiplierMilli": 1000,
                  "visionRangeTiles": null
                }
              }]
            }
            """;

            StructureCatalog.Configure(StructureCorpus.FromJson(newJson));
            var loaded = StructureCatalog.Get("test-brand-new-structure");
            Assert.Equal("Test Brand New Structure", loaded.Name);
            Assert.Equal(StructureKind.Storage, loaded.Kind);
            Assert.Equal(42, loaded.Cost);
            Assert.Equal(99, loaded.CapacityBonus);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void FromJson_reads_the_same_document_shape_a_file_holds()
    {
        // BU9: the file format's own in-memory entrance. One parser serves Load and FromJson (one shape, not
        // two), so a fixture that authors its rows in a literal cannot drift from a committed corpus file.
        var corpus = StructureCorpus.FromJson("""
        {
          "kind": "structure-anchor",
          "_meta": {"partition": "Store"},
          "entries": [
            {
              "id": "test-from-json-depot",
              "name": "Test From Json Depot",
              "anchor": {
                "structureId": "test-from-json-depot", "family": "loam-structures", "role": "Store",
                "roleSecondary": "none", "requiredSlotKind": "Wildland", "elementPrimary": "none",
                "elementSecondary": "none", "tempo": "none", "reach": "melee",
                "strengthBand": "rubble", "rarity": "sprout", "traits": [], "costProfile": "cheap",
                "targetPreference": "none", "variants": [], "acquisitionPaths": ["built"],
                "footprint": "one-cell", "coverTier": "none", "controlPoint": true,
                "obstacleVerbs": [], "reason": "test"
              },
              "_provenance": {"source": "AUTHORED", "citation": "test"},
              "magnitudes": {
                "structureKind": "ItemStorage", "cost": 0, "yieldMultiplierMilli": 1000,
                "buildTurns": 0, "capacityBonus": 0, "itemStorageCapacityBonus": 25,
                "flatYieldPerTurn": 0, "constructRubbleCost": 0, "constructIronworkCost": 0,
                "materialTier": 0, "blocksMovement": false, "blocksLineOfFire": false,
                "obstacleKind": "None", "coverPowerMilli": 0, "coverRadius": 0,
                "entryStaminaMultiplierMilli": 1000, "visionRangeTiles": null
              }
            }
          ]
        }
        """);

        var row = Assert.Single(corpus.Rows);
        Assert.Equal("test-from-json-depot", row.StructureId);
        Assert.Equal(25, row.Magnitudes!.ItemStorageCapacityBonus);

        StructureCatalog.Configure(corpus);
        try
        {
            var def = StructureCatalog.Get("test-from-json-depot");
            Assert.Equal(StructureKind.ItemStorage, def.Kind);
            Assert.Equal(25, def.ItemStorageCapacityBonus);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void FromJson_contributes_nothing_for_a_document_that_is_not_corpus_content()
    {
        // Same rule Load applies to a stray JSON file: a document whose top level is not
        // `{"kind": "structure-anchor", ...}` is not corpus content, and a corpus document without an
        // `entries` array holds no rows.
        Assert.Empty(StructureCorpus.FromJson("""{"kind": "something-else", "entries": [{"id": "x"}]}""").Rows);
        Assert.Empty(StructureCorpus.FromJson("""{"kind": "structure-anchor"}""").Rows);
    }

    [Fact]
    public void FromJson_names_its_source_when_a_row_is_unreadable()
    {
        // Loud over silent, the same stance Load takes for a bad file: the failure says which document it came
        // from, so an inline fixture is as diagnosable as a file.
        var ex = Assert.Throws<StructureCorpusLoadException>(() => StructureCorpus.FromJson(
            """{"kind": "structure-anchor", "entries": [{"id": "test-incomplete"}]}""", "inline-test"));

        Assert.Contains("inline-test", ex.Message, StringComparison.Ordinal);
        Assert.Contains("anchor", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public class BandsTests
{
    [Theory]
    [InlineData("rubble", 1)]
    [InlineData("timber", 2)]
    [InlineData("stone", 3)]
    public void MaterialTierOf_resolves_the_three_real_bands(string band, int expectedTier) =>
        Assert.Equal(expectedTier, Bands.MaterialTierOf(band));

    [Fact]
    public void MaterialTierOf_throws_for_an_unknown_band() =>
        Assert.Throws<InvalidOperationException>(() => Bands.MaterialTierOf("adamantium"));
}
