using System.Text.Json;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;

namespace FusionRpg.Core.ActorSurface;

/// <summary>How a family joins to live <c>channelId</c> values on <c>/sheet</c>.</summary>
public enum DerivedExpandKind
{
    None,
    Element,
    StatusCategory,
    StatusId,
    Resource,
    ActionCategory
}

public sealed record LocaleMap(IReadOnlyDictionary<string, string> Values)
{
    public string Resolve(string lang)
    {
        if (Values.TryGetValue(lang, out var hit) && !string.IsNullOrWhiteSpace(hit))
            return hit;
        return Values["en"];
    }
}

public sealed record DerivedSurfaceTabDef(
    string Id,
    LocaleMap DisplayName,
    int Order,
    DerivedExpandKind Expand);

public sealed record DerivedSheetGroupDef(
    string Id,
    string Tab,
    LocaleMap DisplayName,
    int Order);

public sealed record DerivedVariantDef(
    string Id,
    LocaleMap DisplayName,
    int Ordinal,
    bool PresentationOnly = false);

public sealed record DerivedStatSurfaceEntry(
    string Family,
    DerivedExpandKind Expand,
    string SheetGroup,
    DerivedComposeKind Compose,
    UnitClass UnitClass,
    LocaleMap DisplayName,
    LocaleMap Reading,
    string Icon,
    string Gauge,
    string? CapRef)
{
    /// <summary>Legacy alias — v1 overloaded <c>axis</c> as sheetGroup; prefer <see cref="SheetGroup"/>.</summary>
    public string Axis => SheetGroup;
}

public sealed record DerivedStatSurfaceCatalog(
    int SchemaVersion,
    int Version,
    IReadOnlyList<DerivedSurfaceTabDef> Tabs,
    IReadOnlyList<DerivedSheetGroupDef> SheetGroups,
    IReadOnlyList<DerivedStatSurfaceEntry> Entries,
    IReadOnlyList<DerivedVariantDef> StatusCategoryVariants,
    IReadOnlyList<DerivedVariantDef> ActionCategoryVariants);

/// <summary>Pure parser, no file I/O (tunables-ssot.md T7.2).</summary>
public static class DerivedStatSurfaceCatalogLoader
{
    const string Catalog = "derived-stat-catalog";

    // The three L2b status categories are referenced from StatusL2bCategory; `omni` is the deliberate
    // fourth, non-specific variant this catalog's own surface adds (presentation-only — see
    // DerivedSurfaceCook), kept as an explicit, commented addition rather than an invisible literal
    // (combat-math-dedup Task 13, D19).
    static readonly HashSet<string> StatusCategoryIds = new(StringComparer.Ordinal)
    {
        "omni", StatusL2bCategory.Dot, StatusL2bCategory.Cc, StatusL2bCategory.Contagion
    };

    static readonly HashSet<string> StatusFamilyIds = new(StringComparer.Ordinal)
    {
        "status.power",
        "status.resist",
        "status.duration",
        "status.durationReduction",
        "status.intensity",
        "status.intensityReduction"
    };

    static readonly HashSet<string> ResourceFamilyIds = new(StringComparer.Ordinal)
    {
        "resource.max",
        "resource.regen",
        "resource.efficiency",
        "resource.restore"
    };

    // ElementRoster owns the roster and its omni id (combat-math-dedup Task 13, D19): derived rather
    // than re-listed, so a seventh element cannot be added there and missed here.
    static readonly HashSet<string> ElementLeafIds =
        ElementRoster.Concrete.Select(e => e.ToElementId())
            .Append(ElementRoster.OmniId)
            .ToHashSet(StringComparer.Ordinal);

