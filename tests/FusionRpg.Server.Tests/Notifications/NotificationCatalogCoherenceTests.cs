using FusionRpg.Core.Notify;
using FusionRpg.Server.Notifications;
using Xunit;

namespace FusionRpg.Server.Tests.Notifications;

/// <summary>
/// The catalogue-coherence join for the **cache** domain, and the closure that keeps the join honest
/// across domains (notify-vocabulary §1 / world-notify-source §4 Testing 9, whose own test covers the
/// `world` domain from Core). The rule these tests exist for: *a catalogue row that no producer can emit
/// can never fire, and a producer category with no catalogue row throws at publish time* — so every
/// registered category must join to the code that emits it, in both directions.
///
/// <para><b>Why the closure test matters more than the two joins.</b> Before it, the join existed for
/// `world` only: a `corpse-cache` row nobody emits (or a third domain added later with no join at all)
/// would have passed every green suite. The closure asserts that each category's domain is one this
/// program can join, so adding a domain forces the join rather than silently skipping it.</para>
/// </summary>
[Collection("NotificationHub")]
public class NotificationCatalogCoherenceTests
{
    /// <summary>The domains this program's producers can actually emit, each with its own join below.</summary>
    static readonly string[] JoinedDomains = { "world", "corpse-cache" };

    [Fact]
    public void Every_corpse_cache_category_is_emitted_by_the_cache_source()
    {
        var catalog = ShippedCatalog();
        foreach (var id in CategoriesInDomain(catalog, "corpse-cache"))
            Assert.True(CacheNotificationSource.KnownCategories.Contains(id),
                $"catalogue category '{id}' has domain 'corpse-cache' but CacheNotificationSource cannot emit it");
    }

    [Fact]
    public void Every_category_the_cache_source_emits_is_registered_with_domain_corpse_cache()
    {
        var catalog = ShippedCatalog();
        foreach (var id in CacheNotificationSource.KnownCategories)
        {
            Assert.True(catalog.TryGet(id, out var row), $"cache source category '{id}' has no catalogue row");
            Assert.Equal("corpse-cache", row!.Domain);
        }
    }

    /// <summary>The closure: no catalogue row may belong to a domain this program has not joined. A new
    /// domain fails here until its producer exposes its categories and a join is written — the point is to
    /// make that omission impossible rather than to pin any count. (The set of domains is a closed
    /// vocabulary the code owns: `world` and `corpse-cache` are the two sources that exist.)</summary>
    [Fact]
    public void Every_catalogue_domain_has_a_producer_join()
    {
        var catalog = ShippedCatalog();
        var unjoined = CategoriesByDomain(catalog)
            .Where(kv => !JoinedDomains.Contains(kv.Key, StringComparer.Ordinal))
            .Select(kv => $"{kv.Key} ({string.Join(", ", kv.Value)})")
            .ToList();

        Assert.Empty(unjoined);
    }

    static IEnumerable<string> CategoriesInDomain(NotificationCatalog catalog, string domain) =>
        CatalogRows()
            .Where(kv => string.Equals(kv.Value, domain, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .OrderBy(id => id, StringComparer.Ordinal);

    static Dictionary<string, string> CatalogRows() => CategoriesByDomain(ShippedCatalog())
        .SelectMany(kv => kv.Value.Select(id => (id, domain: kv.Key)))
        .ToDictionary(x => x.id, x => x.domain, StringComparer.Ordinal);

    static Dictionary<string, List<string>> CategoriesByDomain(NotificationCatalog catalog)
    {
        var byDomain = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var id in RegisteredIds())
        {
            Assert.True(catalog.TryGet(id, out var row), $"registered category '{id}' vanished from the catalogue");
            if (!byDomain.TryGetValue(row!.Domain, out var list)) byDomain[row.Domain] = list = new List<string>();
            list.Add(id);
        }
        return byDomain;
    }

    /// <summary>The shipped catalogue's own ids, read from the file the hosts load.</summary>
    static IEnumerable<string> RegisteredIds()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(CatalogPath()));
        foreach (var row in doc.RootElement.GetProperty("categories").EnumerateArray())
            yield return row.GetProperty("id").GetString()!;
    }

    static NotificationCatalog ShippedCatalog() =>
        NotificationCatalogLoader.Parse(File.ReadAllText(CatalogPath()));

    static string CatalogPath() =>
        Path.Combine(FindRepoRoot(), "data", "tuning", "notification-catalog.v3.json");

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not find repo root above " + AppContext.BaseDirectory);
    }
}
