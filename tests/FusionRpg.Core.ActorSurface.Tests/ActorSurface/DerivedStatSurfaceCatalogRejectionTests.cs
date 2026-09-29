using System.Text.Json;
using System.Text.Json.Nodes;
using FusionRpg.Core.ActorSurface;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.ActorSurface;

public sealed class DerivedStatSurfaceCatalogRejectionTests
{
    [Fact]
    public void Shipped_v2_unitClass_matches_CombatFamilyUnitClass()
    {
        var catalog = DerivedStatSurfaceCatalogLoader.Parse(ReadTuning("derived-stat-catalog.v3.json"));
        foreach (var e in catalog.Entries.Where(x => x.Expand == DerivedExpandKind.Element))
        {
            Assert.True(
                DerivedStatChannels.CombatFamilyUnitClass.TryGetValue(e.Family, out var expected),
                "CombatFamilyUnitClass missing " + e.Family);
            Assert.Equal(expected, e.UnitClass);
        }
    }

    [Fact]
    public void Parse_rejects_unknown_expand()
    {
        var json = MutateShippedEntry("combat.power", e => e["expand"] = "banana");
        var ex = Assert.Throws<ActorSurfaceCatalogRejection>(() => DerivedStatSurfaceCatalogLoader.Parse(json));
        Assert.Contains("expand", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("banana", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_rejects_unknown_compose()
    {
        var json = MutateShippedEntry("combat.power", e => e["compose"] = "NotACompose");
        var ex = Assert.Throws<ActorSurfaceCatalogRejection>(() => DerivedStatSurfaceCatalogLoader.Parse(json));
        Assert.Contains("compose", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_unknown_unitClass()
    {
        var json = MutateShippedEntry("combat.power", e => e["unitClass"] = "NotAUnit");
        var ex = Assert.Throws<ActorSurfaceCatalogRejection>(() => DerivedStatSurfaceCatalogLoader.Parse(json));
        Assert.Contains("unitClass", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_unitClass_mismatch_vs_CombatFamilyUnitClass()
    {
        var json = MutateShippedEntry("combat.reflect.rate", e => e["unitClass"] = "SigmoidPoints");
        var ex = Assert.Throws<ActorSurfaceCatalogRejection>(() => DerivedStatSurfaceCatalogLoader.Parse(json));
        Assert.Contains("CombatFamilyUnitClass", ex.Message, StringComparison.Ordinal);
        Assert.Contains("combat.reflect.rate", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_rejects_leaf_element_family()
    {
        var json = InsertEntry("""
            {
              "family": "combat.power.fire",
              "expand": "element",
              "sheetGroup": "offense",
              "compose": "FlatSum",
              "unitClass": "GameUnits",
              "displayName": { "en": "x" },
              "reading": { "en": "x" },
              "icon": "x",
              "gauge": "relative"
            }
            """);
        var ex = Assert.Throws<ActorSurfaceCatalogRejection>(() => DerivedStatSurfaceCatalogLoader.Parse(json));
        Assert.Contains("expanded element", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_leaf_resource_family()
    {
        var json = InsertEntry("""
            {
              "family": "resource.max.hp",
              "expand": "resource",
              "sheetGroup": "pools",
              "compose": "FlatSum",
              "unitClass": "GameUnits",
              "displayName": { "en": "x" },
              "reading": { "en": "x" },
              "icon": "x",
              "gauge": "relative"
            }
            """);
        var ex = Assert.Throws<ActorSurfaceCatalogRejection>(() => DerivedStatSurfaceCatalogLoader.Parse(json));
        Assert.Contains("expanded resource", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_leaf_action_category_family()
    {
        var json = InsertEntry("""
            {
              "family": "skill.cooldown.attack",
              "expand": "action-category",
              "sheetGroup": "skill",
              "compose": "FlatSum",
              "unitClass": "PerMilleRatio",
              "displayName": { "en": "x" },
              "reading": { "en": "x" },
              "icon": "x",
              "gauge": "bounded"
            }
            """);
        var ex = Assert.Throws<ActorSurfaceCatalogRejection>(() => DerivedStatSurfaceCatalogLoader.Parse(json));
        Assert.Contains("expanded action-category", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_duplicate_family()
    {
        var json = InsertEntry("""
            {
              "family": "combat.power",
              "expand": "element",
              "sheetGroup": "offense",
              "compose": "FlatSum",
              "unitClass": "GameUnits",
              "displayName": { "en": "x" },
              "reading": { "en": "x" },
              "icon": "x",
              "gauge": "relative"
            }
            """);
        var ex = Assert.Throws<ActorSurfaceCatalogRejection>(() => DerivedStatSurfaceCatalogLoader.Parse(json));
        Assert.Contains("duplicate family", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_sheetGroup_tab_mismatch()
    {
        var json = MutateShippedEntry("combat.power", e => e["sheetGroup"] = "pools");
        var ex = Assert.Throws<ActorSurfaceCatalogRejection>(() => DerivedStatSurfaceCatalogLoader.Parse(json));
        Assert.Contains("sheetGroup.tab", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_rejects_missing_combat_family()
    {
        var root = JsonNode.Parse(ReadTuning("derived-stat-catalog.v3.json"))!.AsObject();
        var arr = root["entries"]!.AsArray();
        for (var i = arr.Count - 1; i >= 0; i--)
        {
            if (arr[i]?["family"]?.GetValue<string>() == "combat.power")
                arr.RemoveAt(i);
        }

        var ex = Assert.Throws<ActorSurfaceCatalogRejection>(() => DerivedStatSurfaceCatalogLoader.Parse(root.ToJsonString()));
        Assert.Contains("combat.power", ex.Message, StringComparison.Ordinal);
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_bad_status_category_variants()
    {
        var root = JsonNode.Parse(ReadTuning("derived-stat-catalog.v3.json"))!.AsObject();
        root["statusCategoryVariants"] = new JsonArray(
            JsonNode.Parse("""{ "id": "omni", "displayName": { "en": "Omni" }, "ordinal": 0 }""")!);

        var ex = Assert.Throws<ActorSurfaceCatalogRejection>(() => DerivedStatSurfaceCatalogLoader.Parse(root.ToJsonString()));
        Assert.Contains("statusCategoryVariants", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigureAll_rejects_resource_catalog_id_drift()
    {
        var resourcesJson = ReadTuning("resource-catalog.v1.json");
        var root = JsonNode.Parse(resourcesJson)!.AsObject();
        root["entries"]!.AsArray().RemoveAt(0);

        var ex = Assert.Throws<ActorSurfaceCatalogRejection>(() =>
            ActorSurfaceCatalogHub.ConfigureAll(
                AptitudeSurfaceCatalogLoader.Parse(ReadTuning("aptitude-catalog.v1.json")),
                DerivedStatSurfaceCatalogLoader.Parse(ReadTuning("derived-stat-catalog.v3.json")),
                StatusSurfaceCatalogLoader.Parse(ReadTuning("status-catalog.v1.json")),
                ResourceSurfaceCatalogLoader.Parse(root.ToJsonString()),
                ElementSurfaceCatalogLoader.Parse(ReadTuning("element-catalog.v1.json")),
                ActorSheetSurfaceCatalogLoader.Parse(ReadTuning("actor-sheet.v1.json"))));
        Assert.Contains("ResourceIds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cook_resource_ordinals_follow_ResourceIds_and_defaults()
    {
        ActorSurfaceCatalogHub.ConfigureAll(
            AptitudeSurfaceCatalogLoader.Parse(ReadTuning("aptitude-catalog.v1.json")),
            DerivedStatSurfaceCatalogLoader.Parse(ReadTuning("derived-stat-catalog.v3.json")),
            StatusSurfaceCatalogLoader.Parse(ReadTuning("status-catalog.v1.json")),
            ResourceSurfaceCatalogLoader.Parse(ReadTuning("resource-catalog.v1.json")),
            ElementSurfaceCatalogLoader.Parse(ReadTuning("element-catalog.v1.json")),
            ActorSheetSurfaceCatalogLoader.Parse(ReadTuning("actor-sheet.v1.json")));

        var cooked = DerivedSurfaceCook.Build(null, "bogus");
        Assert.Equal("en", cooked.Lang);
        Assert.Equal("plant", cooked.Side);

        var resources = Assert.Single(cooked.Tabs, t => t.Id == "resources");
        Assert.Equal(DerivedStatChannels.ResourceIds.ToList(), resources.Variants.Select(v => v.Id).ToList());
        for (var i = 0; i < resources.Variants.Count; i++)
            Assert.Equal(i, resources.Variants[i].Ordinal);
    }

    static string MutateShippedEntry(string family, Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(ReadTuning("derived-stat-catalog.v3.json"))!.AsObject();
        var entry = root["entries"]!.AsArray()
            .Select(n => n!.AsObject())
            .Single(e => e["family"]!.GetValue<string>() == family);
        mutate(entry);
        return root.ToJsonString();
    }

    static string InsertEntry(string entryJson)
    {
        var root = JsonNode.Parse(ReadTuning("derived-stat-catalog.v3.json"))!.AsObject();
        root["entries"]!.AsArray().Insert(0, JsonNode.Parse(entryJson));
        return root.ToJsonString();
    }

    static string ReadTuning(string fileName)
    {
        var path = Path.Combine(FindRepoRoot(), "data", "tuning", fileName);
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "data", "tuning", "actor-sheet.v1.json")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root with data/tuning/actor-sheet.v1.json");
    }
}
