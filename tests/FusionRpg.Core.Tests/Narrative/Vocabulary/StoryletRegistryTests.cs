using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FusionRpg.Core.Narrative.Vocabulary;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Narrative.Vocabulary;

/// <summary>Fixture paths for the narrative registries. The real files are narrative-seed's
/// `gk-data/packs/fusion/data/seed/narrative/_registry/**` (plan §8.1 defers the commit that reads them); until then the runtime
/// loader is proven against `gk-core/tests/fixtures/narrative/_registry/`, which carries the row shape of
/// `narrative-seed/spec-storylet-vocab.md` §2.</summary>
static class NarrativeFixtureFiles
{
    public static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    public static string RegistryDir() => Path.Combine(RepoRoot(), "tests", "fixtures", "narrative", "_registry");

    public static string Text(string name) => File.ReadAllText(Path.Combine(RegistryDir(), name));
}

/// <summary>
/// npc-story-events `narrative-vocabulary` NR1.2 (`spec-narrative-vocabulary.md` §1, §2, §5): the
/// storylet-side catalogs and the one loader. Members are compared against the FILE, never a literal list —
/// the seed side authors these — and the only literal pinned here is the runtime's own host-kind set, whose
/// 16 members are places that exist in code. Every rejection case is a document built in memory, so no test
/// writes a file.
/// </summary>
[Trait("VerificationId", "core.narrative")]
public class StoryletRegistryTests
{
    static readonly string Dir = NarrativeFixtureFiles.RegistryDir();

    static void Configure() => NarrativeRegistryHub.Configure(Dir);

    static string[] Keys(string file, string vocabulary) =>
        JsonDocument.Parse(NarrativeFixtureFiles.Text(file)).RootElement
            .GetProperty(vocabulary).EnumerateObject().Select(p => p.Name).ToArray();

    static string[] NestedKeys(string file, string vocabulary, string block) =>
        JsonDocument.Parse(NarrativeFixtureFiles.Text(file)).RootElement
            .GetProperty(vocabulary).GetProperty(block).EnumerateObject().Select(p => p.Name).ToArray();

    static string HostKindsDoc(string clock) => """
        {
          "schemaVersion": 1,
          "registryVersion": 1,
          "hostKinds": {
            "delve.curio": {
              "description": "d", "negative": "n", "place": "delve", "roomKind": "curio",
              "admits": ["curio"], "climateNeutral": false, "climateSource": "room",
              "climates": ["none"], "clock": "CLOCK"
            }
          }
        }
        """.Replace("CLOCK", clock);

    static string ConditionsDoc(string leaf, string proposed) => """
        {
          "schemaVersion": 1,
          "registryVersion": 1,
          "proposedLeaves": ["PROPOSED"],
          "conditions": {
            "danger-band-is": {
              "description": "d", "negative": "n", "argFamily": "dangerBand",
              "usableIn": ["slot"], "leaf": "LEAF"
            }
          }
        }
        """.Replace("LEAF", leaf).Replace("PROPOSED", proposed);

    static string ConsequenceDoc(string param) => """
        {
          "schemaVersion": 1,
          "registryVersion": 1,
          "consequenceKinds": {
            "relation.shift": {
              "description": "d", "negative": "n", "params": ["PARAM"],
              "refForms": ["role:<roleId>"], "routesTo": "the relation ledger"
            }
          }
        }
        """.Replace("PARAM", param);

    // ---- one file per vocabulary, read by both sides ------------------------------------------------

