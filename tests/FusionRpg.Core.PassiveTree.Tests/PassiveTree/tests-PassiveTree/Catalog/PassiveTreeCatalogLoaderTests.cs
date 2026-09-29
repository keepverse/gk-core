using System.Text.Json;
using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.Catalog;

/// <summary>Task B2 — `TreeRecord`/`NodeRecord`/`NodeAtom` and the load path
/// (spec-tree-catalog.md §2.1-2.5, §3).</summary>
public class PassiveTreeCatalogLoaderTests
{
    static PassiveTreeTuning MakeTuning(int maxNodeShareMilli = 182) => new(
        SchemaVersion: 1, Version: 1,
        TierLadder: new TierLadderTuning(5),
        Budget: new BudgetTuning(1000, 500),
        TreeShareMilli: 1000, TreeBudgetMilli: 1000,
        Potency: new PotencyTuning(maxNodeShareMilli, 1, new long[] { 46, 91, 137, 182 }),
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

    /// <summary>A minimal, valid, hand-authored two-node tree — the fixture every happy-path test
    /// builds on. Node 1 is a primary-channel magnitude node; node 2 is a derived-channel
    /// mechanism node with node 1 as its prerequisite.</summary>
    static string ValidTreeJson(
        string gateQuantity = "aptitude.Might@Commander",
        int node1BudgetShareMilli = 18,
        string category = "primary") => $$"""
    {
      "treeId": "might",
      "category": "{{category}}",
      "gateQuantity": "{{gateQuantity}}",
      "shapeArchetype": "broad-and-flat",
      "tiers": 10,
      "branches": 2,
      "nodesPerTier": [2,2,2,2,2,2,2,2,2,2],
      "catalogVersion": 1,
      "enabled": true,
      "nodes": [
        {
          "id": "skill.might-off-t1-n0",
          "branch": "off",
          "tier": 1,
          "nodeKey": "n0",
          "prereqNodeIds": [],
          "nodeClass": "magnitude",
          "affixIds": ["affix.might.power1"],
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
        },
        {
          "id": "skill.might-off-t2-n0",
          "branch": "off",
          "tier": 2,
          "nodeKey": "n0",
          "prereqNodeIds": ["skill.might-off-t1-n0"],
          "nodeClass": "mechanism",
          "affixIds": ["affix.might.resist1", "affix.might.resist2"],
          "budgetShareMilli": 36,
          "atoms": [
            {
              "kindId": "status.apply",
              "attachPoint": "Status",
              "channelId": "status.resist.dot",
              "op": "increased",
              "trigger": "OnDamageTaken",
              "whenJson": null,
              "kMicro": 999,
              "scaleAxis": "Theta",
              "unitClass": "StatusPotencyPoints",
              "soulCurveId": "curve.might.resist"
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

    [Fact]
    public void The_record_round_trips_a_hand_authored_tree()
    {
        var (loaded, report) = PassiveTreeCatalogLoader.Load(ValidTreeJson(), MakeTuning());

        Assert.True(report.IsOk, string.Join("; ", report.Refusals));
        Assert.NotNull(loaded);
        Assert.Equal("might", loaded!.Tree.TreeId);
        Assert.Equal(TreeCategory.Primary, loaded.Tree.Category);
        Assert.Equal(2, loaded.Nodes.Count);

        var node1 = loaded.Nodes[0];
        Assert.Equal("skill.might-off-t1-n0", node1.NodeId);
        Assert.Equal(NodeClass.Magnitude, node1.NodeClass);
        Assert.Single(node1.AffixIds);
        Assert.Equal(18, node1.BudgetShareMilli);
        Assert.Equal(12345L, node1.Atoms[0].KMicro);
        Assert.IsType<long>(node1.Atoms[0].KMicro);
        Assert.Equal(UnitClass.GameUnits, node1.Atoms[0].UnitClass);

        var node2 = loaded.Nodes[1];
        Assert.Equal(2, node2.AffixIds.Count);
        Assert.Equal(new[] { "skill.might-off-t1-n0" }, node2.PrereqNodeIds);
        Assert.Equal(NodeClass.Mechanism, node2.NodeClass);
    }

    [Fact]
    public void WhenJson_is_carried_verbatim()
    {
        var json = """
        {
          "treeId": "might", "category": "primary", "gateQuantity": "x", "shapeArchetype": "a",
          "tiers": 10, "branches": 2, "nodesPerTier": [2], "catalogVersion": 1, "enabled": true,
          "nodes": [{
            "id": "skill.might-off-t1-n0", "branch": "off", "tier": 1, "nodeKey": "n0",
            "prereqNodeIds": [], "nodeClass": "magnitude", "affixIds": ["a.1"],
            "budgetShareMilli": 18,
            "atoms": [{
              "kindId": "stat.modify", "attachPoint": "Stat", "channelId": "atk", "op": "flat",
              "trigger": null, "whenJson": {"kind": "ActorIsKiller"}, "kMicro": 1,
              "scaleAxis": "PTheta", "unitClass": "GameUnits", "soulCurveId": null
            }],
            "excludeProps": [], "exclusionForm": "None", "tagsJson": null, "enabled": true,
            "retiredAtRevision": null
          }]
        }
        """;
        var (loaded, report) = PassiveTreeCatalogLoader.Load(json, MakeTuning());
        Assert.True(report.IsOk, string.Join("; ", report.Refusals));
        using var doc = JsonDocument.Parse(loaded!.Nodes[0].Atoms[0].WhenJson!);
        Assert.Equal("ActorIsKiller", doc.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public void A_tree_whose_gateQuantity_has_no_producer_loads_and_stays_enabled()
    {
        // D37: element_mastery / status_applied.<id> have no producer yet — the catalog never
        // disables a tree for it. No validation against a "known quantities" list happens here at
        // all; that is tree-resolve's concern (spec-tree-catalog.md §2.1).
        var (loaded, report) = PassiveTreeCatalogLoader.Load(
            ValidTreeJson(gateQuantity: "status_applied.wither", category: "status"), MakeTuning());

        Assert.True(report.IsOk, string.Join("; ", report.Refusals));
        Assert.True(loaded!.Tree.Enabled);
        Assert.Equal("status_applied.wither", loaded.Tree.GateQuantity);
    }

    [Fact]
    public void An_id_violating_the_grammar_is_refused()
    {
        var json = ValidTreeJson().Replace("skill.might-off-t1-n0", "skill.might/off.t1-n0");
        var (loaded, report) = PassiveTreeCatalogLoader.Load(json, MakeTuning());

        Assert.False(report.IsOk);
        Assert.Null(loaded);
        Assert.Contains(report.Refusals, r => r.Contains("grammar"));
    }

    [Fact]
    public void A_budget_share_above_the_ceiling_is_refused_naming_both_numbers()
    {
        var (loaded, report) = PassiveTreeCatalogLoader.Load(
            ValidTreeJson(node1BudgetShareMilli: 999), MakeTuning(maxNodeShareMilli: 182));

        Assert.False(report.IsOk);
        Assert.Null(loaded);
        var msg = Assert.Single(report.Refusals, r => r.Contains("budgetShareMilli"));
        Assert.Contains("999", msg);
        Assert.Contains("182", msg);
    }

    [Fact]
    public void A_high_kMicro_alone_never_trips_the_potency_ceiling()
    {
        // §2.5's dimensional-error correction: kMicro and budgetShareMilli are not comparable in
        // any denominator, so the check must never fire off kMicro alone — only a legitimately
        // over-budget budgetShareMilli refuses.
        var json = ValidTreeJson().Replace("\"kMicro\": 12345", "\"kMicro\": 999999999");
        var (loaded, report) = PassiveTreeCatalogLoader.Load(json, MakeTuning(maxNodeShareMilli: 182));

        Assert.True(report.IsOk, string.Join("; ", report.Refusals));
        Assert.Equal(999999999L, loaded!.Nodes[0].Atoms[0].KMicro);
    }

    [Fact]
    public void Unknown_id_rejection_happens_once_at_import_batched_not_per_node()
    {
        // Two independent defects in one document — both must appear in ONE report, proving the
        // batching (R5), not a throw-on-first-error path.
        var json = ValidTreeJson().Replace("skill.might-off-t2-n0", "skill.might/off-t2-n0")
                                  .Replace("\"budgetShareMilli\": 18", "\"budgetShareMilli\": 999");
        var (loaded, report) = PassiveTreeCatalogLoader.Load(json, MakeTuning());

        Assert.False(report.IsOk);
        Assert.Null(loaded);
        Assert.True(report.Refusals.Count >= 2, string.Join("; ", report.Refusals));
        Assert.Contains(report.Refusals, r => r.Contains("grammar"));
        Assert.Contains(report.Refusals, r => r.Contains("budgetShareMilli"));
    }

    [Fact]
    public void An_unresolvable_prereq_is_refused_naming_both_ids()
    {
        var json = ValidTreeJson().Replace(
            "\"prereqNodeIds\": [\"skill.might-off-t1-n0\"]",
            "\"prereqNodeIds\": [\"skill.might-off-t1-does-not-exist\"]");
        var (loaded, report) = PassiveTreeCatalogLoader.Load(json, MakeTuning());

        Assert.False(report.IsOk);
        var msg = Assert.Single(report.Refusals);
        Assert.Contains("skill.might-off-t2-n0", msg);
        Assert.Contains("skill.might-off-t1-does-not-exist", msg);
    }

    [Fact]
    public void A_category_token_outside_the_five_value_map_is_refused_naming_it()
    {
        var json = ValidTreeJson(category: "not-a-real-category");
        var (loaded, report) = PassiveTreeCatalogLoader.Load(json, MakeTuning());

        Assert.Contains(report.Refusals, r => r.Contains("not-a-real-category"));
    }

    /// <summary>P4.2 (§6 M3) — the load-time arm. Before P4.2 `NodeAtomOp` had no `More`, so a derived
    /// `"op": "more"` failed `Enum.TryParse` and that WAS the M3 enforcement. P4.2 added the member, so
    /// this test is what keeps the load-time refusal loud: without the explicit arm in `LoadAtom`, this
    /// row would load cleanly and then apply nothing forever (`AtomDerivedSubsystem.TryParseOp` has no
    /// "more" arm) — the exact silent no-op the load path exists to refuse.</summary>
    [Fact]
    public void Derived_atom_with_a_more_op_is_refused_by_name_at_load()
    {
        var json = MutateFirstNode(ValidTreeJson(), node =>
        {
            var atoms = node["atoms"].EnumerateArray().ToList();
            var atom = atoms[0].EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            atom["kindId"] = JsonSerializer.SerializeToElement("stat.derived");
            atom["channelId"] = JsonSerializer.SerializeToElement("combat.power.fire");
            atom["op"] = JsonSerializer.SerializeToElement("more");
            node["atoms"] = JsonSerializer.SerializeToElement(new[] { atom });
        });

        var (loaded, report) = PassiveTreeCatalogLoader.Load(json, MakeTuning());

        Assert.False(report.IsOk);
        var msg = Assert.Single(report.Refusals, r => r.Contains("more"));
        Assert.Contains("M3", msg);
        Assert.Contains("derived", msg, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The other half of P4.2: a `stat.modify` atom may carry `more`, and one that does is
    /// loaded rather than refused — the load path must agree with `AtomRowValidator.StatOps` and with
    /// the bind path, or the primary side stays broken while the derived side looks protected.</summary>
    [Fact]
    public void Primary_atom_with_a_more_op_loads_cleanly()
    {
        var json = MutateFirstNode(ValidTreeJson(), node =>
        {
            var atoms = node["atoms"].EnumerateArray().ToList();
            var atom = atoms[0].EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            atom["op"] = JsonSerializer.SerializeToElement("more");
            node["atoms"] = JsonSerializer.SerializeToElement(new[] { atom });
        });

        var (loaded, report) = PassiveTreeCatalogLoader.Load(json, MakeTuning());

        Assert.True(report.IsOk, string.Join("; ", report.Refusals));
        Assert.Equal(NodeAtomOp.More, loaded!.Nodes[0].Atoms[0].Op);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void AffixIds_outside_1_to_3_is_refused(int count)
    {
        var affixArray = "[" + string.Join(",", Enumerable.Range(0, count).Select(i => $"\"a.{i}\"")) + "]";
        var json = ValidTreeJson().Replace("[\"affix.might.power1\"]", affixArray);
        var (loaded, report) = PassiveTreeCatalogLoader.Load(json, MakeTuning());

        Assert.Contains(report.Refusals, r => r.Contains("affixIds"));
    }

    static string MutateFirstNode(string json, Action<Dictionary<string, JsonElement>> mutate)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.Clone();
        var rootDict = root.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        var nodesArray = rootDict["nodes"].EnumerateArray().ToList();
        var firstNode = nodesArray[0].EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        mutate(firstNode);
        nodesArray[0] = JsonSerializer.SerializeToElement(firstNode);
        rootDict["nodes"] = JsonSerializer.SerializeToElement(nodesArray);
        return JsonSerializer.Serialize(rootDict);
    }

    [Fact]
    public void ExclusionForm_None_with_nonempty_excludeProps_is_refused()
    {
        var json = MutateFirstNode(ValidTreeJson(), node =>
        {
            node["excludeProps"] = JsonSerializer.SerializeToElement(new[] { "conversionState:converted" });
        });
        var (loaded, report) = PassiveTreeCatalogLoader.Load(json, MakeTuning());

        Assert.Contains(report.Refusals, r => r.Contains("exclusionForm is None"));
    }

    [Fact]
    public void ExclusionForm_nonNone_with_empty_excludeProps_is_refused()
    {
        var json = MutateFirstNode(ValidTreeJson(), node =>
        {
            node["exclusionForm"] = JsonSerializer.SerializeToElement("Nullification");
        });
        var (loaded, report) = PassiveTreeCatalogLoader.Load(json, MakeTuning());

        Assert.Contains(report.Refusals, r => r.Contains("excludeProps is empty"));
    }

    [Fact]
    public void An_unregistered_channel_is_refused_never_silently_written()
    {
        var json = ValidTreeJson().Replace("\"channelId\": \"atk\"", "\"channelId\": \"not.a.real.channel\"");
        var (loaded, report) = PassiveTreeCatalogLoader.Load(json, MakeTuning());

        Assert.Contains(report.Refusals, r => r.Contains("not.a.real.channel"));
    }

    [Fact]
    public void An_unknown_atom_kind_is_refused()
    {
        var json = ValidTreeJson().Replace("\"kindId\": \"stat.modify\"", "\"kindId\": \"not.a.real.kind\"");
        var (loaded, report) = PassiveTreeCatalogLoader.Load(json, MakeTuning());

        Assert.Contains(report.Refusals, r => r.Contains("not.a.real.kind"));
    }

    [Theory]
    [InlineData("OnGranted")]
    [InlineData("OnRemoved")]
    public void A_lifecycle_trigger_is_refused_as_not_authorable(string trigger)
    {
        var json = ValidTreeJson().Replace("\"trigger\": \"OnDamageTaken\"", $"\"trigger\": \"{trigger}\"");
        var (loaded, report) = PassiveTreeCatalogLoader.Load(json, MakeTuning());

        Assert.Contains(report.Refusals, r => r.Contains(trigger) && r.Contains("authorable"));
    }

    [Theory]
    [InlineData("Milliseconds")]
    [InlineData("Count")]
    [InlineData("Flag")]
    [InlineData("LadderIndex")]
    [InlineData("AptitudePoints")]
    [InlineData("LoamUnits")]
    public void Six_refused_unit_classes_are_rejected_as_magnitude_targets(string unitClass)
    {
        var json = ValidTreeJson()
            .Replace("\"unitClass\": \"GameUnits\"", $"\"unitClass\": \"{unitClass}\"")
            .Replace("\"scaleAxis\": \"PTheta\"", "\"scaleAxis\": \"FlatPermille\""); // avoid a second, unrelated refusal
        var (loaded, report) = PassiveTreeCatalogLoader.Load(json, MakeTuning());

        Assert.Contains(report.Refusals, r => r.Contains(unitClass) && r.Contains("refused"));
    }

    [Fact]
    public void A_sigmoid_channel_carrying_PTheta_is_refused_the_silent_failure_class()
    {
        // §2.4's own worked example: SigmoidMultiplierPoints paired with PTheta is a design error
        // that fails SILENTLY at runtime (the sheet number rises, the multiplier does not) — this
        // loader catches it at IMPORT instead.
        var json = ValidTreeJson().Replace("\"unitClass\": \"GameUnits\"", "\"unitClass\": \"SigmoidMultiplierPoints\"");
        var (loaded, report) = PassiveTreeCatalogLoader.Load(json, MakeTuning());

        Assert.Contains(report.Refusals, r => r.Contains("SigmoidMultiplierPoints") && r.Contains("Theta"));
    }

    [Fact]
    public void An_IdMismatch_between_the_id_and_its_own_coordinates_is_refused_but_kept_as_authored()
    {
        var json = MutateFirstNode(ValidTreeJson(), node =>
        {
            node["tier"] = JsonSerializer.SerializeToElement(5); // id still says t1
        });
        var (loaded, report) = PassiveTreeCatalogLoader.Load(json, MakeTuning());

        Assert.Contains(report.Refusals, r => r.Contains("IdMismatch"));
    }

    // -- 2026-09-07 real-corpus finding: a dotted/underscored real treeId is not a false IdMismatch --

    [Theory]
    [InlineData("nerve.afflicted", "nerveafflicted")]
    [InlineData("charm_pulse", "charmpulse")]
    [InlineData("might", "might")]
    public void TreeSlugFor_matches_seedsmiths_own_ids_tree_slug_for_rule(string treeId, string expectedSlug)
    {
        // Pinned against the SAME five real trees `ids.tree_slug_for` (seedsmith, J1) strips a dot
        // or underscore from — both sides must agree on what "the same tree" means, or a real
        // dotted/underscored tree's own catalog refuses to import (the exact live-boot-proof finding
        // this test guards against regressing).
        Assert.Equal(expectedSlug, PassiveTreeCatalogLoader.TreeSlugFor(treeId));
    }

    [Fact]
    public void A_real_dotted_tree_id_does_not_false_positive_an_IdMismatch()
    {
        // The real live-boot-proof finding, 2026-09-07: `data/generated/passive-tree/nerve.afflicted
        // .json`'s own real node ids (minted by `ids.tree_slug_for`, which strips the dot) refused
        // to import against every one of its own 40 nodes -- `PassiveTreeCatalogLoader` compared the
        // id's own (correctly stripped) "nerveafflicted" slug against the RAW `treeId` field
        // ("nerve.afflicted") verbatim, before this fix normalized both sides the same way.
        var json = ValidTreeJson().Replace("\"treeId\": \"might\"", "\"treeId\": \"nerve.afflicted\"")
            .Replace("skill.might-off-t1-n0", "skill.nerveafflicted-off-t1-n0")
            .Replace("skill.might-off-t2-n0", "skill.nerveafflicted-off-t2-n0");
        var (loaded, report) = PassiveTreeCatalogLoader.Load(json, MakeTuning());

        Assert.DoesNotContain(report.Refusals, r => r.Contains("IdMismatch"));
        Assert.True(report.IsOk, string.Join("; ", report.Refusals));
        Assert.NotNull(loaded);
    }
}
