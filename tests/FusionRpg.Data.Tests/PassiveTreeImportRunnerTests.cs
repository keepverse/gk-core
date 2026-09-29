using FusionRpg.Data.Seed;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// H9's own "committed" acceptance bullet (passive-tree-todo.md) — found 2026-09-07 that
/// <see cref="RpgStore.ImportTreeCatalog"/>/<see cref="RpgStore.ImportTreeCatalogFiles"/> (task C4/C5,
/// already shipped) had ZERO production callers: nothing ever imported a bound
/// <c>gk-data/packs/fusion/data/generated/passive-tree/*.json</c> catalog into the store at boot. These tests exercise
/// <see cref="PassiveTreeImportRunner.RunSelfHealing"/> directly — the same shape
/// <see cref="SeedImportRunnerTests"/> already uses for the atom-content self-heal — against a small,
/// synthetic tree-catalog fixture, never the real 480-node corpus (which is itself still partial, and
/// whose own end-to-end wiring test, <c>ContentBootStartupWiringTests</c>, is unrelatedly red from a
/// concurrent session's own in-progress work as of this writing — this file's own fixtures are fully
/// isolated from that).
/// </summary>
[Trait("Category", "DiskSemantics")]
public class PassiveTreeImportRunnerTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    // The import runner's subject is a real generated-corpus directory on disk, so the fixture base
    // stays a real temp dir (the "disk is the thing under test" case); only the store is in memory.
    readonly string _dir;

    public PassiveTreeImportRunnerTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _dir = Path.Combine(Path.GetTempPath(), "fusionrpg-treeimport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _testStore.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ---- fixtures -----------------------------------------------------------------------------

    // The real gk-core/data/tuning/passive-tree.v1.json content, verbatim (this program's own "ONE tunable
    // file" — a test fixture copy, never a second source of truth for the real values).
    const string ValidTuningFile = """
        {
          "schemaVersion": 1,
          "version": 1,
          "tierLadder": { "reqScalePoints": 5 },
          "budget": { "treeTotalPoints": 1000, "branchSplitMilli": 500 },
          "treeShareMilli": 1000,
          "treeBudgetMilli": 1000,
          "potency": { "maxNodeShareMilli": 182, "minTerminalWidth": 1, "bandEdgesMilli": [46, 91, 137, 182] },
          "mechanism": { "rampStartMilli": 0, "rampEndMilli": 1000 },
          "archetype": { "rewardSpreadMaxRatioMilli": 6000 },
          "exclusion": { "targetShareMilli": 20 },
          "archetypeAssignment": "ordinal-round-robin",
          "designTarget": { "thetaAllIn": 92 },
          "concentration": { "fmaxMilli": 1200, "wMilli": 500 },
          "soulTrack": { "thetaPerSoulLevelMilli": 1000 },
          "unlockCost": { "firstPoints": 5, "stepPoints": 2 },
          "respec": { "basePrice": 50, "escalationPermille": 500 },
          "gateCounters": {
            "masteryCurveFirstCount": 23, "masteryCurveStepCount": 23,
            "elementMasteryRatePoints": 4, "statusMasteryRatePoints": 4, "flushIntervalMs": 5000
          }
        }
        """;

    static string TreeJson(string treeId) => $$"""
        {
          "treeId": "{{treeId}}",
          "category": "primary",
          "gateQuantity": "aptitude.Might@Commander",
          "shapeArchetype": "broad-and-flat",
          "tiers": 10,
          "branches": 2,
          "nodesPerTier": [2,2,2,2,2,2,2,2,2,2],
          "catalogVersion": 1,
          "enabled": true,
          "nodes": [
            {
              "id": "skill.{{treeId}}-off-t1-n0",
              "branch": "off",
              "tier": 1,
              "nodeKey": "n0",
              "prereqNodeIds": [],
              "nodeClass": "magnitude",
              "affixIds": ["affix.a"],
              "budgetShareMilli": 18,
              "atoms": [
                {
                  "kindId": "stat.modify",
                  "attachPoint": "Stat",
                  "channelId": "atk",
                  "op": "flat",
                  "trigger": null,
                  "whenJson": null,
                  "kMicro": 12345,
                  "scaleAxis": "PTheta",
                  "unitClass": "GameUnits"
                }
              ],
              "excludeProps": [],
              "exclusionForm": "None",
              "tagsJson": null,
              "enabled": true,
              "retiredAtRevision": null
            }
          ]
        }
        """;

    static void WriteFixtureTree(string generatedDir, string treeId) =>
        File.WriteAllText(Path.Combine(generatedDir, $"{treeId}.json"), TreeJson(treeId));

    /// <summary>A search root with a real, importable <c>gk-data/packs/fusion/data/generated/passive-tree</c> and
    /// <c>gk-core/data/tuning</c>.</summary>
    static string MakeValidCorpus(string underDir, params string[] treeIds)
    {
        var generatedDir = Path.Combine(underDir, "data", "generated", "passive-tree");
        Directory.CreateDirectory(generatedDir);
        foreach (var treeId in treeIds)
            WriteFixtureTree(generatedDir, treeId);

        var tuningDir = Path.Combine(underDir, "data", "tuning");
        Directory.CreateDirectory(tuningDir);
        File.WriteAllText(Path.Combine(tuningDir, "passive-tree.v1.json"), ValidTuningFile);
        return underDir;
    }

    // ---- test 1: a clean install imports -------------------------------------------------------

    [Fact]
    public void A_clean_bound_catalog_imports_and_the_tree_catalog_revision_becomes_nonzero()
    {
        var searchStart = MakeValidCorpus(_dir + "-tree1", "might", "fortitude");
        try
        {
            var result = PassiveTreeImportRunner.RunSelfHealing(_store, searchStart);

            Assert.Equal(PassiveTreeImportStatus.Imported, result.Status);
            Assert.NotNull(result.Outcome);
            Assert.True(result.Outcome!.Ok);
            Assert.Equal(2, result.Outcome.TreesImported);
            Assert.True(_store.GetTreeCatalogRevision() > 0);
        }
        finally
        {
            Directory.Delete(searchStart, recursive: true);
        }
    }

    // ---- test 2: no reachable corpus is skipped, never fatal -------------------------------------

    [Fact]
    public void An_install_with_no_reachable_generated_corpus_is_skipped()
    {
        var isolated = Path.Combine(Path.GetTempPath(), "fusionrpg-no-tree-corpus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated);
        try
        {
            var result = PassiveTreeImportRunner.RunSelfHealing(_store, isolated);

            Assert.Equal(PassiveTreeImportStatus.TreeNotFound, result.Status);
            Assert.NotNull(result.Detail);
            Assert.Null(result.Outcome);
            Assert.Equal(0, _store.GetTreeCatalogRevision());
        }
        finally
        {
            Directory.Delete(isolated, recursive: true);
        }
    }

    // ---- test 3: a directory that exists but holds nothing is the same as not found -------------

    [Fact]
    public void An_empty_generated_directory_is_treated_as_not_found_not_a_failure()
    {
        var searchStart = _dir + "-tree3";
        Directory.CreateDirectory(Path.Combine(searchStart, "data", "generated", "passive-tree"));
        Directory.CreateDirectory(Path.Combine(searchStart, "data", "tuning"));
        try
        {
            var result = PassiveTreeImportRunner.RunSelfHealing(_store, searchStart);

            Assert.Equal(PassiveTreeImportStatus.TreeNotFound, result.Status);
            Assert.Equal(0, _store.GetTreeCatalogRevision());
        }
        finally
        {
            Directory.Delete(searchStart, recursive: true);
        }
    }

    // ---- test 4: a corrupt tree file fails visibly, never fatally --------------------------------

    [Fact]
    public void A_corrupt_tree_file_fails_visibly_and_the_store_still_boots()
    {
        var searchStart = _dir + "-tree4";
        var generatedDir = Path.Combine(searchStart, "data", "generated", "passive-tree");
        Directory.CreateDirectory(generatedDir);
        File.WriteAllText(Path.Combine(generatedDir, "broken.json"), "{ this is not valid json");
        Directory.CreateDirectory(Path.Combine(searchStart, "data", "tuning"));
        File.WriteAllText(Path.Combine(searchStart, "data", "tuning", "passive-tree.v1.json"), ValidTuningFile);

        try
        {
            var result = PassiveTreeImportRunner.RunSelfHealing(_store, searchStart);

            Assert.Equal(PassiveTreeImportStatus.Failed, result.Status);
            Assert.NotNull(result.Detail);
            // A caught exception, not an unhandled one reaching the caller — the whole point of the
            // try/catch matching SeedImportRunner.RunSelfHealing's own contract.
            Assert.Equal(0, _store.GetTreeCatalogRevision());
        }
        finally
        {
            Directory.Delete(searchStart, recursive: true);
        }
    }

    // ---- test 5: a second launch does not re-import ----------------------------------------------

    [Fact]
    public void A_second_launch_does_not_reimport_and_the_revision_holds()
    {
        var searchStart = MakeValidCorpus(_dir + "-tree5", "might");
        try
        {
            var first = PassiveTreeImportRunner.RunSelfHealing(_store, searchStart);
            Assert.Equal(PassiveTreeImportStatus.Imported, first.Status);
            var revisionAfterFirst = _store.GetTreeCatalogRevision();
            Assert.True(revisionAfterFirst > 0);

            var second = PassiveTreeImportRunner.RunSelfHealing(_store, searchStart);

            Assert.Equal(PassiveTreeImportStatus.AlreadyCurrent, second.Status);
            // AlreadyCurrent never opens an import at all — direct proof nothing was re-read, not just
            // that the revision happens to match.
            Assert.Null(second.Outcome);
            Assert.Equal(revisionAfterFirst, _store.GetTreeCatalogRevision());
        }
        finally
        {
            Directory.Delete(searchStart, recursive: true);
        }
    }

    // ---- test 6: independent of the atom-content self-heal ---------------------------------------

    [Fact]
    public void The_tree_catalog_import_is_independent_of_the_atom_content_self_heal()
    {
        // A search root with BOTH a real atom seed tree (gk-data/packs/fusion/data/seed/atoms) AND a real generated tree
        // catalog (gk-data/packs/fusion/data/generated/passive-tree) — proves the two importers run independently, neither
        // gating the other, matching this codebase's own established "a lint, never a gate" pattern
        // for boot-time content checks.
        var searchStart = MakeValidCorpus(_dir + "-tree6", "might");
        var atomsDir = Path.Combine(searchStart, "data", "seed", "atoms");
        Directory.CreateDirectory(atomsDir);
        File.WriteAllText(Path.Combine(atomsDir, "vitality.json"), """
            {
              "schemaVersion": 1, "kind": "atom",
              "entries": [ { "kind": "stat.modify", "family": "atom.selfheal-vitality", "tier": 1,
                            "name": "Self-Heal Vitality I",
                            "params": { "channel": "maxHp", "op": "flat", "amount": 45 } } ]
            }
            """);
        try
        {
            var atomResult = SeedImportRunner.RunSelfHealing(_store, searchStart);
            var treeResult = PassiveTreeImportRunner.RunSelfHealing(_store, searchStart);

            Assert.Equal(SeedImportStatus.Imported, atomResult.Status);
            Assert.Equal(PassiveTreeImportStatus.Imported, treeResult.Status);
            Assert.True(_store.GetCatalogRevision() > 0);
            Assert.True(_store.GetTreeCatalogRevision() > 0);
        }
        finally
        {
            Directory.Delete(searchStart, recursive: true);
        }
    }
}
