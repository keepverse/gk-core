using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FusionRpg.Core.Narrative.Vocabulary;
using Xunit;

namespace FusionRpg.Core.Tests.Narrative.Vocabulary;

/// <summary>
/// npc-story-events `narrative-vocabulary` NR1.3 (`spec-narrative-vocabulary.md` §1): the character-side
/// catalogs and the doctrine catalog. The three character vocabularies are narrative-seed's, so their
/// members are compared against the FILE; `doctrines.v1.json` IS this program's file and is read from
/// `gk-data/packs/fusion/data/seed/narrative/_registry/`. The two "no second vocabulary" checks are what this task can prove —
/// the reads themselves belong to `relation-ledger` (NR2.13) and `character-registry` (NR3.1).
/// </summary>
[Trait("VerificationId", "core.narrative")]
public class CharacterRegistryTests
{
    static readonly string FixtureDir = NarrativeFixtureFiles.RegistryDir();

    static string[] Keys(string file, string vocabulary) =>
        JsonDocument.Parse(NarrativeFixtureFiles.Text(file)).RootElement
            .GetProperty(vocabulary).EnumerateObject().Select(p => p.Name).ToArray();

    static void Configure() => NarrativeRegistryHub.Configure(FixtureDir);

    static string RolesDoc(string allegiance) => """
        {
          "schemaVersion": 1,
          "registryVersion": 1,
          "roles": { "wanderer": { "description": "d", "negative": "n", "allegiance": "ALLEGIANCE" } },
          "leads": { "lead_summoner": "player" }
        }
        """.Replace("ALLEGIANCE", allegiance);

    static string VoicesDoc(string body) =>
        "{ \"schemaVersion\": 1, \"registryVersion\": 1, \"voices\": { " + body + " } }";

    static string LineContextsDoc(string keyedOn) => """
        {
          "schemaVersion": 1,
          "registryVersion": 1,
          "lineContexts": { "greet": { "description": "d", "negative": "n", "keyedOn": "KEYEDON" } }
        }
        """.Replace("KEYEDON", keyedOn);

    // ---- members equal their file ----------------------------------------------------------------

    [Fact]
    public void Members_equal_their_file_for_every_character_vocabulary()
    {
        Configure();

        Assert.Equal(Keys("roles.v1.json", "roles"), NarrativeRoleCatalog.All.Select(x => x.Id));
        Assert.Equal(Keys("voices.v1.json", "voices"), VoiceRegisterCatalog.All.Select(x => x.Id));
        Assert.Equal(Keys("line-contexts.v1.json", "lineContexts"), LineContextCatalog.All.Select(x => x.Id));

        // The leads block is the file's own, not a list here: a lead is not one of the roles.
        var leads = JsonDocument.Parse(NarrativeFixtureFiles.Text("roles.v1.json")).RootElement.GetProperty("leads");
        Assert.Equal(leads.EnumerateObject().Select(p => p.Name), NarrativeRoleCatalog.Leads.Keys);
        foreach (var lead in leads.EnumerateObject())
            Assert.Equal(lead.Value.GetString(), NarrativeRoleCatalog.Leads[lead.Name]);
    }

    [Fact]
    public void Voted_lists_carry_none_and_closed_attributes()
    {
        Configure();

        // Role, voice and line context are voted fields, so each list must be able to say "none fits".
        Assert.True(NarrativeRoleCatalog.IsKnown("none"));
        Assert.True(VoiceRegisterCatalog.IsKnown("none"));
        Assert.True(LineContextCatalog.IsKnown("none"));

        var allegiances = new[] { "player", "ally", "independent", "antagonist" };
        foreach (var role in NarrativeRoleCatalog.All)
        {
            if (role.Id == "none") Assert.Null(role.Allegiance);
            else Assert.Contains(role.Allegiance, allegiances);
        }
        foreach (var lead in NarrativeRoleCatalog.Leads.Values) Assert.Contains(lead, allegiances);

        var keyedOn = new[] { "relation", "personal-history", "world-fact", "outing" };
        foreach (var context in LineContextCatalog.All)
        {
            if (context.Id == "none") Assert.Null(context.KeyedOn);
            else Assert.Contains(context.KeyedOn, keyedOn);
        }

        // A register names its authored exemplar; `none` names none.
        Assert.All(VoiceRegisterCatalog.All.Where(x => x.Id != "none"), row => Assert.EndsWith(".json", row.Exemplar));
        Assert.Null(VoiceRegisterCatalog.Get("none").Exemplar);
    }

    // ---- the one committed file this task ships --------------------------------------------------

    [Fact]
    public void The_committed_doctrine_file_parses_and_configures()
    {
        var path = Path.Combine(NarrativeFixtureFiles.RepoRoot(), "data", "seed", "narrative", "_registry", "doctrines.v1.json");
        Assert.True(File.Exists(path), path);

        var rows = DoctrineCatalog.Parse(File.ReadAllText(path));
        DoctrineCatalog.Validate(rows);
        DoctrineCatalog.Configure(rows);

        // The file's ROWS are counter-doctrine's reviewed vocabulary and NR6.1's own test compares them
        // against the spec's table; what this test guards is that the committed file parses, validates and
        // configures, and that an unknown id is still refused. (Until NR6.1 the file was empty — the
        // emptiness was never the property, only that ship's state.)
        Assert.NotEmpty(rows);
        Assert.False(DoctrineCatalog.IsKnown("none"));
        Assert.False(DoctrineCatalog.IsKnown("doctrine.anything"));
        Assert.Throws<ArgumentException>(() => DoctrineCatalog.Get("doctrine.anything"));
    }

