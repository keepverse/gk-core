using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.PassiveTree;

/// <summary>Task C5 — the five migration rules R1/R2/R3/R4/R6 as executable properties, plus the
/// retirement write path (spec-tree-catalog.md §4, spec-tree-state.md §4). R5 already has its own
/// home in `TreeCatalogImportTests.cs` (task C4) and is untouched here.</summary>
public class TreeCatalogMigrationTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public TreeCatalogMigrationTests()
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

    static string TreeJson(string treeId, int catalogVersion, int n0BudgetShareMilli, long n0KMicro,
                            bool includeN1 = false)
    {
        var n1 = includeN1 ? $$"""
            ,
            {
              "id": "skill.{{treeId}}-off-t1-n1",
              "branch": "off",
              "tier": 1,
              "nodeKey": "n1",
              "prereqNodeIds": [],
              "nodeClass": "magnitude",
              "affixIds": ["affix.b"],
              "budgetShareMilli": 12,
              "atoms": [
                { "kindId": "stat.modify", "attachPoint": "Stat", "channelId": "atk", "op": "flat",
                  "trigger": null, "whenJson": null, "kMicro": 999, "scaleAxis": "PTheta",
                  "unitClass": "GameUnits", "soulCurveId": null }
              ],
              "excludeProps": [], "exclusionForm": "None", "tagsJson": null,
              "enabled": true, "retiredAtRevision": null
            }
            """ : "";

        return $$"""
        {
          "treeId": "{{treeId}}",
          "category": "primary",
          "gateQuantity": "aptitude.Might@Commander",
          "shapeArchetype": "broad-and-flat",
          "tiers": 10,
          "branches": 2,
          "nodesPerTier": [2,2,2,2,2,2,2,2,2,2],
          "catalogVersion": {{catalogVersion}},
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
              "budgetShareMilli": {{n0BudgetShareMilli}},
              "atoms": [
                { "kindId": "stat.modify", "attachPoint": "Stat", "channelId": "atk", "op": "flat",
                  "trigger": null, "whenJson": null, "kMicro": {{n0KMicro}}, "scaleAxis": "PTheta",
                  "unitClass": "GameUnits", "soulCurveId": null }
              ],
              "excludeProps": [], "exclusionForm": "None", "tagsJson": null,
              "enabled": true, "retiredAtRevision": null
            }{{n1}}
          ]
        }
        """;
    }

    (bool Enabled, int? RetiredAtRevision, int BudgetShareMilli)? ReadCatalogNode(string nodeId)
    {
        using var c = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT enabled, retired_at_revision, budget_share_milli FROM rpg_tree_catalog_node WHERE node_id = $id;";
        cmd.Parameters.AddWithValue("$id", nodeId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (r.GetInt64(0) != 0, r.IsDBNull(1) ? (int?)null : r.GetInt32(1), r.GetInt32(2));
    }

    long ReadAtomKMicro(string nodeId)
    {
        using var c = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT k_micro FROM rpg_tree_catalog_atom WHERE node_id = $id;";
        cmd.Parameters.AddWithValue("$id", nodeId);
        return (long)cmd.ExecuteScalar()!;
    }

    // ---- R1/R2: inserting a node changes no existing id --------------------------------------

    [Fact] // inserting_a_node_does_not_change_any_existing_id
    public void Inserting_a_node_in_a_later_import_changes_no_existing_id()
    {
        var v1 = _store.ImportTreeCatalog(new[] { TreeJson("might", 1, 18, 12345) }, Tuning());
        Assert.True(v1.Ok, string.Join("; ", v1.Refusals));

        var v2 = _store.ImportTreeCatalog(new[] { TreeJson("might", 1, 18, 12345, includeN1: true) }, Tuning());
        Assert.True(v2.Ok, string.Join("; ", v2.Refusals));

        var n0 = ReadCatalogNode("skill.might-off-t1-n0");
        Assert.NotNull(n0);
        Assert.True(n0!.Value.Enabled);
        Assert.Null(n0.Value.RetiredAtRevision);
        Assert.Equal(18, n0.Value.BudgetShareMilli); // byte-identical, not just "still present"

        var n1 = ReadCatalogNode("skill.might-off-t1-n1"); // the newly inserted node also lands, live
        Assert.NotNull(n1);
        Assert.True(n1!.Value.Enabled);
    }

    // ---- R1/R2: a retired node keeps its row, greyed, never deleted -------------------------

    [Fact] // a_retired_node_keeps_its_id_and_is_never_reissued (retirement write path half)
    public void A_node_dropped_from_a_later_corpus_is_retired_in_place_not_deleted()
    {
        var v1 = _store.ImportTreeCatalog(new[] { TreeJson("might", 1, 18, 12345) }, Tuning());
        Assert.True(v1.Ok, string.Join("; ", v1.Refusals));

        // v2's corpus is a DIFFERENT tree entirely -- "might" (and its node) is dropped.
        var v2 = _store.ImportTreeCatalog(new[] { TreeJson("fortitude", 1, 20, 5555) }, Tuning());
        Assert.True(v2.Ok, string.Join("; ", v2.Refusals));
        Assert.Equal(2, v2.Revision);

        var row = ReadCatalogNode("skill.might-off-t1-n0");
        Assert.NotNull(row); // the row survives -- C4's old DELETE-then-reinsert would have wiped it
        Assert.False(row!.Value.Enabled);
        Assert.Equal(2, row.Value.RetiredAtRevision); // stamped with the revision that dropped it
        Assert.Equal(18, row.Value.BudgetShareMilli); // its content is untouched, not zeroed

        // Its atoms row also survives -- "what this node used to grant" is still readable, greyed.
        Assert.Equal(12345, ReadAtomKMicro("skill.might-off-t1-n0"));
    }

    [Fact] // a retirement is stamped ONCE, at the revision that first dropped it -- never re-stamped
    public void A_retired_node_keeps_its_original_retirement_revision_across_further_imports()
    {
        _store.ImportTreeCatalog(new[] { TreeJson("might", 1, 18, 12345) }, Tuning());
        _store.ImportTreeCatalog(new[] { TreeJson("fortitude", 1, 20, 5555) }, Tuning()); // retires might @ rev 2
        _store.ImportTreeCatalog(new[] { TreeJson("fortitude", 1, 20, 5555) }, Tuning()); // rev 3, still no "might"

        var row = ReadCatalogNode("skill.might-off-t1-n0");
        Assert.Equal(2, row!.Value.RetiredAtRevision); // NOT bumped to 3
    }

    // ---- R1/R2: a retired id must never be reissued -----------------------------------------

    [Fact] // a_retired_node_keeps_its_id_and_is_never_reissued (reissue-refusal half)
    public void A_retired_node_id_is_never_reissued_even_with_different_content()
    {
        _store.ImportTreeCatalog(new[] { TreeJson("might", 1, 18, 12345) }, Tuning());
        var retiring = _store.ImportTreeCatalog(new[] { TreeJson("fortitude", 1, 20, 5555) }, Tuning());
        Assert.Equal(2, retiring.Revision);

        // A THIRD import tries to bring "skill.might-off-t1-n0" back, with different content.
        var reissue = _store.ImportTreeCatalog(new[] { TreeJson("might", 1, 99, 77777) }, Tuning());

        Assert.False(reissue.Ok);
        Assert.Contains(reissue.Refusals, r => r.Contains("skill.might-off-t1-n0", StringComparison.Ordinal));
        Assert.Equal(2, _store.GetTreeCatalogRevision()); // refused import never advances the revision

        var row = ReadCatalogNode("skill.might-off-t1-n0");
        Assert.Equal(18, row!.Value.BudgetShareMilli); // the retired row is untouched by the refused attempt
    }

    // ---- R6: a magnitude retune changes no id and migrates no per-actor row ------------------

    [Fact] // retuning_a_magnitude_does_not_change_any_id
    public void Retuning_a_magnitude_changes_no_id_and_migrates_no_per_actor_row()
    {
        _store.ImportTreeCatalog(new[] { TreeJson("might", 1, 18, 12345) }, Tuning());
        _store.SaveTreeNodeState(FusionRpg.Core.Stats.Aptitudes.AllocationScope.Commander, "player:1",
            new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 3 });

        // Same tree, same node id, DIFFERENT coefficients -- a pure content retune.
        var outcome = _store.ImportTreeCatalog(new[] { TreeJson("might", 1, 25, 98765) }, Tuning());

        Assert.True(outcome.Ok, string.Join("; ", outcome.Refusals));

        var row = ReadCatalogNode("skill.might-off-t1-n0");
        Assert.True(row!.Value.Enabled); // still live, same id
        Assert.Equal(25, row.Value.BudgetShareMilli); // new content took effect
        Assert.Equal(98765, ReadAtomKMicro("skill.might-off-t1-n0"));

        // The per-actor row is completely untouched -- soul_level is exactly what it was before.
        var loaded = _store.LoadTreeState(FusionRpg.Core.Stats.Aptitudes.AllocationScope.Commander, "player:1");
        Assert.Equal(3, loaded["skill.might-off-t1-n0"]);
    }

    // ---- R6: the "classes.v2.json trap" -- filename vN must equal catalogVersion -------------

    [Fact] // filename_version_equals_catalogVersion_field
    public void A_mismatched_filename_version_refuses_the_import_naming_the_file()
    {
        var outcome = _store.ImportTreeCatalogFiles(
            new[] { ("might.v2.json", TreeJson("might", catalogVersion: 1, 18, 12345)) }, Tuning());

        Assert.False(outcome.Ok);
        Assert.Contains(outcome.Refusals, r => r.Contains("might.v2.json", StringComparison.Ordinal));
        Assert.Equal(0, _store.GetTreeCatalogRevision());
    }

    [Fact]
    public void A_matching_filename_version_is_accepted()
    {
        var outcome = _store.ImportTreeCatalogFiles(
            new[] { ("might.v1.json", TreeJson("might", catalogVersion: 1, 18, 12345)) }, Tuning());

        Assert.True(outcome.Ok, string.Join("; ", outcome.Refusals));
        Assert.Equal(1, outcome.Revision);
    }

    [Fact] // "where applicable" -- a file name with no vN token at all is never refused for that alone
    public void A_filename_with_no_version_token_is_never_refused_for_that_alone()
    {
        var outcome = _store.ImportTreeCatalogFiles(
            new[] { ("might.json", TreeJson("might", catalogVersion: 1, 18, 12345)) }, Tuning());

        Assert.True(outcome.Ok, string.Join("; ", outcome.Refusals));
    }
}
