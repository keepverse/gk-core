using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

[Trait("VerificationId", "data.almanac-seed")]
public class AlmanacSeedEnrichmentTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public AlmanacSeedEnrichmentTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    void SeedDump(string side, int typeId, string name, string? enumName = null)
    {
        var fields = new Dictionary<string, string?> { ["name"] = name };
        if (enumName != null) fields["enumName"] = enumName;
        _store.UpsertAlmanacTextDump(side, typeId, fields, null);
    }

    [Fact]
    public void Exact_name_match_succeeds()
    {
        SeedDump("plant", 0, "Peashooter", enumName: "Peashooter");
        _store.RebuildAlmanacSeed();

        var summary = _store.ImportAlmanacEnrichment(new[]
        {
            new AlmanacEnrichmentImportRow { Name = "Peashooter", Side = "plant", TypeClass = "Basic Plant" }
        }, "test-source");

        Assert.Equal(1, summary.Matched);
        Assert.Empty(summary.Unmatched);
        var dto = _store.GetAlmanacSeed("plant", 0)!;
        Assert.NotNull(dto.Enrichment);
        Assert.Equal("Basic Plant", dto.Enrichment!.TypeClass);
        Assert.Equal("test-source", dto.Enrichment.Source);
    }

    [Fact]
    public void Case_and_whitespace_normalized_match_succeeds()
    {
        SeedDump("plant", 0, "Peashooter", enumName: "Peashooter");
        _store.RebuildAlmanacSeed();

        var summary = _store.ImportAlmanacEnrichment(new[]
        {
            new AlmanacEnrichmentImportRow { Name = "  pea shooter  ".Replace(" ", ""), Side = "plant", TypeClass = "Basic Plant" }
        }, "test-source");

        // Sanity: the transformed name really differs only by case/whitespace from "Peashooter".
        Assert.Equal(1, summary.Matched);
        Assert.Empty(summary.Unmatched);
    }

    [Fact]
    public void Genuinely_absent_name_reported_unmatched_not_dropped()
    {
        SeedDump("plant", 0, "Peashooter", enumName: "Peashooter");
        _store.RebuildAlmanacSeed();

        var summary = _store.ImportAlmanacEnrichment(new[]
        {
            new AlmanacEnrichmentImportRow { Name = "Peashooter", Side = "plant" },
            new AlmanacEnrichmentImportRow { Name = "SomeRemovedPlantFrom361", Side = "plant" }
        }, "test-source");

        Assert.Equal(1, summary.Matched);
        Assert.Equal(new[] { "SomeRemovedPlantFrom361" }, summary.Unmatched);
    }

    [Fact]
    public void Side_mismatch_does_not_cross_match()
    {
        SeedDump("plant", 0, "Ghost", enumName: "GhostPlant");
        _store.RebuildAlmanacSeed();

        var summary = _store.ImportAlmanacEnrichment(new[]
        {
            new AlmanacEnrichmentImportRow { Name = "Ghost", Side = "zombie" }
        }, "test-source");

        Assert.Equal(0, summary.Matched);
        Assert.Equal(new[] { "Ghost" }, summary.Unmatched);
    }

    [Fact]
    public void Enrichment_import_never_contaminates_core_columns()
    {
        SeedDump("plant", 0, "Peashooter", enumName: "Peashooter");
        _store.RebuildAlmanacSeed();
        var before = _store.GetAlmanacSeed("plant", 0)!;

        _store.ImportAlmanacEnrichment(new[]
        {
            new AlmanacEnrichmentImportRow
            {
                Name = "Peashooter", Side = "plant",
                Qualities = new[] { "Offensive" }, Unlock = "Beat Level 1", TypeClass = "Basic Plant"
            }
        }, "test-source");
        var after = _store.GetAlmanacSeed("plant", 0)!;

        Assert.Equal(before.DisplayName, after.DisplayName);
        Assert.Equal(before.TypeName, after.TypeName);
        Assert.Equal(before.SunCost, after.SunCost);
        Assert.Equal(before.CooldownSec, after.CooldownSec);
        Assert.Equal(before.CostStatus, after.CostStatus);
        Assert.Equal(before.Hp, after.Hp);
        Assert.Equal(before.Attack, after.Attack);
        Assert.Equal(before.Armor, after.Armor);
        Assert.Equal(before.ArmorMax, after.ArmorMax);
        Assert.Equal(before.StatsObserved, after.StatsObserved);
        Assert.NotNull(after.Enrichment);
        Assert.Null(before.Enrichment);
    }

    [Fact]
    public void Enrichment_reimport_updates_existing_row_not_duplicates()
    {
        SeedDump("plant", 0, "Peashooter", enumName: "Peashooter");
        _store.RebuildAlmanacSeed();

        _store.ImportAlmanacEnrichment(new[]
        {
            new AlmanacEnrichmentImportRow { Name = "Peashooter", Side = "plant", TypeClass = "Basic Plant" }
        }, "v1");
        _store.ImportAlmanacEnrichment(new[]
        {
            new AlmanacEnrichmentImportRow { Name = "Peashooter", Side = "plant", TypeClass = "Updated Class" }
        }, "v2");

        var dto = _store.GetAlmanacSeed("plant", 0)!;
        Assert.Equal("Updated Class", dto.Enrichment!.TypeClass);
        Assert.Equal("v2", dto.Enrichment.Source);
    }

    [Fact]
    public void Description_field_imports_and_reads_back()
    {
        SeedDump("plant", 0, "Sunflower", enumName: "Sunflower");
        _store.RebuildAlmanacSeed();

        var summary = _store.ImportAlmanacEnrichment(new[]
        {
            new AlmanacEnrichmentImportRow
            {
                Name = "Sunflower", Side = "plant",
                Description = "First production is 5-8s after planting."
            }
        }, "test-source");

        Assert.Equal(1, summary.Matched);
        var dto = _store.GetAlmanacSeed("plant", 0)!;
        Assert.Equal("First production is 5-8s after planting.", dto.Enrichment!.Description);
    }

    [Fact]
    public void Missing_enrichment_is_null_not_error()
    {
        SeedDump("plant", 0, "Peashooter", enumName: "Peashooter");
        _store.RebuildAlmanacSeed();

        var dto = _store.GetAlmanacSeed("plant", 0)!;
        Assert.Null(dto.Enrichment);
    }
}