    [Fact]
    public void An_empty_doctrine_list_is_a_legal_configured_catalog()
    {
        // The SHAPE property this task proved, kept without the stale premise: an empty row set is a legal
        // configured catalog, never a missing-registry rejection, and every id is then unknown.
        DoctrineCatalog.Configure(Array.Empty<DoctrineDef>());

        Assert.Empty(DoctrineCatalog.All);
        Assert.False(DoctrineCatalog.IsKnown("ward.fire"));
        Assert.Throws<ArgumentException>(() => DoctrineCatalog.Get("ward.fire"));
    }

    // ---- no second vocabulary (this task's own claim) --------------------------------------------

    [Fact]
    public void No_second_personality_vocabulary_is_declared()
    {
        // `CreaturePersonality` IS the personality vocabulary (Creatures/Contracts/ContractPolicy.cs:18);
        // the narrative module reads it and never re-declares it.
        Assert.Equal("FusionRpg.Core.Creatures.Contracts",
            typeof(FusionRpg.Core.Creatures.Contracts.CreaturePersonality).Namespace);
        Assert.Equal(5, Enum.GetValues(typeof(FusionRpg.Core.Creatures.Contracts.CreaturePersonality)).Length);
        Assert.DoesNotContain(VocabularyTypes(), name => name.Contains("Personality", StringComparison.Ordinal));
    }

    [Fact]
    public void No_second_relation_ladder_is_declared()
    {
        // The one relation ladder is the dungeon disposition registry (R4) — read by `relation-ledger`
        // (NR2.13). This module declares no disposition type, no band list and no ladder of its own.
        Assert.Equal("FusionRpg.Core.Dungeon.Registry",
            typeof(FusionRpg.Core.Dungeon.Registry.DispositionCatalog).Namespace);
        Assert.DoesNotContain(VocabularyTypes(), name =>
            name.Contains("Disposition", StringComparison.Ordinal) ||
            name.Contains("RelationBand", StringComparison.Ordinal));
    }

    static string[] VocabularyTypes() => typeof(HostKindCatalog).Assembly.GetTypes()
        .Where(t => t.Namespace == typeof(HostKindCatalog).Namespace && t.IsPublic)
        .Select(t => t.Name)
        .ToArray();

    // ---- rejections, never defaults --------------------------------------------------------------

    [Fact]
    public void An_unknown_allegiance_rejects_naming_file_and_key()
    {
        var (rows, leads) = NarrativeRoleCatalog.Parse(RolesDoc("neutral"));
        var ex = Assert.Throws<NarrativeVocabularyRejection>(() => NarrativeRoleCatalog.Validate(rows, leads));
        Assert.Contains("roles.v1.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("neutral", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_voted_list_without_none_rejects()
    {
        var rows = VoiceRegisterCatalog.Parse(VoicesDoc("\"grim\": { \"description\": \"d\", \"negative\": \"n\" }"));
        var ex = Assert.Throws<NarrativeVocabularyRejection>(() => VoiceRegisterCatalog.Validate(rows));
        Assert.Contains("voices.v1.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("none", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_keyed_on_rejects_naming_file_and_key()
    {
        var rows = LineContextCatalog.Parse(LineContextsDoc("mood"));
        var ex = Assert.Throws<NarrativeVocabularyRejection>(() => LineContextCatalog.Validate(rows));
        Assert.Contains("line-contexts.v1.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("mood", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_none_row_carrying_an_attribute_rejects()
    {
        var (rows, leads) = NarrativeRoleCatalog.Parse(RolesDoc("player").Replace("\"wanderer\"", "\"none\""));
        var ex = Assert.Throws<NarrativeVocabularyRejection>(() => NarrativeRoleCatalog.Validate(rows, leads));
        Assert.Contains("none", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_id_reads_fail_rather_than_defaulting()
    {
        Configure();

        Assert.False(NarrativeRoleCatalog.IsKnown("merchant"));
        Assert.Throws<ArgumentException>(() => NarrativeRoleCatalog.Get("merchant"));
        Assert.False(VoiceRegisterCatalog.IsKnown("singsong"));
        Assert.Throws<ArgumentException>(() => VoiceRegisterCatalog.Get("singsong"));
        Assert.False(LineContextCatalog.IsKnown("sneer"));
        Assert.Throws<ArgumentException>(() => LineContextCatalog.Get("sneer"));
    }

    // ---- a read before Configure throws; Configure is the only writer -----------------------------

    [Fact]
    public void A_read_before_configure_throws()
    {
        var probes = new (Type Type, string[] CacheFields)[]
        {
            (typeof(NarrativeRoleCatalog), new[] { "_all", "_byId", "_leads" }),
            (typeof(VoiceRegisterCatalog), new[] { "_all", "_byId" }),
            (typeof(LineContextCatalog), new[] { "_all", "_byId" }),
            (typeof(DoctrineCatalog), new[] { "_all", "_byId" }),
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
                    Assert.Throws<InvalidOperationException>(() => ReadAll(probe.Type));
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
        if (catalog == typeof(NarrativeRoleCatalog)) { _ = NarrativeRoleCatalog.All; return; }
        if (catalog == typeof(VoiceRegisterCatalog)) { _ = VoiceRegisterCatalog.All; return; }
        if (catalog == typeof(LineContextCatalog)) { _ = LineContextCatalog.All; return; }
        if (catalog == typeof(DoctrineCatalog)) { _ = DoctrineCatalog.All; return; }
        throw new ArgumentOutOfRangeException(nameof(catalog), catalog, "not a character-side catalog");
    }

    [Fact]
    public void No_public_writer_but_configure()
    {
        var types = new[]
        {
            typeof(NarrativeRoleCatalog), typeof(VoiceRegisterCatalog),
            typeof(LineContextCatalog), typeof(DoctrineCatalog),
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
}
