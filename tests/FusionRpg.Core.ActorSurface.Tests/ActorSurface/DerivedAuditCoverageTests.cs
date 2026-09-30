using FusionRpg.Contracts;
using FusionRpg.Core.ActorSurface;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.ActorSurface;

public sealed class DerivedAuditCoverageTests
{
    [Fact]
    public void Registry_pin_stays_268()
    {
        Assert.Equal(DerivedAuditCoverage.RegistryPin, DerivedStatRegistry.CreateDefault().AllRegistered.Count);
    }

    [Fact]
    public void Cook_expand_ids_resolve_via_registry_or_open_prefix()
    {
        ConfigureAllSurfaceCatalogs();
        var cook = DerivedSurfaceCook.Build("en", "plant");
        var cookIds = DerivedAuditCoverage.EnumerateCookExpandIds(cook);
        var registry = DerivedStatRegistry.CreateDefault();
        var registered = registry.AllRegistered.Select(d => d.ChannelId).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(DerivedAuditCoverage.RegistryPin, registered.Count);
        Assert.NotEmpty(cookIds);
        // D1: Status rail is Omni+L2b (4) × six dense families — no longer Omni+24 open-prefix.
        Assert.Contains("status.resist.dot", cookIds);
        Assert.DoesNotContain("status.resist.butter", cookIds);
        foreach (var id in cookIds)
            Assert.True(registry.TryResolveChannel(id, out _), "cook expand unresolved: " + id);

        // Empty sheet → every cook id is missing; arm channels classify as no-producer.
        var emptySheet = new ActorSheetDto { InstanceId = "x", Derived = Array.Empty<ActorSheetChannelDto>() };
        var report = DerivedAuditCoverage.Build(cook, registry, emptySheet, treeAtomsPresent: false);
        Assert.Equal(cookIds.Count, report.MissingCook.Count);
        Assert.Equal(DerivedAuditCoverage.RegistryPin, report.MissingRegistry.Count);
        Assert.Contains(DerivedStatChannels.ProgressionBonusArm1, report.Classified.ExpectedNoProducer);
        Assert.Contains(DerivedStatChannels.ProgressionBonusArm2, report.Classified.ExpectedNoProducer);
        Assert.Contains("(tree-atoms-absent)", report.Classified.ExpectedInjectorTreeGap);
        Assert.True(report.Classified.GapUnwired.Count > 0);
    }

    [Fact]
    public void Full_registry_sheet_with_no_contribs_classifies_status_and_no_producer()
    {
        ConfigureAllSurfaceCatalogs();
        var cook = DerivedSurfaceCook.Build("en", "plant");
        var registry = DerivedStatRegistry.CreateDefault();
        var sheet = new ActorSheetDto
        {
            InstanceId = "x",
            Derived = registry.AllRegistered
                .Select(d => new ActorSheetChannelDto
                {
                    ChannelId = d.ChannelId,
                    Value = d.DefaultValue,
                    Contributions = Array.Empty<ActorContributionDto>()
                })
                .ToList()
        };
        var report = DerivedAuditCoverage.Build(cook, registry, sheet, treeAtomsPresent: true);
        // Dense registry is fully on the sheet; cook expand is L2b-closed so MissingCook stays empty
        // (or only open-prefix leftovers if any remain).
        Assert.Empty(report.MissingRegistry);
        Assert.All(report.MissingCook, id =>
            Assert.True(DerivedAuditCoverage.IsStatusSessionChannel(id), id));
        Assert.Equal(report.CookExpandCount - report.MissingCook.Count, report.PresentCount);
        Assert.Empty(report.Touched);
        Assert.Contains(DerivedStatChannels.ProgressionBonusArm1, report.Classified.ExpectedNoProducer);
        Assert.Contains(report.Classified.ExpectedStatusSession, id => id.StartsWith("status.", StringComparison.Ordinal));
        Assert.DoesNotContain("(tree-atoms-absent)", report.Classified.ExpectedInjectorTreeGap);
        Assert.Contains(report.Classified.GapUnwired, id => id.StartsWith("combat.", StringComparison.Ordinal));
    }

    static void ConfigureAllSurfaceCatalogs()
    {
        ActorSurfaceCatalogHub.ConfigureAll(
            AptitudeSurfaceCatalogLoader.Parse(ReadTuning("aptitude-catalog.v1.json")),
            DerivedStatSurfaceCatalogLoader.Parse(ReadTuning("derived-stat-catalog.v3.json")),
            StatusSurfaceCatalogLoader.Parse(ReadTuning("status-catalog.v1.json")),
            ResourceSurfaceCatalogLoader.Parse(ReadTuning("resource-catalog.v1.json")),
            ElementSurfaceCatalogLoader.Parse(ReadTuning("element-catalog.v1.json")),
            ActorSheetSurfaceCatalogLoader.Parse(ReadTuning("actor-sheet.v1.json")));
    }

    static string ReadTuning(string fileName)
    {
        var root = FindRepoRoot();
        return File.ReadAllText(Path.Combine(root, "data", "tuning", fileName));
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
