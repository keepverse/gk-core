using FusionRpg.Contracts;
using FusionRpg.Core.ActorSurface;

namespace FusionRpg.Core.Stats.Derived;

/// <summary>
/// Classifies cook-expand / registry / sheet channel coverage for the derived audit.
/// Never invents channel values — report only.
/// </summary>
public sealed class DerivedAuditCoverageReport
{
    public IReadOnlyList<string> CookExpandIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RegistryIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SheetIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Present { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Touched { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingCook { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingRegistry { get; init; } = Array.Empty<string>();
    public DerivedAuditClassification Classified { get; init; } = new();
    public bool TreeAtomsPresent { get; init; }
    public int RegistryCount { get; init; }
    public int CookExpandCount { get; init; }
    public int SheetCount { get; init; }
    public int PresentCount { get; init; }
    public int TouchedCount { get; init; }
}

public sealed class DerivedAuditClassification
{
    public IReadOnlyList<string> ExpectedStatusSession { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExpectedNoProducer { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExpectedInjectorTreeGap { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GapUnwired { get; init; } = Array.Empty<string>();
}

public static class DerivedAuditCoverage
{
    /// <summary>Pinned registry count — structural census lock (same role as CatalogResolves269).</summary>
    public const int RegistryPin = 268; // 269 -> 268 (solid-enforcement `retire-atk` R4, 2026-09-18)

    /// <summary>Spec / atom-catalog known no-producer channels on the cold sheet.</summary>
    public static readonly IReadOnlyList<string> KnownNoProducerChannels = new[]
    {
        DerivedStatChannels.ProgressionBonusArm1,
        DerivedStatChannels.ProgressionBonusArm2,
    };

    public static IReadOnlyList<string> EnumerateCookExpandIds(DerivedSurfaceDto cook)
    {
        if (cook is null) throw new ArgumentNullException(nameof(cook));
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        var elementVariants = cook.Tabs.FirstOrDefault(t => t.Id == "elements")?.Variants
            ?? Array.Empty<DerivedSurfaceVariantDto>();
        var statusVariants = cook.Tabs.FirstOrDefault(t => t.Id == "status")?.Variants
            ?? Array.Empty<DerivedSurfaceVariantDto>();
        var resourceVariants = cook.Tabs.FirstOrDefault(t => t.Id == "resources")?.Variants
            ?? Array.Empty<DerivedSurfaceVariantDto>();
        var actionVariants = cook.Tabs.FirstOrDefault(t => t.Id == "other")?.ActionCategoryVariants
            ?? Array.Empty<DerivedSurfaceVariantDto>();

        foreach (var tab in cook.Tabs)
        {
            foreach (var cat in tab.Categories)
            {
                foreach (var fam in cat.Families)
                {
                    switch (fam.Expand)
                    {
                        case "none":
                            ids.Add(fam.Family);
                            break;
                        case "element":
                            foreach (var v in elementVariants)
                                ids.Add($"{fam.Family}.{v.Id}");
                            break;
                        case "status-category":
                        case "status-id":
                            foreach (var v in statusVariants)
                                ids.Add($"{fam.Family}.{v.Id}");
                            break;
                        case "resource":
                            foreach (var v in resourceVariants)
                                ids.Add($"{fam.Family}.{v.Id}");
                            break;
                        case "action-category":
                            foreach (var v in actionVariants)
                                ids.Add($"{fam.Family}.{v.Id}");
                            break;
                        default:
                            ids.Add(fam.Family);
                            break;
                    }
                }
            }
        }

        return ids.ToList();
    }

    public static DerivedAuditCoverageReport Build(
        DerivedSurfaceDto cook,
        DerivedStatRegistry registry,
        ActorSheetDto sheet,
        bool treeAtomsPresent)
    {
        if (cook is null) throw new ArgumentNullException(nameof(cook));
        if (registry is null) throw new ArgumentNullException(nameof(registry));
        if (sheet is null) throw new ArgumentNullException(nameof(sheet));

        var cookIds = EnumerateCookExpandIds(cook);
        var registryIds = registry.AllRegistered
            .Select(d => d.ChannelId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        var sheetIds = sheet.Derived
            .Select(c => c.ChannelId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        var sheetSet = sheetIds.ToHashSet(StringComparer.Ordinal);
        var contribByChannel = sheet.Derived.ToDictionary(
            c => c.ChannelId,
            c => c.Contributions?.Count > 0,
            StringComparer.Ordinal);

        var present = cookIds.Where(sheetSet.Contains).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var missingCook = cookIds.Where(id => !sheetSet.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var missingRegistry = registryIds.Where(id => !sheetSet.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var touched = sheet.Derived
            .Where(c => c.Contributions is { Count: > 0 })
            .Select(c => c.ChannelId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var expectedNoProducer = new List<string>();
        var expectedStatusSession = new List<string>();
        var expectedTreeGap = new List<string>();
        var gapUnwired = new List<string>();

        // Named Server/Injector gap: tree hydrate missing on this host — one marker, not a channel invent.
        if (!treeAtomsPresent)
            expectedTreeGap.Add("(tree-atoms-absent)");

        // Classify cook+registry union that is missing from the sheet or untouched by producers.
        var auditUniverse = cookIds.Concat(registryIds).Distinct(StringComparer.Ordinal);
        foreach (var id in auditUniverse.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!sheetSet.Contains(id))
            {
                if (IsKnownNoProducer(id))
                    expectedNoProducer.Add(id);
                else if (IsStatusSessionChannel(id))
                    expectedStatusSession.Add(id);
                else
                    gapUnwired.Add(id);
                continue;
            }

            if (contribByChannel.TryGetValue(id, out var hasContrib) && hasContrib)
                continue;

            if (IsKnownNoProducer(id))
                expectedNoProducer.Add(id);
            else if (IsStatusSessionChannel(id))
                expectedStatusSession.Add(id);
            else
                gapUnwired.Add(id);
        }

        return new DerivedAuditCoverageReport
        {
            CookExpandIds = cookIds,
            RegistryIds = registryIds,
            SheetIds = sheetIds,
            Present = present,
            Touched = touched,
            MissingCook = missingCook,
            MissingRegistry = missingRegistry,
            TreeAtomsPresent = treeAtomsPresent,
            RegistryCount = registryIds.Count,
            CookExpandCount = cookIds.Count,
            SheetCount = sheetIds.Count,
            PresentCount = present.Count,
            TouchedCount = touched.Count,
            Classified = new DerivedAuditClassification
            {
                ExpectedNoProducer = expectedNoProducer,
                ExpectedStatusSession = expectedStatusSession,
                ExpectedInjectorTreeGap = expectedTreeGap,
                GapUnwired = gapUnwired
            }
        };
    }

    public static bool IsKnownNoProducer(string channelId)
    {
        if (KnownNoProducerChannels.Contains(channelId, StringComparer.Ordinal))
            return true;
        return channelId.StartsWith("status.expose.", StringComparison.Ordinal);
    }

    /// <summary>
    /// Status-derived mods are Injector Hot session only on UniqueActorHubCompose — cold sheet
    /// cannot fill these without aptitude/equip/tree producers.
    /// </summary>
    public static bool IsStatusSessionChannel(string channelId) =>
        channelId.StartsWith("status.", StringComparison.Ordinal)
        && !channelId.StartsWith("status.expose.", StringComparison.Ordinal);

}