    public static DerivedStatSurfaceCatalog Parse(string json)
    {
        using var doc = ActorSurfaceJson.ParseDocument(json, Catalog);
        var root = doc.RootElement;
        var schemaVersion = ActorSurfaceJson.Int(root, "schemaVersion", "$", Catalog);
        if (schemaVersion != 2)
        {
            throw new ActorSurfaceCatalogRejection(
                $"{Catalog}: schemaVersion {schemaVersion} is not supported — require 2");
        }

        var version = ActorSurfaceJson.Int(root, "version", "$", Catalog);

        var tabs = ParseTabs(ActorSurfaceJson.Arr(root, "tabs", "$", Catalog));
        var sheetGroups = ParseSheetGroups(ActorSurfaceJson.Arr(root, "sheetGroups", "$", Catalog), tabs);
        var statusVariants = ParseVariants(
            ActorSurfaceJson.Arr(root, "statusCategoryVariants", "$", Catalog),
            "statusCategoryVariants",
            requirePresentationOnly: false);
        var actionVariants = ParseVariants(
            ActorSurfaceJson.Arr(root, "actionCategoryVariants", "$", Catalog),
            "actionCategoryVariants",
            requirePresentationOnly: false);

        if (statusVariants.Count != 4
            || !StatusCategoryIds.SetEquals(statusVariants.Select(v => v.Id)))
        {
            throw new ActorSurfaceCatalogRejection(
                $"{Catalog}: statusCategoryVariants must be exactly omni/dot/cc/contagion (L2b math vocabulary; Status rail uses status-catalog)");
        }

        if (actionVariants.Count != DerivedStatChannels.ActionCategories.Count
            || !DerivedStatChannels.ActionCategories.ToHashSet(StringComparer.Ordinal)
                .SetEquals(actionVariants.Select(v => v.Id)))
        {
            throw new ActorSurfaceCatalogRejection(
                $"{Catalog}: actionCategoryVariants must match DerivedStatChannels.ActionCategories");
        }

        var entries = ParseEntries(ActorSurfaceJson.Arr(root, "entries", "$", Catalog));
        RejectDuplicates(entries);
        RejectExpandedLeaves(entries);
        RejectSheetGroupTabMismatch(entries, sheetGroups, tabs);
        RejectCombatParity(entries);
        RejectCombatUnitClassParity(entries);
        RejectStatusParity(entries);
        RejectResourceParity(entries);
        RejectActionCategoryParity(entries);

        return new DerivedStatSurfaceCatalog(
            schemaVersion,
            version,
            tabs,
            sheetGroups,
            entries,
            statusVariants,
            actionVariants);
    }

    static IReadOnlyList<DerivedSurfaceTabDef> ParseTabs(JsonElement arr)
    {
        var list = new List<DerivedSurfaceTabDef>(arr.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new ActorSurfaceCatalogRejection($"{Catalog}: non-object '$.tabs[{i}]'");
            var path = $"tabs[{i}]";
            var id = ActorSurfaceJson.Str(el, "id", path, Catalog);
            if (!seen.Add(id))
                throw new ActorSurfaceCatalogRejection($"{Catalog}: duplicate tab id '{id}'");
            list.Add(new DerivedSurfaceTabDef(
                Id: id,
                DisplayName: ActorSurfaceJson.Locale(el, "displayName", path, Catalog),
                Order: ActorSurfaceJson.Int(el, "order", path, Catalog),
                Expand: ParseExpand(el, path)));
            i++;
        }

        var required = new[] { "elements", "status", "resources", "other" };
        foreach (var id in required)
        {
            if (!seen.Contains(id))
                throw new ActorSurfaceCatalogRejection($"{Catalog}: missing required tab '{id}'");
        }

        return list;
    }

    static IReadOnlyList<DerivedSheetGroupDef> ParseSheetGroups(
        JsonElement arr,
        IReadOnlyList<DerivedSurfaceTabDef> tabs)
    {
        var tabIds = tabs.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var list = new List<DerivedSheetGroupDef>(arr.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new ActorSurfaceCatalogRejection($"{Catalog}: non-object '$.sheetGroups[{i}]'");
            var path = $"sheetGroups[{i}]";
            var id = ActorSurfaceJson.Str(el, "id", path, Catalog);
            if (!seen.Add(id))
                throw new ActorSurfaceCatalogRejection($"{Catalog}: duplicate sheetGroup id '{id}'");
            var tab = ActorSurfaceJson.Str(el, "tab", path, Catalog);
            if (!tabIds.Contains(tab))
                throw new ActorSurfaceCatalogRejection($"{Catalog}: unknown sheetGroup.tab '{tab}' at '{path}'");
            list.Add(new DerivedSheetGroupDef(
                Id: id,
                Tab: tab,
                DisplayName: ActorSurfaceJson.Locale(el, "displayName", path, Catalog),
                Order: ActorSurfaceJson.Int(el, "order", path, Catalog)));
            i++;
        }

        return list;
    }

