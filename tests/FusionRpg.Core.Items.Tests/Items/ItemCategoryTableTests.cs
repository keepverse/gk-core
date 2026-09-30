using System.Text.Json;
using FusionRpg.Core.Items;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Items;

/// <summary>`base-types` (item module 6, I3's lane) against the REAL, shipped `item-category.v1.json`.</summary>
public class ItemCategoryTableTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static string RawJson() =>
        File.ReadAllText(Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "_seed", "item-category.v1.json"));

    static IReadOnlyList<ItemCategoryRow> Load() => ItemCategoryTable.Parse(RawJson());

    [Fact]
    public void All_ten_rows_ship()
    {
        var rows = Load();
        // The count is fully implied by the element-wise equality below -- never pinned separately
        // (population-pin SE3.3, 2026-09-19).
        Assert.Equal(ItemCategoryTable.CategoryIds.OrderBy(s => s), rows.Select(r => r.CategoryId).OrderBy(s => s));
    }

    [Fact]
    public void Every_row_names_a_non_empty_consumer()
    {
        foreach (var row in Load())
            Assert.False(string.IsNullOrWhiteSpace(row.Consumer));
    }

    [Fact]
    public void Unique_set_and_legendary_are_not_categories()
    {
        // They are rarities (I1) -- the reason this list is ten, not thirteen.
        var ids = Load().Select(r => r.CategoryId).ToHashSet();
        Assert.DoesNotContain("unique", ids);
        Assert.DoesNotContain("set", ids);
        Assert.DoesNotContain("legendary", ids);
    }

    [Fact]
    public void The_six_declare_only_categories_match_the_ssot()
    {
        // ssot-item-categories.md §5.1's own "v1" column marks six rows "declare only": consumable,
        // insert, charm, blueprint and cache (a named future consumer, unbuilt today) plus cosmetic
        // (no consumer ever planned).
        var declareOnly = Load().Where(r => r.DeclareOnly).Select(r => r.CategoryId).OrderBy(s => s).ToList();
        Assert.Equal(
            new[] { "blueprint", "cache", "charm", "consumable", "cosmetic", "insert" }.OrderBy(s => s),
            declareOnly);
    }

    [Fact]
    public void Equipment_material_quest_and_currency_are_authored_or_shipped_not_declare_only()
    {
        var authored = Load().Where(r => !r.DeclareOnly).Select(r => r.CategoryId).OrderBy(s => s).ToList();
        Assert.Equal(
            new[] { "currency", "equipment", "material", "quest" }.OrderBy(s => s),
            authored);
    }

    [Fact]
    public void A_document_with_an_empty_consumer_is_rejected()
    {
        using var doc = JsonDocument.Parse(RawJson());
        var text = RawJson().Replace(
            "\"consumer\": \"BindGate + compose -> EntityStatWriter (lawn)\"",
            "\"consumer\": \"\"");
        Assert.NotEqual(RawJson(), text);
        var ex = Assert.Throws<ItemCategoryRejection>(() => ItemCategoryTable.Parse(text));
        Assert.Contains("item.category-no-consumer", ex.Message);
    }

    [Fact]
    public void A_document_missing_a_row_is_rejected() =>
        Assert.Throws<ItemCategoryRejection>(() => ItemCategoryTable.Parse("""{"entries":[]}"""));

    [Fact]
    public void A_document_with_an_unknown_category_id_is_rejected()
    {
        var bad = """{"entries":[{"categoryId":"unique","rollsValues":false,"stackIntent":"never","ownerScope":"player","store":"stack","consumer":"x"}]}""";
        Assert.Throws<ItemCategoryRejection>(() => ItemCategoryTable.Parse(bad));
    }
}