    [Fact]
    public void Members_equal_their_file_for_every_seed_owned_registry()
    {
        Configure();

        Assert.Equal(Keys("host-kinds.v1.json", "hostKinds"), HostKindCatalog.All.Select(x => x.Id));
        Assert.Equal(Keys("choice-kinds.v1.json", "choiceKinds"), ChoiceKindCatalog.All.Select(x => x.Id));
        Assert.Equal(Keys("consequence-kinds.v1.json", "consequenceKinds"), ConsequenceKindCatalog.All.Select(x => x.Id));
        Assert.Equal(Keys("conditions.v1.json", "conditions"), ConditionCatalog.All.Select(x => x.Id));
        // teaches is planner-only and its FILE ORDER is the teaching order, so this equality is a contract
        // (arc-shapes §5 orders the spine's chapters by it), not a membership check.
        Assert.Equal(Keys("teaches.v1.json", "teaches"), TeachesCatalog.All.Select(x => x.Id));
        Assert.Equal(NestedKeys("role-tags.v1.json", "roleTags", "roleKinds"), RoleTagCatalog.RoleKinds.Select(x => x.Id));
        Assert.Equal(NestedKeys("role-tags.v1.json", "roleTags", "requireFamilies"), RoleTagCatalog.RequireFamilies.Select(x => x.Id));

        // The file's own proposedLeaves block, read by the test, is the other half of the leaf contract.
        Assert.Equal(
            JsonDocument.Parse(NarrativeFixtureFiles.Text("conditions.v1.json")).RootElement
                .GetProperty("proposedLeaves").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            ConditionCatalog.ProposedLeaves);
    }

    [Fact]
    public void Host_kinds_are_the_sixteen_places_the_runtime_owns()
    {
        Configure();

        // Pinned because each member is a PLACE THAT EXISTS IN CODE (spec §2), in §2's own order: the seven
        // Delve room kinds, the six world slots plus the petition, the expedition return and the homeworld
        // hub. A new member is a reviewed change to §2, not a seed edit.
        Assert.Equal(PinnedHostKinds, HostKindCatalog.All.Select(x => x.Id).ToArray());

        // §2's clock column: every host has a clock, the Delve counts on the room clock, every world host on
        // the world turn clock, the expedition return on its collect, the homeworld on its return.
        foreach (var host in HostKindCatalog.All)
            Assert.True(Enum.IsDefined(typeof(HostClockKind), HostKindCatalog.ClockOf(host.Id)), host.Id);
        Assert.Equal(HostClockKind.DelveRoom, HostKindCatalog.ClockOf("delve.rest"));
        Assert.Equal(HostClockKind.WorldTurn, HostKindCatalog.ClockOf("world.petition"));
        Assert.Equal(HostClockKind.ExpeditionCollect, HostKindCatalog.ClockOf("expedition.return"));
        Assert.Equal(HostClockKind.SanctumReturn, HostKindCatalog.ClockOf("sanctum.hub"));
    }

    static readonly string[] PinnedHostKinds =
    {
        "delve.curio", "delve.shrine", "delve.trap", "delve.merchant", "delve.wild", "delve.rest", "delve.unknown",
        "world.shrine", "world.anomaly", "world.tear", "world.vault", "world.market", "world.wildland",
        "world.petition", "expedition.return", "sanctum.hub",
    };

    [Fact]
    public void No_catalog_exists_for_the_retired_sector_climates()
    {
        Configure();

        // R20 retired `sector-climates.v1.json`: a world storylet reads the sector's own WorldSector.Climate,
        // and no narrative registry mirrors it.
        var vocabularyTypes = typeof(HostKindCatalog).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(HostKindCatalog).Namespace && t.IsPublic)
            .Select(t => t.Name);
        Assert.DoesNotContain(vocabularyTypes, name => name.Contains("SectorClimate", StringComparison.Ordinal));

