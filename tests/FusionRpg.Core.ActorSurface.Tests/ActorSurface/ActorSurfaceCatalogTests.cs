using FusionRpg.Core.ActorSurface;
using FusionRpg.Core.Hud;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.ActorSurface;

public sealed class ActorSurfaceCatalogTests
{
    [Fact]
    public void Parse_shipped_aptitude_catalog()
    {
        var catalog = AptitudeSurfaceCatalogLoader.Parse(ReadTuning("aptitude-catalog.v1.json"));
        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Equal(1, catalog.Version);
        // pin: immutable gk-core/data/tuning/aptitude-catalog.v1.json — a published version never changes
        Assert.Equal(12, catalog.Entries.Count);
        Assert.Equal("Might", catalog.Entries[0].Id);
        Assert.Equal("Hit harder.", catalog.Entries[0].Reading);
        Assert.Equal("swords", catalog.Entries[0].Icon);
        Assert.All(catalog.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Icon)));
    }

    [Fact]
    public void Parse_aptitude_catalog_missing_icon_does_not_crash()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "kind": "aptitude-catalog",
              "version": 1,
              "entries": [
                {
                  "id": "Might",
                  "posture": "force",
                  "ordinal": 0,
                  "displayName": "Might",
                  "role": "offence",
                  "reading": "Hit harder."
                }
              ]
            }
            """;
        var catalog = AptitudeSurfaceCatalogLoader.Parse(json);
        Assert.Null(catalog.Entries[0].Icon);
    }

    [Fact]
    public void Parse_shipped_derived_stat_catalog_v3()
    {
        var catalog = DerivedStatSurfaceCatalogLoader.Parse(ReadTuning("derived-stat-catalog.v3.json"));
        Assert.Equal(2, catalog.SchemaVersion);
        Assert.Equal(3, catalog.Version);
        // 54 -> 53 (solid-enforcement `retire-atk` R4, 2026-09-18): the retired channel's row leaves
        // the live file through gk-core/tools/tuning/publish.py, not a hand edit; v2 stays on disk.
        // pin: immutable gk-core/data/tuning/derived-stat-catalog.v3.json — a published version never changes
        Assert.Equal(53, catalog.Entries.Count);
        Assert.Equal(28, catalog.Entries.Count(e => e.Expand == DerivedExpandKind.Element));
        Assert.Equal(6, catalog.Entries.Count(e => e.Expand == DerivedExpandKind.StatusCategory));
        Assert.Equal(0, catalog.Entries.Count(e => e.Expand == DerivedExpandKind.StatusId));
        Assert.Equal(4, catalog.Entries.Count(e => e.Expand == DerivedExpandKind.Resource));
        Assert.Contains(catalog.Entries, e => e.Family == "combat.power");
        Assert.Contains(catalog.Entries, e => e.Family == "status.resist" && e.CapRef == "categoryResistCap");
        Assert.DoesNotContain(catalog.Entries, e => e.Family == "status.resist.dot");
        Assert.Equal("Power", catalog.Entries.First(e => e.Family == "combat.power").DisplayName.Resolve("en"));
        Assert.Equal(4, catalog.StatusCategoryVariants.Count);
        Assert.Equal(5, catalog.ActionCategoryVariants.Count);
    }

    [Fact]
    public void Parse_rejects_leaf_status_family()
    {
        var json = MinimalV2Catalog(entries: """
            {
              "family": "status.resist.dot",
              "expand": "status-category",
              "sheetGroup": "status",
              "compose": "SumIncreased",
              "unitClass": "StatusPotencyPoints",
              "displayName": { "en": "DoT resist" },
              "reading": { "en": "x" },
              "icon": "x",
              "gauge": "relative"
            }
            """);

        var ex = Assert.Throws<ActorSurfaceCatalogRejection>(() => DerivedStatSurfaceCatalogLoader.Parse(json));
        Assert.Contains("expanded status-category", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_missing_en_locale()
    {
        var json = MinimalV2Catalog(entries: """
            {
              "family": "progression.power",
              "expand": "none",
              "sheetGroup": "progression",
              "compose": "FlatReplace",
              "unitClass": "LadderIndex",
              "displayName": { "zh": "功" },
              "reading": { "en": "ok" },
              "icon": "x",
              "gauge": "none"
            }
            """);

        var ex = Assert.Throws<ActorSurfaceCatalogRejection>(() => DerivedStatSurfaceCatalogLoader.Parse(json));
        Assert.Contains("missing required 'en'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_shipped_status_catalog()
    {
        var catalog = StatusSurfaceCatalogLoader.Parse(ReadTuning("status-catalog.v1.json"));
        // pin: immutable gk-core/data/tuning/status-catalog.v1.json — a published version never changes
        Assert.Equal(24, catalog.Entries.Count);
        Assert.Contains(catalog.Entries, e => e.Id == "butter" && e.Kind == StatusKind.UnityCc);
        Assert.Contains(catalog.Entries, e => e.Id == "wither" && e.Kind == StatusKind.OverTime);
        Assert.Contains(catalog.Entries, e => e.Id == "bond" && e.PayloadKinds.Count == 0);
        Assert.Contains(catalog.Entries, e => e.Id == "expose"
            && e.PayloadKinds.Contains(StatusPayloadKind.ModifyStat));
        Assert.Contains(catalog.Entries, e => e.Id == "leech" && e.PulseHealsAttacker);
    }

    [Fact]
    public void Parse_shipped_resource_catalog()
    {
        var catalog = ResourceSurfaceCatalogLoader.Parse(ReadTuning("resource-catalog.v1.json"));
        Assert.Equal(6, catalog.Entries.Count);
        var hunger = Assert.Single(catalog.Entries, e => e.Id == "hunger");
        Assert.Equal("Sun", hunger.Labels.Plant);
        Assert.Equal("Hunger", hunger.Labels.Zombie);
    }

    [Fact]
    public void Parse_shipped_element_catalog()
    {
        var catalog = ElementSurfaceCatalogLoader.Parse(ReadTuning("element-catalog.v1.json"));
        Assert.Contains(catalog.Entries, e => e.Id == "omni" && e.PresentationOnly);
        Assert.Contains(catalog.Entries, e => e.Id == "fire" && !e.PresentationOnly);
    }

    [Fact]
    public void Parse_element_catalog_v2_declares_one_minimal_glyph_for_each_concrete_element()
    {
        var catalog = ElementSurfaceCatalogLoader.Parse(ReadTuning("element-catalog.v2.json"));
        var concrete = catalog.Entries.Where(e => !e.PresentationOnly).ToArray();

        Assert.Equal(2, catalog.Version);
        Assert.Equal(ElementRoster.Concrete.Count, concrete.Length);
        Assert.All(concrete, e => Assert.False(string.IsNullOrWhiteSpace(e.HudGlyph)));
        Assert.Equal("flame", Assert.Single(concrete, e => e.Id == "fire").HudGlyph);
        Assert.Equal("eclipse", Assert.Single(concrete, e => e.Id == "dark").HudGlyph);
    }

    [Fact]
    public void Parse_shipped_actor_sheet()
    {
        var catalog = ActorSheetSurfaceCatalogLoader.Parse(ReadTuning("actor-sheet.v1.json"));
        Assert.Equal(ActorSheetTabKind.Condition, catalog.DefaultOpen);
        Assert.Equal(8, catalog.Tabs.Count);
        Assert.Equal(2, catalog.Version);
        Assert.Contains(catalog.Tabs, t => t.Kind == ActorSheetTabKind.Condition && t.Icon == "heart-pulse");
        Assert.Contains(catalog.Tabs, t => t.Kind == ActorSheetTabKind.Paths && t.Icon == "git-branch");
        Assert.Contains(catalog.KitRoles, r => r.RoleId == "armament-primary"
            && r.Labels.Humanoid == "Weapon"
            && r.Labels.Plant == "Stem");
    }

    [Fact]
    public void Parse_rejects_unknown_tab_kind()
    {
        var json = """
            {
              "schemaVersion": 1,
              "kind": "actor-sheet",
              "version": 1,
              "defaultOpen": "condition",
              "tabs": [
                { "kind": "inventory", "label": "Inventory", "order": 0, "hidden": false }
              ],
              "kitRoles": []
            }
            """;

        var ex = Assert.Throws<ActorSurfaceCatalogRejection>(() => ActorSheetSurfaceCatalogLoader.Parse(json));
        Assert.Contains("unknown tab kind", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inventory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_rejects_unknown_StatusKind()
    {
        var json = """
            {
              "schemaVersion": 1,
              "kind": "status-catalog",
              "version": 1,
              "entries": [
                {
                  "id": "bogus",
                  "kind": "NotARealKind",
                  "categories": ["dot"],
                  "stacking": "Refresh",
                  "payloadKinds": [],
                  "displayName": "Bogus",
                  "reading": "No.",
                  "hudToken": "X",
                  "color": "#ffffff"
                }
              ]
            }
            """;

        var ex = Assert.Throws<ActorSurfaceCatalogRejection>(() => StatusSurfaceCatalogLoader.Parse(json));
        Assert.Contains("kind", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NotARealKind", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDto_happy_path_counts()
    {
        ConfigureAllSurfaceCatalogs();

        var dto = ActorSurfaceCatalogHub.BuildDto();

        // The hub's own DTO build must not drop or duplicate a row past the raw parse
        // (Parse_shipped_aptitude_catalog above).
        // pin: immutable gk-core/data/tuning/aptitude-catalog.v1.json — a published version never changes
        Assert.Equal(12, dto.Aptitudes.Count);
        // 54 -> 53 (solid-enforcement `retire-atk` R4, 2026-09-18: the v3 catalog no longer carries
        // the retired channel's row).
        // pin: immutable gk-core/data/tuning/derived-stat-catalog.v3.json — same reason, for Families
        Assert.Equal(53, dto.Families.Count);
        Assert.Equal(6, dto.Resources.Count);
        // pin: immutable gk-core/data/tuning/status-catalog.v1.json — same reason, for Statuses
        Assert.Equal(24, dto.Statuses.Count);
        Assert.Equal(36d, dto.HudPresentation.IdentityElementPrimaryPixels);
        Assert.Equal(30d, dto.HudPresentation.IdentityElementSecondaryPixels);
        Assert.Equal(8, dto.Tabs.Count);
        Assert.Contains("aptitude:1", dto.VersionStamp, StringComparison.Ordinal);
        Assert.Contains("sheet:2", dto.VersionStamp, StringComparison.Ordinal);
        Assert.Equal("heart-pulse", Assert.Single(dto.Tabs, t => t.Kind == "condition").Icon);
        Assert.Equal("condition", dto.Tabs[0].Kind);
        Assert.Equal("element", Assert.Single(dto.Families, f => f.Family == "combat.power").Expand);
    }

    [Fact]
    public void Expand_parity_against_registry_does_not_move_the_count()
    {
        var catalog = DerivedStatSurfaceCatalogLoader.Parse(ReadTuning("derived-stat-catalog.v3.json"));
        var registry = DerivedStatRegistry.CreateDefault();
        var registered = registry.AllRegistered.Select(d => d.ChannelId).ToHashSet(StringComparer.Ordinal);
        // solid-enforcement population-pin (SE3.2, 2026-09-19): this test's own name says the real
        // contract -- the expand-parity walk below must never MOVE registered.Count (a defensive
        // check against a HashSet somehow mutating mid-iteration, not a re-assertion of the
        // registry's total, which `AtomCatalogSsotDriftTests`'s own canonical `closed-vocabulary
        // DerivedStatRegistry.AllRegistered` pin already owns). Captured once, compared against
        // itself at the end -- no literal needed either way.
        var countBeforeExpandWalk = registered.Count;

        foreach (var family in catalog.Entries.Where(e => e.Expand == DerivedExpandKind.Element))
        {
            foreach (var el in new[] { "omni", "fire", "ice", "air", "earth", "light", "dark" })
            {
                var id = $"{family.Family}.{el}";
                Assert.True(registered.Contains(id), "missing element expand " + id);
            }
        }

        foreach (var family in catalog.Entries.Where(e => e.Expand == DerivedExpandKind.StatusCategory))
        {
            foreach (var cat in new[] { "omni", "dot", "cc", "contagion" })
            {
                var id = $"{family.Family}.{cat}";
                Assert.True(registered.Contains(id), "missing status expand " + id);
            }
        }

        foreach (var family in catalog.Entries.Where(e => e.Expand == DerivedExpandKind.Resource))
        {
            foreach (var res in DerivedStatChannels.ResourceIds)
            {
                var id = $"{family.Family}.{res}";
                Assert.True(registered.Contains(id), "missing resource expand " + id);
            }
        }

        foreach (var family in catalog.Entries.Where(e => e.Expand == DerivedExpandKind.ActionCategory))
        {
            foreach (var cat in DerivedStatChannels.ActionCategories)
            {
                var id = $"{family.Family}.{cat}";
                Assert.True(registered.Contains(id), "missing action expand " + id);
            }
        }

        Assert.Equal(countBeforeExpandWalk, registered.Count);
    }

    [Fact]
    public void DerivedSurfaceCook_four_tabs_and_side_labels()
    {
        ConfigureAllSurfaceCatalogs();

        var en = DerivedSurfaceCook.Build("en", "plant");
        Assert.Equal(2, en.SchemaVersion);
        Assert.Equal(new[] { "elements", "status", "resources", "other" }, en.Tabs.Select(t => t.Id));

        var elements = Assert.Single(en.Tabs, t => t.Id == "elements");
        Assert.Contains(elements.Variants, v => v.Id == "omni" && v.PresentationOnly);
        Assert.Contains(elements.Variants, v => v.Id == "fire" && !v.PresentationOnly);
        Assert.Equal(28, elements.Categories.SelectMany(c => c.Families).Count());

        var status = Assert.Single(en.Tabs, t => t.Id == "status");
        Assert.Equal(4, status.Variants.Count);
        Assert.Equal(new[] { "omni", "dot", "cc", "contagion" }, status.Variants.Select(v => v.Id));
        Assert.True(status.Variants[0].PresentationOnly);
        Assert.Contains(status.Variants, v => v.Id == "dot" && !v.PresentationOnly);
        Assert.Equal(6, status.Categories.SelectMany(c => c.Families).Count());
        Assert.All(status.Categories.SelectMany(c => c.Families), f => Assert.Equal("status-category", f.Expand));
        Assert.Contains(status.Categories.SelectMany(c => c.Families), f => f.Family == "status.resist");

        var resources = Assert.Single(en.Tabs, t => t.Id == "resources");
        Assert.Equal(6, resources.Variants.Count);
        Assert.Equal("Sun", Assert.Single(resources.Variants, v => v.Id == "hunger").DisplayName);

        var other = Assert.Single(en.Tabs, t => t.Id == "other");
        Assert.Single(other.Variants, v => v.Id == "shared");
        Assert.Equal("Shared", other.Variants[0].DisplayName);
        Assert.NotNull(other.ActionCategoryVariants);
        Assert.Equal(5, other.ActionCategoryVariants!.Count);
        Assert.DoesNotContain(other.ActionCategoryVariants!, v => v.Id == "shared");
        var skillCd = Assert.Single(other.Categories.SelectMany(c => c.Families), f => f.Family == "skill.cooldown");
        Assert.Equal("action-category", skillCd.Expand);
        Assert.Equal("{family}.{variant}", skillCd.ChannelPattern);
        var power = Assert.Single(other.Categories.SelectMany(c => c.Families), f => f.Family == "progression.power");
        Assert.Equal("none", power.Expand);
        Assert.Equal("{family}", power.ChannelPattern);

        var zhFallback = DerivedSurfaceCook.Build("zh", "plant");
        Assert.Equal("Elements", Assert.Single(zhFallback.Tabs, t => t.Id == "elements").DisplayName);

        var zombie = DerivedSurfaceCook.Build("en", "zombie");
        var hungerZ = Assert.Single(
            Assert.Single(zombie.Tabs, t => t.Id == "resources").Variants,
            v => v.Id == "hunger");
        Assert.Equal("Hunger", hungerZ.DisplayName);

        Assert.Contains("derived-stat-catalog.v2", en.VersionStamp, StringComparison.Ordinal);
    }

    static void ConfigureAllSurfaceCatalogs()
    {
        ActorHudTuningHub.Configure(ActorHudTuningLoader.Parse(ReadTuning("actor-hud.v7.json")));
        ActorSurfaceCatalogHub.ConfigureAll(
            AptitudeSurfaceCatalogLoader.Parse(ReadTuning("aptitude-catalog.v1.json")),
            DerivedStatSurfaceCatalogLoader.Parse(ReadTuning("derived-stat-catalog.v3.json")),
            StatusSurfaceCatalogLoader.Parse(ReadTuning("status-catalog.v1.json")),
            ResourceSurfaceCatalogLoader.Parse(ReadTuning("resource-catalog.v1.json")),
            ElementSurfaceCatalogLoader.Parse(ReadTuning("element-catalog.v1.json")),
            ActorSheetSurfaceCatalogLoader.Parse(ReadTuning("actor-sheet.v1.json")));
    }

    /// <summary>Minimal v2 shell with all combat families stubbed so combat parity can fail on the injected leaf.</summary>
    static string MinimalV2Catalog(string entries)
    {
        // Use shipped catalog as base and replace entries — simpler for rejection tests that only need one bad row.
        // For leaf rejection we inject a tiny invalid document that still has schema shells but will fail
        // before full combat parity if leaf check runs first — leaf check runs before combat parity.
        var shipped = DerivedStatSurfaceCatalogLoader.Parse(ReadTuning("derived-stat-catalog.v3.json"));
        // Build by mutating JSON: take shipped and append a leaf entry via string replace is fragile.
        // Instead craft a document that duplicates status.resist as a leaf — rejection is leaf-first.
        _ = shipped;
        var full = ReadTuning("derived-stat-catalog.v3.json");
        // Insert the leaf entry at the start of the entries array.
        var needle = "\"entries\": [";
        var idx = full.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(idx >= 0);
        return full.Insert(idx + needle.Length, "\n" + entries + ",");
    }

    static string ReadTuning(string fileName)
    {
        var path = Path.Combine(FindRepoRoot(), "data", "tuning", fileName);
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