    static IReadOnlyList<DerivedVariantDef> ParseVariants(
        JsonElement arr,
        string key,
        bool requirePresentationOnly)
    {
        var list = new List<DerivedVariantDef>(arr.GetArrayLength());
        var i = 0;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new ActorSurfaceCatalogRejection($"{Catalog}: non-object '$.{key}[{i}]'");
            var path = $"{key}[{i}]";
            var presentationOnly = false;
            if (el.TryGetProperty("presentationOnly", out var po))
            {
                if (po.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ActorSurfaceCatalogRejection($"{Catalog}: non-boolean '{path}.presentationOnly'");
                presentationOnly = po.GetBoolean();
            }
            else if (requirePresentationOnly)
            {
                throw new ActorSurfaceCatalogRejection($"{Catalog}: missing '{path}.presentationOnly'");
            }

            list.Add(new DerivedVariantDef(
                Id: ActorSurfaceJson.Str(el, "id", path, Catalog),
                DisplayName: ActorSurfaceJson.Locale(el, "displayName", path, Catalog),
                Ordinal: ActorSurfaceJson.Int(el, "ordinal", path, Catalog),
                PresentationOnly: presentationOnly));
            i++;
        }

        return list;
    }

    static IReadOnlyList<DerivedStatSurfaceEntry> ParseEntries(JsonElement arr)
    {
        var list = new List<DerivedStatSurfaceEntry>(arr.GetArrayLength());
        var i = 0;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new ActorSurfaceCatalogRejection($"{Catalog}: non-object '$.entries[{i}]'");
            var path = $"entries[{i}]";
            list.Add(new DerivedStatSurfaceEntry(
                Family: ActorSurfaceJson.Str(el, "family", path, Catalog),
                Expand: ParseExpand(el, path),
                SheetGroup: ActorSurfaceJson.Str(el, "sheetGroup", path, Catalog),
                Compose: ActorSurfaceJson.EnumValue<DerivedComposeKind>(el, "compose", path, Catalog),
                UnitClass: ActorSurfaceJson.EnumValue<UnitClass>(el, "unitClass", path, Catalog),
                DisplayName: ActorSurfaceJson.Locale(el, "displayName", path, Catalog),
                Reading: ActorSurfaceJson.Locale(el, "reading", path, Catalog),
                Icon: ActorSurfaceJson.Str(el, "icon", path, Catalog),
                Gauge: ActorSurfaceJson.Str(el, "gauge", path, Catalog),
                CapRef: ActorSurfaceJson.OptionalStr(el, "capRef", path, Catalog)));
            i++;
        }