        foreach (var constant in typeof(NarrativeRegistryHub)
                     .GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.IsLiteral && f.FieldType == typeof(string)))
            Assert.DoesNotContain("sector-climates", (string)constant.GetRawConstantValue()!, StringComparison.Ordinal);
    }

    // ---- a read before Configure throws; Configure is the only writer -------------------------------

    [Fact]
    public void A_read_before_configure_throws()
    {
        // The catalogs are process-wide static content caches (spec §5), so the unconfigured state is only
        // observable with the cache cleared. Clearing it by reflection and restoring it through the real
        // writer keeps this test order-independent instead of depending on which test ran first.
        var probes = new (Type Type, string[] CacheFields)[]
        {
            (typeof(ChoiceKindCatalog), new[] { "_all", "_byId" }),
            (typeof(ConsequenceKindCatalog), new[] { "_all", "_byId" }),
            (typeof(ConditionCatalog), new[] { "_all", "_byId", "_proposedLeaves" }),
            (typeof(RoleTagCatalog), new[] { "_roleKinds", "_families" }),
            (typeof(TeachesCatalog), new[] { "_all" }),
            (typeof(HostKindCatalog), new[] { "_all", "_byId" }),
        };

        Configure();
        try
        {
            foreach (var probe in probes)
            {
                var fields = probe.CacheFields
                    .Select(name => probe.Type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static))
                    .ToArray();
                Assert.All(fields, field => Assert.NotNull(field));
                var originals = fields.Select(field => field!.GetValue(null)).ToArray();
                try
                {
                    foreach (var field in fields) field!.SetValue(null, null);
                    var type = probe.Type;
                    Assert.Throws<InvalidOperationException>(() => ReadAll(type));
                }
                finally
                {
                    for (var i = 0; i < fields.Length; i++) fields[i]!.SetValue(null, originals[i]);
                }
            }
        }
        finally
        {
            Configure();
        }
    }

    static void ReadAll(Type catalog)
    {
        if (catalog == typeof(ChoiceKindCatalog)) { _ = ChoiceKindCatalog.All; return; }
        if (catalog == typeof(ConsequenceKindCatalog)) { _ = ConsequenceKindCatalog.All; return; }
        if (catalog == typeof(ConditionCatalog)) { _ = ConditionCatalog.All; return; }
        if (catalog == typeof(RoleTagCatalog)) { _ = RoleTagCatalog.RoleKinds; return; }
        if (catalog == typeof(TeachesCatalog)) { _ = TeachesCatalog.All; return; }
        if (catalog == typeof(HostKindCatalog)) { _ = HostKindCatalog.All; return; }
        throw new ArgumentOutOfRangeException(nameof(catalog), catalog, "not a narrative catalog");
    }

    [Fact]
    public void No_public_writer_but_configure()
    {
        var types = new[]
        {
            typeof(ChoiceKindCatalog), typeof(ConsequenceKindCatalog), typeof(ConditionCatalog),
            typeof(RoleTagCatalog), typeof(TeachesCatalog), typeof(HostKindCatalog),
        };

        foreach (var type in types)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                Assert.True(field.IsInitOnly || field.IsLiteral, $"{type.Name}.{field.Name} is a public writable static field.");
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
                Assert.False(property.CanWrite, $"{type.Name}.{property.Name} is a public writer.");

            var writeish = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(method => method.Name)
                .Where(name => name is "Configure" or "Reset" or "Clear" or "Set" or "Update" or "Add" or "Remove")
                .Distinct();
            Assert.Equal(new[] { "Configure" }, writeish);
        }
    }

    // ---- rejections, never defaults (spec §5) -------------------------------------------------------

    [Fact]
    public void A_missing_registry_file_rejects_naming_the_file()
    {
        var missing = Path.Combine(Dir, "no-such-directory");
        var ex = Assert.Throws<NarrativeVocabularyRejection>(() => NarrativeRegistryHub.Configure(missing));
        Assert.Contains("host-kinds.v1.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_row_key_rejects_naming_file_and_key()
    {
        var doc = HostKindsDoc("delve.room").Replace("\"admits\": [\"curio\"],", string.Empty);
        var ex = Assert.Throws<NarrativeVocabularyRejection>(() => HostKindCatalog.Parse(doc));
        Assert.Contains("host-kinds.v1.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("admits", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_row_key_rejects_naming_file_and_key()
    {
        var doc = HostKindsDoc("delve.room").Replace("\"description\": \"d\",", "\"description\": \"d\", \"weight\": 5,");
        var ex = Assert.Throws<NarrativeVocabularyRejection>(() => HostKindCatalog.Parse(doc));
        Assert.Contains("host-kinds.v1.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("weight", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clock_outside_host_clock_kind_rejects_naming_file_and_key()
    {
        var ex = Assert.Throws<NarrativeVocabularyRejection>(() => HostKindCatalog.Parse(HostKindsDoc("delve.clock")));
        Assert.Contains("host-kinds.v1.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("delve.clock", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_condition_leaf_that_is_neither_built_nor_proposed_rejects()
    {
        // Parse and Validate are the load path's two steps (the `DispositionCatalog` shape): Parse is
        // structural, Validate is the semantic half. The hub runs both.
        var (rows, proposed) = ConditionCatalog.ParseDocument(ConditionsDoc("NopeIs", "RelationBandAtMost"));
        var ex = Assert.Throws<NarrativeVocabularyRejection>(() => ConditionCatalog.Validate(rows, proposed));
        Assert.Contains("conditions.v1.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NopeIs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_proposed_leaf_the_runtime_has_landed_rejects()
    {
        // `BandIs` is a built LeafId today, so a file still listing it as proposed is told to move.
        var (rows, proposed) = ConditionCatalog.ParseDocument(ConditionsDoc("BandIs", "BandIs"));
        var ex = Assert.Throws<NarrativeVocabularyRejection>(() => ConditionCatalog.Validate(rows, proposed));
        Assert.Contains("conditions.v1.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BandIs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_relation_shift_param_outside_the_ledgers_fact_kinds_rejects()
    {
        var rows = ConsequenceKindCatalog.Parse(ConsequenceDoc("admired"));
        var ex = Assert.Throws<NarrativeVocabularyRejection>(() => ConsequenceKindCatalog.Validate(rows));
        Assert.Contains("consequence-kinds.v1.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("relation.shift", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_id_reads_fail_rather_than_defaulting()
    {
        Configure();

        Assert.False(ChoiceKindCatalog.IsKnown("bribe"));
        Assert.Throws<ArgumentException>(() => ChoiceKindCatalog.Get("bribe"));
        Assert.False(HostKindCatalog.IsKnown("world.nowhere"));
        Assert.Throws<ArgumentException>(() => HostKindCatalog.Get("world.nowhere"));
        Assert.Throws<ArgumentException>(() => HostKindCatalog.ClockOf("world.nowhere"));
        Assert.False(TeachesCatalog.IsKnown("algebra"));
        Assert.Throws<ArgumentException>(() => TeachesCatalog.Get("algebra"));
        Assert.False(ConditionCatalog.IsKnown("mood-is"));
        Assert.False(ConsequenceKindCatalog.IsKnown("reward.grant"));
        Assert.False(RoleTagCatalog.IsKnownFamily("temperament"));
    }

    [Fact]
    public void Every_row_carries_a_description_and_a_negative_clause()
    {
        Configure();

        Assert.All(ChoiceKindCatalog.All, row => Assert.False(string.IsNullOrWhiteSpace(row.Negative)));
        Assert.All(ConsequenceKindCatalog.All, row => Assert.False(string.IsNullOrWhiteSpace(row.Negative)));
        Assert.All(ConditionCatalog.All, row => Assert.False(string.IsNullOrWhiteSpace(row.Negative)));
        Assert.All(HostKindCatalog.All, row => Assert.False(string.IsNullOrWhiteSpace(row.Negative)));
        Assert.All(TeachesCatalog.All, row => Assert.False(string.IsNullOrWhiteSpace(row.Negative)));
        Assert.All(TeachesCatalog.All, row => Assert.False(string.IsNullOrWhiteSpace(row.TeachingLine)));
    }
}
