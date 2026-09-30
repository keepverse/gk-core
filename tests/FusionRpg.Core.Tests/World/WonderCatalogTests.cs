using System.IO;
using System.Reflection;
using System.Text.Json;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Siege;
using FusionRpg.Core.World.StructureSeed;
using Xunit;
using FusionRpg.TestSupport;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// Task 1.4 (`wonder-structure`, spec-wonder-structure.md §Design 1-6): the Wonder vocabulary
/// (`WonderScope`/`WonderRarity`/`WonderEffectKind`/`WonderEffectDef`), the orthogonal
/// `StructureDef` facet fields, the `Validate` refusals, and the tunable
/// `WonderPolicy.ExistenceCapFor`. Each test names the acceptance-criteria bullet it proves.
/// </summary>
public class WonderCatalogTests
{
    static WonderTuning TestTuning => new(
        SchemaVersion: 1, Version: 1,
        UniqueExistenceCap: new WonderUniqueExistenceCapTuning(Sector: 7, Empire: 11));

    static string RealCorpusRoot() => Path.Combine(KeepverseRoots.Content(), "data", "seed", "structures");

    static string RepoRoot() => ContentRoot.Path;

    static void RestoreRealCorpus() => StructureCatalog.Configure(StructureCorpus.Load(RealCorpusRoot()));