        return list;
    }

    static DerivedExpandKind ParseExpand(JsonElement el, string path)
    {
        var raw = ActorSurfaceJson.Str(el, "expand", path, Catalog);
        return raw switch
        {
            "none" => DerivedExpandKind.None,
            "element" => DerivedExpandKind.Element,
            "status-category" => DerivedExpandKind.StatusCategory,
            "status-id" => DerivedExpandKind.StatusId,
            "resource" => DerivedExpandKind.Resource,
            "action-category" => DerivedExpandKind.ActionCategory,
            _ => throw new ActorSurfaceCatalogRejection($"{Catalog}: unknown '{path}.expand' value '{raw}'")
        };
    }

    static void RejectDuplicates(IReadOnlyList<DerivedStatSurfaceEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (!seen.Add(e.Family))
                throw new ActorSurfaceCatalogRejection($"{Catalog}: duplicate family '{e.Family}'");
        }
    }

    static void RejectExpandedLeaves(IReadOnlyList<DerivedStatSurfaceEntry> entries)
    {
        foreach (var e in entries)
        {
            var lastDot = e.Family.LastIndexOf('.');
            if (lastDot < 0)
                continue;
            var last = e.Family[(lastDot + 1)..];
            var prefix = e.Family[..lastDot];

            if (ElementLeafIds.Contains(last) && prefix.StartsWith("combat.", StringComparison.Ordinal))
            {
                throw new ActorSurfaceCatalogRejection(
                    $"{Catalog}: family '{e.Family}' looks like an expanded element channel — author the family only");
            }

            if (StatusCategoryIds.Contains(last) && prefix.StartsWith("status.", StringComparison.Ordinal))
            {
                throw new ActorSurfaceCatalogRejection(
                    $"{Catalog}: family '{e.Family}' looks like an expanded status-category channel — author the family only");
            }

            if (DerivedStatChannels.ResourceIds.Contains(last)
                && prefix.StartsWith("resource.", StringComparison.Ordinal))
            {
                throw new ActorSurfaceCatalogRejection(
                    $"{Catalog}: family '{e.Family}' looks like an expanded resource channel — author the family only");
            }

            if (DerivedStatChannels.ActionCategories.Contains(last)
                && (prefix == DerivedStatChannels.SkillCooldownPrefix
                    || prefix == DerivedStatChannels.SkillEffectivenessPrefix))
            {
                throw new ActorSurfaceCatalogRejection(
                    $"{Catalog}: family '{e.Family}' looks like an expanded action-category channel — author the family only");
            }
        }
    }

    static void RejectSheetGroupTabMismatch(
        IReadOnlyList<DerivedStatSurfaceEntry> entries,
        IReadOnlyList<DerivedSheetGroupDef> sheetGroups,
        IReadOnlyList<DerivedSurfaceTabDef> tabs)
    {
        var groupById = sheetGroups.ToDictionary(g => g.Id, StringComparer.Ordinal);
        var tabExpand = tabs.ToDictionary(t => t.Id, t => t.Expand, StringComparer.Ordinal);

        foreach (var e in entries)
        {
            if (!groupById.TryGetValue(e.SheetGroup, out var group))
            {
                throw new ActorSurfaceCatalogRejection(
                    $"{Catalog}: family '{e.Family}' references unknown sheetGroup '{e.SheetGroup}'");
            }

            var expectedTab = ExpandTabId(e.Expand);
            if (!string.Equals(group.Tab, expectedTab, StringComparison.Ordinal))
            {
                throw new ActorSurfaceCatalogRejection(
                    $"{Catalog}: family '{e.Family}' sheetGroup.tab '{group.Tab}' does not match expand '{ExpandWire(e.Expand)}' (expected tab '{expectedTab}')");
            }

            // Tab-level expand is the default join for that tab; action-category lives under other/none.
            if (tabExpand.TryGetValue(group.Tab, out var tabKind)
                && e.Expand != DerivedExpandKind.ActionCategory
                && e.Expand != tabKind
                && !(group.Tab == "other" && e.Expand == DerivedExpandKind.None))
            {
                throw new ActorSurfaceCatalogRejection(
                    $"{Catalog}: family '{e.Family}' expand '{ExpandWire(e.Expand)}' disagrees with tab '{group.Tab}' expand '{ExpandWire(tabKind)}'");
            }
        }
    }

    static void RejectCombatParity(IReadOnlyList<DerivedStatSurfaceEntry> entries)
    {
        var elementFamilies = entries
            .Where(e => e.Expand == DerivedExpandKind.Element)
            .Select(e => e.Family)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var family in DerivedStatChannels.CombatChannelFamilies)
        {
            if (!elementFamilies.Contains(family))
            {
                throw new ActorSurfaceCatalogRejection(
                    $"{Catalog}: combat family '{family}' missing from catalog (expand: element)");
            }
        }

        foreach (var family in elementFamilies)
        {
            if (!DerivedStatChannels.CombatChannelFamilies.Contains(family))
            {
                throw new ActorSurfaceCatalogRejection(
                    $"{Catalog}: expand:element family '{family}' is not in CombatChannelFamilies");
            }
        }
    }

    static void RejectCombatUnitClassParity(IReadOnlyList<DerivedStatSurfaceEntry> entries)
    {
        foreach (var e in entries)
        {
            if (e.Expand != DerivedExpandKind.Element)
                continue;
            if (!DerivedStatChannels.CombatFamilyUnitClass.TryGetValue(e.Family, out var expected))
                continue;
            if (e.UnitClass != expected)
            {
                throw new ActorSurfaceCatalogRejection(
                    $"{Catalog}: family '{e.Family}' unitClass '{e.UnitClass}' does not match CombatFamilyUnitClass '{expected}'");
            }
        }
    }

    static void RejectStatusParity(IReadOnlyList<DerivedStatSurfaceEntry> entries)
    {
        var statusFamilies = entries
            .Where(e => e.Expand == DerivedExpandKind.StatusId || e.Expand == DerivedExpandKind.StatusCategory)
            .Select(e => e.Family)
            .ToHashSet(StringComparer.Ordinal);

        if (!StatusFamilyIds.SetEquals(statusFamilies))
        {
            throw new ActorSurfaceCatalogRejection(
                $"{Catalog}: status-id/status-category families must be exactly the six status.* potency families");
        }
    }

    static void RejectResourceParity(IReadOnlyList<DerivedStatSurfaceEntry> entries)
    {
        var resourceFamilies = entries
            .Where(e => e.Expand == DerivedExpandKind.Resource)
            .Select(e => e.Family)
            .ToHashSet(StringComparer.Ordinal);

        if (!ResourceFamilyIds.SetEquals(resourceFamilies))
        {
            throw new ActorSurfaceCatalogRejection(
                $"{Catalog}: resource expand families must be exactly resource.max/regen/efficiency/restore");
        }
    }

    static readonly HashSet<string> ActionCategoryFamilyIds = new(StringComparer.Ordinal)
    {
        DerivedStatChannels.SkillCooldownPrefix,
        DerivedStatChannels.SkillEffectivenessPrefix
    };

    static void RejectActionCategoryParity(IReadOnlyList<DerivedStatSurfaceEntry> entries)
    {
        var actionFamilies = entries
            .Where(e => e.Expand == DerivedExpandKind.ActionCategory)
            .Select(e => e.Family)
            .ToHashSet(StringComparer.Ordinal);

        if (!ActionCategoryFamilyIds.SetEquals(actionFamilies))
        {
            throw new ActorSurfaceCatalogRejection(
                $"{Catalog}: action-category families must be exactly skill.cooldown and skill.effectiveness");
        }
    }

    public static string ExpandTabId(DerivedExpandKind expand) => expand switch
    {
        DerivedExpandKind.Element => "elements",
        DerivedExpandKind.StatusCategory or DerivedExpandKind.StatusId => "status",
        DerivedExpandKind.Resource => "resources",
        DerivedExpandKind.None or DerivedExpandKind.ActionCategory => "other",
        _ => throw new ActorSurfaceCatalogRejection($"{Catalog}: unmapped expand '{expand}'")
    };

    public static string ExpandWire(DerivedExpandKind expand) => expand switch
    {
        DerivedExpandKind.None => "none",
        DerivedExpandKind.Element => "element",
        DerivedExpandKind.StatusCategory => "status-category",
        DerivedExpandKind.StatusId => "status-id",
        DerivedExpandKind.Resource => "resource",
        DerivedExpandKind.ActionCategory => "action-category",
        _ => throw new ActorSurfaceCatalogRejection($"{Catalog}: unmapped expand '{expand}'")
    };

    public static string ChannelPattern(DerivedExpandKind expand) =>
        expand == DerivedExpandKind.None ? "{family}" : "{family}.{variant}";
}

public static class DerivedStatSurfaceCatalogHub
{
    static DerivedStatSurfaceCatalog? _catalog;

    public static void Configure(DerivedStatSurfaceCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public static DerivedStatSurfaceCatalog Catalog => _catalog ?? throw new InvalidOperationException(
        "DerivedStatSurfaceCatalogHub.Configure(...) has not run. Hosts read data/tuning/derived-stat-catalog.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.");
}
