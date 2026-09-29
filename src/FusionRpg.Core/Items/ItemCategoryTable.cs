using System.Text.Json;

namespace FusionRpg.Core.Items;

/// <summary>One `item_category` row (ssot-item-categories.md §5.1, claimed by `base-types`, I3's lane).</summary>
public sealed record ItemCategoryRow(
    string CategoryId, bool RollsValues, string StackIntent, string OwnerScope, string Store,
    string Consumer, bool DeclareOnly);

public sealed class ItemCategoryRejection : Exception
{
    public ItemCategoryRejection(string message) : base(message) { }
}

/// <summary>
/// The ten-row item-category taxonomy. SC7: `consumer` is NOT NULL and non-empty on every row — a row
/// no code consumes is not content, it is a lie in a table. Per item-ideal.md §2b.1, the rejection
/// namespaces under the one <see cref="ContentRuleViolated"/> catch-all rather than minting a 34th
/// closed reason code: <c>item.category-no-consumer</c>.
/// </summary>
public static class ItemCategoryTable
{
    static ItemCategoryTable() => FusionRpg.Core.Effects.Atoms.ContentRuleNamespaces.Register("item");

    /// <summary><c>unique</c>, <c>set</c> and <c>legendary</c> are rarities (I1), never categories --
    /// the reason this list is ten, not thirteen.</summary>
    public static readonly IReadOnlyList<string> CategoryIds = new[]
    {
        "equipment", "material", "quest", "currency", "consumable",
        "insert", "charm", "cosmetic", "blueprint", "cache",
    };

    public static IReadOnlyList<ItemCategoryRow> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ItemCategoryRejection("item-category: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new ItemCategoryRejection($"item-category: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("entries", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array)
                throw new ItemCategoryRejection("item-category: missing or non-array 'entries'");

            var rows = new List<ItemCategoryRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in entriesEl.EnumerateArray())
            {
                var id = e.GetProperty("categoryId").GetString()
                    ?? throw new ItemCategoryRejection("item-category: entry missing 'categoryId'");
                if (!CategoryIds.Contains(id))
                    throw new ItemCategoryRejection($"item-category: '{id}' is not one of the ten closed categories");
                if (!seen.Add(id))
                    throw new ItemCategoryRejection($"item-category: '{id}' is duplicated");

                var consumer = e.TryGetProperty("consumer", out var c) ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(consumer))
                    throw new ItemCategoryRejection(
                        $"ContentRuleViolated{{item.category-no-consumer}}: '{id}' has no consumer — a row no code consumes is not content, it is a lie in a table");

                rows.Add(new ItemCategoryRow(
                    id,
                    e.GetProperty("rollsValues").GetBoolean(),
                    e.GetProperty("stackIntent").GetString() ?? throw new ItemCategoryRejection($"'{id}': missing stackIntent"),
                    e.GetProperty("ownerScope").GetString() ?? throw new ItemCategoryRejection($"'{id}': missing ownerScope"),
                    e.GetProperty("store").GetString() ?? throw new ItemCategoryRejection($"'{id}': missing store"),
                    consumer!,
                    e.TryGetProperty("declareOnly", out var d) && d.GetBoolean()));
            }

            if (rows.Count != CategoryIds.Count)
                throw new ItemCategoryRejection($"item-category: {rows.Count} rows seeded, all ten must ship");

            return rows;
        }
    }
}