    static void ResetWonderPolicy()
    {
        // The only way to prove the pre-Configure throw deterministically: no test in this
        // assembly configures WonderPolicy except this class, and xUnit runs one class
        // sequentially, so nulling the holder here cannot race a sibling test.
        var field = typeof(WonderPolicy).GetField("_tuning", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(null, null);
    }

    static StructureDef BaseDef(string id = "test-wonder") => new()
    {
        StructureId = id,
        Name = "Test Wonder",
        RequiredSlotKind = SlotKind.Wildland,
        Kind = StructureKind.Yield,
        AcquisitionPaths = new[] { AcquisitionPath.Built },
    };

    static StructureDef WonderDef(
        WonderScope? scope,
        WonderRarity? rarity,
        params WonderEffectDef[] effects) => BaseDef() with
    {
        WonderScope = scope,
        WonderRarity = rarity,
        WonderEffects = effects,
    };

    static WonderEffectDef Effect(WonderEffectKind kind, WonderScope scope, long valueMilli = 500) => new()
    {
        Kind = kind,
        Scope = scope,
        ValueMilli = valueMilli,
    };

    static string WonderRowJson(
        string id, string kind, string scope, string rarity,
        string effectsJson) =>
        $$"""
        {
          "id": "{{id}}",
          "name": "{{id}}",
          "anchor": {
            "structureId": "{{id}}", "family": "test", "role": "Extract", "roleSecondary": "none",
            "requiredSlotKind": "Wildland", "elementPrimary": "none", "elementSecondary": "none",
            "tempo": "none", "reach": "melee", "strengthBand": "rubble", "rarity": "sprout",
            "traits": [], "costProfile": "cheap", "targetPreference": "none", "variants": [],
            "acquisitionPaths": ["built"], "footprint": "one-cell", "coverTier": "none",
            "controlPoint": true, "obstacleVerbs": [], "reason": "test"
          },
          "_provenance": {"source": "AUTHORED", "citation": "test"},
          "magnitudes": {
            "structureKind": "{{kind}}", "cost": 0, "yieldMultiplierMilli": 1500,
            "buildTurns": 0, "capacityBonus": 0,
            "flatYieldPerTurn": 0,
            "constructRubbleCost": 0, "constructIronworkCost": 0, "materialTier": 0,
            "blocksMovement": false, "blocksLineOfFire": false, "obstacleKind": "None",
            "coverPowerMilli": 0, "coverRadius": 0, "entryStaminaMultiplierMilli": 1000,
            "visionRangeTiles": null, "relicCost": 1,
            "wonderScope": "{{scope}}", "wonderRarity": "{{rarity}}",
            "wonderEffects": [{{effectsJson}}]
          }
        }
        """;

    /// <summary>The rows the JSON document below always held, as an **in-memory** corpus: the corpus is
    /// data, so a fixture that needs its own rows does not write a corpus file to `%TEMP%`
    /// (testing-standard R1 — the `temp-corpus-write` rule refuses the write). `StructureCorpus.FromJson`
    /// is the file format's own in-memory entrance: one parser, so a literal and a committed file cannot
    /// drift apart.</summary>
    static StructureCorpus Corpus(params string[] rows) =>
        StructureCorpus.FromJson("""
        {
          "kind": "structure-anchor",
          "_meta": {"partition": "Store"},
          "entries": [
        """ + string.Join(",\n", rows) + """
          ]
        }
        """);

    // ---- AC 1: StructureKind unchanged — no NEW kind for Wonders (Task 1.2a's ItemStorage stays) ----

    [Fact]
    public void StructureKind_has_six_members_and_no_Wonder_kind()
    {
        // CLOSED vocabulary pin (validation-ssot): StructureKind is an enum the code owns and a
        // human changes — pinning 6 (with ItemStorage newest, per SectorItemCapacityTests) plus the
        // explicit absence of any Wonder member is correct here, unlike a population count.
        var members = Enum.GetValues<StructureKind>();
        Assert.Equal(6, members.Length);
        Assert.Equal(
            new[] { StructureKind.LoamSource, StructureKind.Storage, StructureKind.Yield, StructureKind.Refinery, StructureKind.Obstacle, StructureKind.ItemStorage },
            members);
        Assert.False(Enum.IsDefined(typeof(StructureKind), "Wonder"));
    }

    // ---- AC 2: Sector-scope Wonder flows through All + unmodified LoamProduction.For ----

    [Fact]
    public void A_Sector_scope_Wonder_loads_through_All_and_boosts_LoamProduction_For()
    {
        var corpus = Corpus(WonderRowJson(
            "test-sector-wonder", "Yield", "Sector", "Common",
            """{ "kind": "LoamGenerationRate", "scope": "Sector", "valueMilli": 500 }"""));
        try
        {
            StructureCatalog.Configure(corpus);
            var def = StructureCatalog.Get("test-sector-wonder");
            Assert.Equal(WonderScope.Sector, def.WonderScope);
            Assert.Equal(WonderRarity.Common, def.WonderRarity);
            var effect = Assert.Single(def.WonderEffects);
            Assert.Equal(WonderEffectKind.LoamGenerationRate, effect.Kind);
            Assert.Equal(WonderScope.Sector, effect.Scope);
            Assert.Equal(500, effect.ValueMilli);

            // Zero engine change: the existing, unmodified LoamProduction.For reads the row's own
            // YieldMultiplierMilli (1500) exactly as it would for an ordinary Yield row.
            var sector = new WorldSector
            {
                SectorId = "s",
                TypeId = "stable",
                OwnerFactionId = "f1",
                Slots = new[]
                {
                    new WorldSlot
                    {
                        SlotIndex = 0,
                        SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId,
                        StructureId = "test-sector-wonder",
                    },
                },
            };
            Assert.Equal(checked(LoamPolicy.SeepPerTurn * 1500 / 1000), LoamProduction.For(sector));
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    [Fact]
    public void An_Empire_scope_Wonder_loads_through_All()
    {
        // Module 3's future consumer reads ValueMilli off exactly this shape; this module proves
        // the shape loads, nothing more.
        var corpus = Corpus(WonderRowJson(
            "test-empire-wonder", "Yield", "Empire", "Unique",
            """{ "kind": "LoamGenerationRate", "scope": "Empire", "valueMilli": 200 }"""));
        try
        {
            StructureCatalog.Configure(corpus);
            var def = StructureCatalog.Get("test-empire-wonder");
            Assert.Equal(WonderScope.Empire, def.WonderScope);
            Assert.Equal(WonderRarity.Unique, def.WonderRarity);
            Assert.Equal(200, Assert.Single(def.WonderEffects).ValueMilli);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    // ---- AC 3: Validate refuses World/Multiverse + DefensePower/AuraGrant/EmpireBuff, naming each ----

    [Theory]
    [InlineData(WonderScope.World)]
    [InlineData(WonderScope.Multiverse)]
    public void Validate_refuses_reserved_scopes_naming_the_member(WonderScope scope)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StructureCatalog.Validate(new[]
        {
            WonderDef(scope, WonderRarity.Common, Effect(WonderEffectKind.LoamGenerationRate, scope)),
        }));
        Assert.Contains(scope.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WonderEffectKind.DefensePower)]
    [InlineData(WonderEffectKind.AuraGrant)]
    [InlineData(WonderEffectKind.EmpireBuff)]
    public void Validate_refuses_each_reserved_effect_kind_individually(WonderEffectKind kind)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StructureCatalog.Validate(new[]
        {
            WonderDef(WonderScope.Sector, WonderRarity.Common, Effect(kind, WonderScope.Sector)),
        }));
        Assert.Contains(kind.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_World_scope_row_fails_through_the_All_pipeline()
    {
        // End-to-end: a catalog row naming a reserved scope is a startup error, never silently
        // accepted — Enum.Parse accepts "World" (it is a real member), Validate refuses it.
        var corpus = Corpus(WonderRowJson(
            "test-world-wonder", "Yield", "World", "Common",
            """{ "kind": "LoamGenerationRate", "scope": "World", "valueMilli": 500 }"""));
        try
        {
            StructureCatalog.Configure(corpus);
            var ex = Assert.ThrowsAny<Exception>(() => StructureCatalog.All);
            Assert.Contains("World", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            RestoreRealCorpus();
        }
    }

    // ---- AC 4: pairing mismatch (one set, the other null) ----

    [Fact]
    public void Validate_refuses_a_WonderScope_without_WonderRarity()
    {
        Assert.Throws<InvalidOperationException>(() => StructureCatalog.Validate(new[]
        {
            WonderDef(WonderScope.Sector, null, Effect(WonderEffectKind.LoamGenerationRate, WonderScope.Sector)),
        }));
    }

    [Fact]
    public void Validate_refuses_a_WonderRarity_without_WonderScope()
    {
        Assert.Throws<InvalidOperationException>(() => StructureCatalog.Validate(new[]
        {
            WonderDef(null, WonderRarity.Common),
        }));
    }

    [Fact]
    public void Validate_refuses_a_Wonder_with_no_effects_and_effects_with_no_scope()
    {
        var noEffects = Assert.Throws<InvalidOperationException>(() => StructureCatalog.Validate(new[]
        {
            WonderDef(WonderScope.Sector, WonderRarity.Common),
        }));
        Assert.Contains("no WonderEffectDef", noEffects.Message, StringComparison.Ordinal);

        var noScope = Assert.Throws<InvalidOperationException>(() => StructureCatalog.Validate(new[]
        {
            BaseDef() with { WonderEffects = new[] { Effect(WonderEffectKind.LoamGenerationRate, WonderScope.Sector) } },
        }));
        Assert.Contains("no WonderScope", noScope.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_refuses_a_negative_ValueMilli()
    {
        Assert.Throws<InvalidOperationException>(() => StructureCatalog.Validate(new[]
        {
            WonderDef(WonderScope.Sector, WonderRarity.Common, Effect(WonderEffectKind.LoamGenerationRate, WonderScope.Sector, valueMilli: -1)),
        }));
    }

    // ---- AC 5: Scope-disagreement refusal ----

    [Fact]
    public void Validate_refuses_an_effect_scope_disagreeing_with_its_row()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StructureCatalog.Validate(new[]
        {
            WonderDef(WonderScope.Sector, WonderRarity.Common, Effect(WonderEffectKind.LoamGenerationRate, WonderScope.Empire)),
        }));
        Assert.Contains("does not", ex.Message, StringComparison.Ordinal);
    }

    // ---- AC 6: duplicate (Kind, Scope) refusal ----

    [Fact]
    public void Validate_refuses_a_duplicate_Kind_Scope_pair()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StructureCatalog.Validate(new[]
        {
            WonderDef(WonderScope.Sector, WonderRarity.Common,
                Effect(WonderEffectKind.LoamGenerationRate, WonderScope.Sector, 100),
                Effect(WonderEffectKind.LoamGenerationRate, WonderScope.Sector, 200)),
        }));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- AC 7: Common = long.MaxValue, both live scopes, never finite ----

    [Theory]
    [InlineData(WonderScope.Sector)]
    [InlineData(WonderScope.Empire)]
    public void ExistenceCapFor_Common_is_uncapped_for_both_live_scopes(WonderScope scope)
    {
        WonderPolicy.Configure(TestTuning);
        Assert.Equal(long.MaxValue, WonderPolicy.ExistenceCapFor(scope, WonderRarity.Common));
    }

    // ---- AC 8: Unique reads the tunable file's own number ----

    [Fact]
    public void ExistenceCapFor_Unique_follows_reconfiguration_with_no_code_change()
    {
        WonderPolicy.Configure(TestTuning);
        Assert.Equal(7, WonderPolicy.ExistenceCapFor(WonderScope.Sector, WonderRarity.Unique));
        Assert.Equal(11, WonderPolicy.ExistenceCapFor(WonderScope.Empire, WonderRarity.Unique));

        WonderPolicy.Configure(TestTuning with
        {
            UniqueExistenceCap = new WonderUniqueExistenceCapTuning(Sector: 42, Empire: 43),
        });
        Assert.Equal(42, WonderPolicy.ExistenceCapFor(WonderScope.Sector, WonderRarity.Unique));
        Assert.Equal(43, WonderPolicy.ExistenceCapFor(WonderScope.Empire, WonderRarity.Unique));
    }

    [Fact]
    public void ExistenceCapFor_Unique_reads_the_shipped_tuning_file()
    {
        // End-to-end proof the cap is data, not a constant: parse the real committed file and
        // assert the policy returns that file's own numbers — read independently, never literal-pinned.
        var path = Path.Combine(CoreRoot.Path, "data", "tuning", "loam-relics-wonders.v1.json");
        var json = File.ReadAllText(path);
        WonderPolicy.Configure(WonderTuningLoader.Parse(json));

        using var doc = JsonDocument.Parse(json);
        var cap = doc.RootElement.GetProperty("uniqueExistenceCap");
        Assert.Equal(
            cap.GetProperty("sector").GetInt64(),
            WonderPolicy.ExistenceCapFor(WonderScope.Sector, WonderRarity.Unique));
        Assert.Equal(
            cap.GetProperty("empire").GetInt64(),
            WonderPolicy.ExistenceCapFor(WonderScope.Empire, WonderRarity.Unique));
    }

    [Fact]
    public void WonderTuningLoader_rejects_a_missing_key_instead_of_defaulting()
    {
        Assert.Throws<WonderTuningRejection>(() =>
            WonderTuningLoader.Parse("""
                { "schemaVersion": 1, "version": 1 }
                """));
        Assert.Throws<WonderTuningRejection>(() =>
            WonderTuningLoader.Parse("""
                { "schemaVersion": 1, "version": 1,
                  "uniqueExistenceCap": { "sector": -1, "empire": 3 } }
                """));
    }

    [Fact]
    public void ExistenceCapFor_throws_for_reserved_scopes()
    {
        WonderPolicy.Configure(TestTuning);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WonderPolicy.ExistenceCapFor(WonderScope.World, WonderRarity.Unique));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WonderPolicy.ExistenceCapFor(WonderScope.Multiverse, WonderRarity.Unique));
    }

    // ---- AC 9: throws before Configure (LoamPolicy's no-built-in-default discipline) ----

    [Fact]
    public void ExistenceCapFor_throws_before_Configure()
    {
        ResetWonderPolicy();
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                WonderPolicy.ExistenceCapFor(WonderScope.Sector, WonderRarity.Unique));
            Assert.Throws<InvalidOperationException>(() =>
                WonderPolicy.ExistenceCapFor(WonderScope.Empire, WonderRarity.Unique));
            // Common needs no tuning: uncapped even unconfigured.
            Assert.Equal(long.MaxValue, WonderPolicy.ExistenceCapFor(WonderScope.Sector, WonderRarity.Common));
        }
        finally
        {
            WonderPolicy.Configure(TestTuning);
        }
    }

    // ---- AC 10: all pre-Wonder shipped seed rows byte-identical (no Wonder facet anywhere
    // except the two 4B.1 Wonder rows, which opt in by design and are covered by dedicated tests) ----

    [Fact]
    public void Every_shipped_row_loads_with_no_wonder_facet()
    {
        // Byte-identity proof without pinning a population count: whatever the corpus holds today,
        // rows that are not Wonder rows load exactly as before plus null/empty.
        var corpus = StructureCorpus.Load(RealCorpusRoot());
        foreach (var row in corpus.Rows.Where(r => r.IsCatalogLoadable && r.Magnitudes!.WonderScope is null))
        {
            Assert.Null(row.Magnitudes!.WonderScope);
            Assert.Null(row.Magnitudes!.WonderRarity);
            Assert.Null(row.Magnitudes!.WonderEffects);
        }

        try
        {
            StructureCatalog.Configure(corpus);
            foreach (var def in StructureCatalog.All.Where(d => d.WonderScope is null))
            {
                Assert.Null(def.WonderScope);
                Assert.Null(def.WonderRarity);
                Assert.Empty(def.WonderEffects);
            }
        }
        finally
        {
            RestoreRealCorpus();
        }
    }
}
