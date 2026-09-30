using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Narrative.Vocabulary;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Narrative.Vocabulary;

/// <summary>
/// npc-story-events `narrative-vocabulary` NR1.4 (`spec-narrative-vocabulary.md` §4, §5): the narrative
/// domain's tuning file, its pure parser and its hub. The decisive test is the leaf-path loop — for every
/// scalar key in the committed file, deleting it must make `Parse` reject naming that path — so a new key
/// is covered without editing this test. The file is read from `gk-core/data/tuning/`; nothing here writes.
/// </summary>
[Trait("VerificationId", "core.narrative")]
[Trait("Guard", "narrative")]
public class NarrativeTuningTests
{
    /// <summary>The CURRENT committed version: the highest `narrative.v{n}.json`, so a published v{n+1}
    /// (the sanctioned way to change a tuning file) moves this test with it instead of leaving it reading
    /// a superseded version.</summary>
    static string TuningPath()
    {
        var dir = Path.Combine(NarrativeFixtureFiles.RepoRoot(), "data", "tuning");
        return Directory.GetFiles(dir, "narrative.v*.json")
            .OrderByDescending(path => int.Parse(Path.GetFileNameWithoutExtension(path)[("narrative.v".Length)..]))
            .First();
    }

    static string TuningText() => File.ReadAllText(TuningPath());

    /// <summary>The registries and the one relation ladder must be configured before the tuning parser
    /// runs (it joins them, it does not copy them). The ladder is read from the dungeon registry's own
    /// committed file, which is also what proves the narrative module declares no second ladder.</summary>
    static void ConfigurePrerequisites()
    {
        NarrativeRegistryHub.Configure(NarrativeFixtureFiles.RegistryDir());
        var disposition = Path.Combine(
            KeepverseRoots.Content(), "data", "seed", "dungeon", "_registry", "disposition.v1.json");
        DispositionCatalog.Configure(DispositionCatalog.Parse(File.ReadAllText(disposition)));
    }

    static NarrativeTuning Parse() => NarrativeTuningLoader.Parse(TuningText());

    // ---- the committed file ------------------------------------------------------------------------

    [Fact]
    public void The_committed_file_parses_and_carries_the_union_of_keys()
    {
        ConfigurePrerequisites();
        var tuning = Parse();

        Assert.Equal(1, tuning.SchemaVersion);
        // The file's own published version, read from the file: the loader accepts any published version
        // (a `publish.py` bump is the sanctioned way to change this file) and never pins one.
        Assert.Equal(
            JsonNode.Parse(TuningText())!.AsObject()["version"]!.GetValue<int>(),
            tuning.Version);
        Assert.True(tuning.Version >= 1);
        Assert.Equal(1000L, tuning.SpecificityStepMilli);
        Assert.True(tuning.UnplayedFirst);
        Assert.Equal(NarrativeTuningLoader.StoryletKinds.Count, tuning.KindFrequencyBand.Count);
        Assert.Equal(HostKindCatalog.All.Count, tuning.Firing.Count);
        Assert.Equal(ConsequenceKindCatalog.RelationShiftParams.Count, tuning.ShiftByFactKind.Count);
        Assert.Equal(NarrativeRoleCatalog.All.Count(x => x.Id != "none"), tuning.BaseBandByRole.Count);
        Assert.Equal(NarrativeTuningLoader.ResidentSlots.Count, tuning.ResidentChanceMilliBySlot.Count);
        Assert.Empty(tuning.GatesLeadLevel);

        // The wave-0 union (plan D4): the six blocks the seven later specs declared, so no later module
        // has to hand-edit a committed version.
        Assert.Equal(NarrativeTuningLoader.DelveResidentRoles.Count, tuning.DelveResidentsPerRole.Count);
        Assert.Equal(NarrativeTuningLoader.QuestExpiryClocks.Count, tuning.QuestExpiry.Count);
        Assert.Equal(NarrativeTuningLoader.QuestLogClocks.Count, tuning.QuestLogRecentClosed.Count);
        Assert.Equal(NarrativeTuningLoader.FailureWindowClocks.Count, tuning.FailurePriorityWindow.Count);
        Assert.Equal(NarrativeTuningLoader.DoctrineKeys.Count, tuning.Doctrine.Count);
        Assert.Equal(NarrativeTuningLoader.ReadingsKeys.Count, tuning.Readings.Count);
        Assert.True(tuning.WorldOfferLifetimeTurns > 0);

        // A reading, not a contract: the Delve hosts always fire (the room kind already decided) and the
        // world hosts start at Stellaris' 5% + 0.5% per miss.
        Assert.Equal(1000L, tuning.Firing["delve.curio"].BaseMilli);
        Assert.Equal(50L, tuning.Firing["world.shrine"].BaseMilli);
        Assert.Equal(5L, tuning.Firing["world.shrine"].StepMilli);
    }

