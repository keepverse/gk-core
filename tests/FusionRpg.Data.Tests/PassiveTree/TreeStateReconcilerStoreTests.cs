using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.PassiveTree;

/// <summary>Task C9 — `RpgStore.LoadAndClassifyTreeState` (spec-tree-state.md §4). Live/retired/unknown
/// classification against the real C4 catalog tables.</summary>
public class TreeStateReconcilerStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public TreeStateReconcilerStoreTests()
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

    static string TreeJson(string treeId, bool node1Enabled) => $$"""
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
            { "kindId": "stat.modify", "attachPoint": "Stat", "channelId": "atk", "op": "flat",
              "trigger": null, "whenJson": null, "kMicro": 12345, "scaleAxis": "PTheta",
              "unitClass": "GameUnits", "soulCurveId": null }
          ],
          "excludeProps": [], "exclusionForm": "None", "tagsJson": null,
          "enabled": {{(node1Enabled ? "true" : "false")}}, "retiredAtRevision": null
        }
      ]
    }
    """;

    [Fact] // an_unknown_node_id_does_not_throw_on_actor_load
    public void An_unknown_node_id_does_not_throw_and_classifies_unknown()
    {
        _store.ImportTreeCatalog(new[] { TreeJson("might", node1Enabled: true) }, Tuning());
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:1",
            new Dictionary<string, long> { ["skill.ghost-off-t1-n0"] = 0 });

        var result = _store.LoadAndClassifyTreeState(AllocationScope.Commander, "player:1");

        var one = Assert.Single(result);
        Assert.Equal(TreeNodeStatus.Unknown, one.Status);
    }

    [Fact] // a_retired_node_loads_as_invalid_and_grants_nothing (classification half, real catalog)
    public void A_disabled_catalog_node_classifies_as_retired()
    {
        _store.ImportTreeCatalog(new[] { TreeJson("might", node1Enabled: false) }, Tuning());
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:1",
            new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 2 });

        var result = _store.LoadAndClassifyTreeState(AllocationScope.Commander, "player:1");

        var one = Assert.Single(result);
        Assert.Equal(TreeNodeStatus.Retired, one.Status);
        Assert.Equal(2, one.SoulLevel);
    }

    [Fact]
    public void A_live_catalog_node_classifies_as_live()
    {
        _store.ImportTreeCatalog(new[] { TreeJson("might", node1Enabled: true) }, Tuning());
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:1",
            new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 });

        var result = _store.LoadAndClassifyTreeState(AllocationScope.Commander, "player:1");

        Assert.Equal(TreeNodeStatus.Live, Assert.Single(result).Status);
    }

    [Fact] // "a save with one retired and one unknown id loads, and both render"
    public void A_save_with_one_retired_and_one_unknown_id_loads_and_both_classify()
    {
        _store.ImportTreeCatalog(new[] { TreeJson("might", node1Enabled: false) }, Tuning());
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:1",
            new Dictionary<string, long>
            {
                ["skill.might-off-t1-n0"] = 1,   // retired (disabled in catalog)
                ["skill.ghost-off-t1-n0"] = 0,   // unknown (never imported)
            });

        var result = _store.LoadAndClassifyTreeState(AllocationScope.Commander, "player:1");

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.NodeId == "skill.might-off-t1-n0" && r.Status == TreeNodeStatus.Retired);
        Assert.Contains(result, r => r.NodeId == "skill.ghost-off-t1-n0" && r.Status == TreeNodeStatus.Unknown);
    }

    [Fact]
    public void A_node_removed_from_the_active_corpus_but_previously_known_classifies_as_retired_not_unknown()
    {
        // First import establishes "skill.might-off-t1-n0" as known and enabled.
        _store.ImportTreeCatalog(new[] { TreeJson("might", node1Enabled: true) }, Tuning());
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:1",
            new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 });

        // A later corpus import drops "might" entirely (a different tree only) -- "might"'s node no
        // longer appears in rpg_tree_catalog_node at all, but it stays in the known-id accumulator.
        _store.ImportTreeCatalog(new[] { TreeJson("fortitude", node1Enabled: true) }, Tuning());

        var result = _store.LoadAndClassifyTreeState(AllocationScope.Commander, "player:1");

        Assert.Equal(TreeNodeStatus.Retired, Assert.Single(result).Status);
    }
}
