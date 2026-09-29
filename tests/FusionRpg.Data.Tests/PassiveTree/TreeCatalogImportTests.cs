using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.PassiveTree;

/// <summary>Task C4 — `RpgStore.ImportTreeCatalog` (spec-tree-catalog.md §6). All-or-nothing, bumps
/// `catalog_revision` exactly once, R5's batched-report refusal.</summary>
public class TreeCatalogImportTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public TreeCatalogImportTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static PassiveTreeTuning Tuning() => new(
        SchemaVersion: 1, Version: 1,
        TierLadder: new TierLadderTuning(5),
        Budget: new BudgetTuning(1000, 500),
        TreeShareMilli: 1000, TreeBudgetMilli: 1000,
        Potency: new PotencyTuning(182, 1, new long[] { 46, 91, 137, 182 }),
        Mechanism: new MechanismTuning(0, 1000),
        Archetype: new ArchetypeTuning(6000),
        Exclusion: new ExclusionTuning(20),
        ArchetypeAssignment: "ordinal-round-robin",
        DesignTarget: new DesignTargetTuning(92),
        Concentration: new ConcentrationTuning(1200, 500),
        SoulTrack: new SoulTrackTuning(1000),
        UnlockCost: new UnlockCostTuning(5, 2),
        Respec: new RespecTuning(50, 500),
        GateCounters: new GateCountersTuning(23, 23, 4, 4, 5000, null));

    static string ValidTreeJson(string treeId = "might", int node1BudgetShareMilli = 18) => $$"""
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
          "budgetShareMilli": {{node1BudgetShareMilli}},
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
              "unitClass": "GameUnits",
              "soulCurveId": null
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

    const string InvalidTreeJson = """
    {
      "treeId": "broken",
      "category": "not-a-real-category",
      "gateQuantity": "aptitude.Broken@Commander",
      "shapeArchetype": "broad-and-flat",
      "tiers": 10,
      "branches": 2,
      "nodesPerTier": [2,2,2,2,2,2,2,2,2,2],
      "catalogVersion": 1,
      "enabled": true,
      "nodes": []
    }
    """;

    [Fact]
    public void A_valid_single_tree_import_succeeds_and_bumps_the_revision_exactly_once()
    {
        Assert.Equal(0, _store.GetTreeCatalogRevision());

        var outcome = _store.ImportTreeCatalog(new[] { ValidTreeJson() }, Tuning());

        Assert.True(outcome.Ok, string.Join("; ", outcome.Refusals));
        Assert.Equal(1, outcome.TreesImported);
        Assert.Equal(1, outcome.Revision);
        Assert.Equal(1, _store.GetTreeCatalogRevision());
    }

    [Fact]
    public void A_second_valid_import_bumps_the_revision_again_exactly_once()
    {
        _store.ImportTreeCatalog(new[] { ValidTreeJson() }, Tuning());
        var second = _store.ImportTreeCatalog(new[] { ValidTreeJson() }, Tuning());

        Assert.True(second.Ok, string.Join("; ", second.Refusals));
        Assert.Equal(2, second.Revision);
        Assert.Equal(2, _store.GetTreeCatalogRevision());
    }

    [Fact] // "Import is all-or-nothing... a partial failure leaves the revision unchanged"
    public void A_content_refusal_leaves_the_revision_unchanged_and_names_the_offender()
    {
        var before = _store.GetTreeCatalogRevision();

        var outcome = _store.ImportTreeCatalog(new[] { InvalidTreeJson }, Tuning());

        Assert.False(outcome.Ok);
        Assert.NotEmpty(outcome.Refusals);
        Assert.Contains(outcome.Refusals, r => r.Contains("not-a-real-category", StringComparison.Ordinal));
        Assert.Equal(before, _store.GetTreeCatalogRevision());
        Assert.Equal(before, outcome.Revision);
    }

    [Fact] // "a partial failure leaves the revision unchanged" -- one good tree + one bad tree refuses BOTH
    public void One_bad_tree_in_a_multi_tree_batch_refuses_the_WHOLE_import_not_just_the_bad_one()
    {
        var outcome = _store.ImportTreeCatalog(
            new[] { ValidTreeJson("might"), InvalidTreeJson }, Tuning());

        Assert.False(outcome.Ok);
        Assert.Equal(0, _store.GetTreeCatalogRevision());
        // The GOOD tree must not have been written either -- all-or-nothing over the whole corpus.
        var secondImport = _store.ImportTreeCatalog(new[] { ValidTreeJson("might") }, Tuning());
        Assert.True(secondImport.Ok);
        Assert.Equal(1, secondImport.Revision); // if the first (refused) import had partially written,
                                                  // this would be revision 2, not 1
    }

    [Fact] // R5: "every id no catalog revision has ever had fails the import with every offender named"
    public void An_existing_allocation_naming_an_unknown_id_refuses_the_import_naming_every_offender()
    {
        _store.ImportTreeCatalog(new[] { ValidTreeJson() }, Tuning());
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:1",
            new Dictionary<string, long>
            {
                ["skill.might-off-t1-n0"] = 0,   // a REAL id from the imported catalog
                ["skill.ghost-off-t1-n0"] = 0,   // an id NO catalog revision has ever had
                ["skill.phantom-off-t1-n0"] = 0, // a second one -- both must be named in ONE report
            });

        var outcome = _store.ImportTreeCatalog(new[] { ValidTreeJson() }, Tuning());

        Assert.False(outcome.Ok);
        Assert.Contains(outcome.Refusals, r => r.Contains("skill.ghost-off-t1-n0", StringComparison.Ordinal));
        Assert.Contains(outcome.Refusals, r => r.Contains("skill.phantom-off-t1-n0", StringComparison.Ordinal));
        Assert.Equal(1, _store.GetTreeCatalogRevision()); // unchanged from the first successful import
    }

    [Fact] // "every actor stays loadable" -- a refused import must never touch rpg_tree_node_state
    public void A_refused_import_never_touches_existing_allocations_every_actor_stays_loadable()
    {
        _store.ImportTreeCatalog(new[] { ValidTreeJson() }, Tuning());
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:1",
            new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 3 });

        _store.ImportTreeCatalog(new[] { InvalidTreeJson }, Tuning()); // refused

        var loaded = _store.LoadTreeState(AllocationScope.Commander, "player:1");
        Assert.Equal(3, loaded["skill.might-off-t1-n0"]);
    }

    [Fact] // R5's forward-compat: a node RETIRED from the current corpus is still a KNOWN id, not unknown
    public void A_node_retired_from_the_current_corpus_is_still_a_known_id_not_an_unknown_one()
    {
        // First import establishes "skill.might-off-t1-n0" as a known id and an actor owns it.
        _store.ImportTreeCatalog(new[] { ValidTreeJson() }, Tuning());
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:1",
            new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 });

        // A DIFFERENT tree entirely re-imports on its own -- "might" is not part of this corpus
        // anymore (simulating retirement), but it was accumulated as a known id by the first import,
        // so the second import must NOT refuse for "skill.might-off-t1-n0" being unknown.
        var outcome = _store.ImportTreeCatalog(new[] { ValidTreeJson("fortitude") }, Tuning());

        Assert.True(outcome.Ok, string.Join("; ", outcome.Refusals));
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => _store.ImportTreeCatalog(null!, Tuning()));
        Assert.Throws<ArgumentNullException>(() => _store.ImportTreeCatalog(Array.Empty<string>(), null!));
    }
}