    [Fact]
    public void Every_leaf_path_is_required_by_name()
    {
        ConfigurePrerequisites();

        var leaves = LeafPaths(JsonNode.Parse(TuningText())!.AsObject(), new List<string>())
            // `_meta` is provenance (owner, the never-hand-edit instruction, a note), not a tunable: its
            // prose is asserted below instead of being deleted key by key.
            .Where(segments => segments[0] != "_meta")
            .ToArray();
        Assert.NotEmpty(leaves);

        var checkedCount = 0;
        foreach (var segments in leaves)
        {
            var leaf = string.Join('.', segments);
            var doc = JsonNode.Parse(TuningText())!.AsObject();
            RemoveLeaf(doc, segments);

            var ex = Assert.Throws<NarrativeVocabularyRejection>(() => NarrativeTuningLoader.Parse(doc.ToJsonString()));
            Assert.Contains(leaf, ex.Message, StringComparison.Ordinal);
            checkedCount++;
        }

        // The loop is only meaningful if it actually walked the file's keys.
        Assert.True(checkedCount >= 40, $"expected the committed file to carry >= 40 leaf paths, walked {checkedCount}");
    }

    [Fact]
    public void Meta_carries_the_never_hand_edit_instruction()
    {
        var meta = JsonNode.Parse(TuningText())!.AsObject()["_meta"]!.AsObject();
        Assert.Contains("Never hand-edit", meta["rebalance"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    // Paths are KEY SEQUENCES, never a dotted string split back apart: a registry key legitimately
    // contains a dot (`firing.world.shrine.baseMilli` is three keys, not four).
    static IEnumerable<List<string>> LeafPaths(JsonObject node, List<string> prefix)
    {
        foreach (var pair in node)
        {
            var path = new List<string>(prefix) { pair.Key };
            if (pair.Value is JsonObject child && child.Count > 0)
            {
                foreach (var leaf in LeafPaths(child, path)) yield return leaf;
            }
            else
            {
                yield return path;
            }
        }
    }

    static void RemoveLeaf(JsonObject root, IReadOnlyList<string> segments)
    {
        var node = root;
        for (var i = 0; i < segments.Count - 1; i++) node = node[segments[i]]!.AsObject();
        node.Remove(segments[^1]);
    }

    // ---- rejections, never defaults ----------------------------------------------------------------

    static string Mutate(Action<JsonObject> mutate)
    {
        var doc = JsonNode.Parse(TuningText())!.AsObject();
        mutate(doc);
        return doc.ToJsonString();
    }

    static NarrativeVocabularyRejection Rejects(string json) =>
        Assert.Throws<NarrativeVocabularyRejection>(() => NarrativeTuningLoader.Parse(json));

    [Fact]
    public void An_unknown_drop_band_rejects_naming_the_key()
    {
        ConfigurePrerequisites();
        var ex = Rejects(Mutate(doc => doc["selection"]!["kindFrequencyBand"]!["story"] = "legendary"));
        Assert.Contains("selection.kindFrequencyBand.story", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_host_kind_rejects_naming_the_key()
    {
        ConfigurePrerequisites();
        var ex = Rejects(Mutate(doc => doc["firing"]!["world.nowhere"] = new JsonObject { ["baseMilli"] = 50, ["stepMilli"] = 5 }));
        Assert.Contains("firing.world.nowhere", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clock_outside_the_three_rejects_and_delve_room_is_one()
    {
        ConfigurePrerequisites();
        // `delve.room` is absent on purpose: the Delve's own recent-cell filter IS that cooldown (§4).
        var ex = Rejects(Mutate(doc => doc["cooldown"]!["perKind"]!["delve.room"] = 1));
        Assert.Contains("cooldown.perKind.delve.room", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_lead_level_gate_rejects_naming_the_key()
    {
        ConfigurePrerequisites();
        var ex = Rejects(Mutate(doc => doc["gates"]!["leadLevel"] = new JsonObject { ["raid"] = -1 }));
        Assert.Contains("gates.leadLevel.raid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_leftover_join_rank_by_band_rejects_naming_the_key()
    {
        ConfigurePrerequisites();
        // Removed by the 2026-09-19 audit: every join starts at the contracts program's normal bind rank,
        // so a key whose every value must equal the default is not a balance number.
        var ex = Rejects(Mutate(doc => doc["relation"]!["joinRankByBand"] = new JsonObject { ["eager"] = "trusted" }));
        Assert.Contains("joinRankByBand", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_root_key_and_an_unsupported_version_reject()
    {
        ConfigurePrerequisites();
        Assert.Contains("narrative", Rejects(Mutate(doc => doc["narrative"] = new JsonObject())).Message, StringComparison.Ordinal);
        Assert.Contains("version", Rejects(Mutate(doc => doc["version"] = 0)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_disposition_id_rejects_naming_the_key()
    {
        ConfigurePrerequisites();
        var ex = Rejects(Mutate(doc => doc["relation"]!["baseBandByRole"]!["trader"] = "friendly"));
        Assert.Contains("relation.baseBandByRole.trader", ex.Message, StringComparison.Ordinal);
    }

    // ---- the hub has no default; Configure is the only writer ---------------------------------------

    [Fact]
    public void The_hub_has_no_default()
    {
        ConfigurePrerequisites();
        NarrativeTuningHub.Configure(Parse());

        var field = typeof(NarrativeTuningHub).GetField("_tuning", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var original = field!.GetValue(null);
        try
        {
            field.SetValue(null, null);
            Assert.Throws<InvalidOperationException>(() => { _ = NarrativeTuningHub.Tuning; });
        }
        finally
        {
            field.SetValue(null, original);
        }

        Assert.Equal(1000L, NarrativeTuningHub.Tuning.SpecificityStepMilli);
    }

    [Fact]
    public void No_public_writer_but_configure()
    {
        foreach (var field in typeof(NarrativeTuningHub).GetFields(BindingFlags.Public | BindingFlags.Static))
            Assert.True(field.IsInitOnly || field.IsLiteral, $"NarrativeTuningHub.{field.Name} is a public writable static field.");
        foreach (var property in typeof(NarrativeTuningHub).GetProperties(BindingFlags.Public | BindingFlags.Static))
            Assert.False(property.CanWrite, $"NarrativeTuningHub.{property.Name} is a public writer.");

        var writeish = typeof(NarrativeTuningHub).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .Where(name => name is "Configure" or "Reset" or "Clear" or "Set" or "Update" or "Add" or "Remove")
            .Distinct();
        Assert.Equal(new[] { "Configure" }, writeish);
    }

    [Fact]
    public void Milli_clock_and_score_fields_are_long_and_band_steps_are_int()
    {
        // The spec's numeric table: every *Milli, clock tick and score is long (a rate grows with n; int
        // per-mille exceeds its range at Theta 3213), a band step is int (bounded by the four-member
        // ladder), a gate threshold is int (a summoner level).
        Assert.Equal(typeof(long), typeof(NarrativeTuning).GetProperty(nameof(NarrativeTuning.SpecificityStepMilli))!.PropertyType);
        Assert.Equal(typeof(IReadOnlyDictionary<string, long>), typeof(NarrativeTuning).GetProperty(nameof(NarrativeTuning.CooldownPerStorylet))!.PropertyType);
        Assert.Equal(typeof(IReadOnlyDictionary<string, long>), typeof(NarrativeTuning).GetProperty(nameof(NarrativeTuning.CooldownPerKind))!.PropertyType);
        Assert.Equal(typeof(IReadOnlyDictionary<string, long>), typeof(NarrativeTuning).GetProperty(nameof(NarrativeTuning.CastingScore))!.PropertyType);
        Assert.Equal(typeof(IReadOnlyDictionary<string, long>), typeof(NarrativeTuning).GetProperty(nameof(NarrativeTuning.ResidentChanceMilliBySlot))!.PropertyType);
        Assert.Equal(typeof(IReadOnlyDictionary<string, long>), typeof(NarrativeTuning).GetProperty(nameof(NarrativeTuning.SaveCastPerRole))!.PropertyType);
        Assert.Equal(typeof(IReadOnlyDictionary<string, long>), typeof(NarrativeTuning).GetProperty(nameof(NarrativeTuning.GatesLeadLevel))!.PropertyType);
        Assert.Equal(typeof(IReadOnlyDictionary<string, int>), typeof(NarrativeTuning).GetProperty(nameof(NarrativeTuning.ShiftByFactKind))!.PropertyType);
        Assert.Equal(typeof(int), typeof(NarrativeTuning).GetProperty(nameof(NarrativeTuning.MaxNegativeInARow))!.PropertyType);
        Assert.Equal(typeof(long), typeof(FiringRow).GetProperty(nameof(FiringRow.BaseMilli))!.PropertyType);
        Assert.Equal(typeof(long), typeof(FiringRow).GetProperty(nameof(FiringRow.StepMilli))!.PropertyType);
    }
}
