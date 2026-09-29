using System.Linq;
using System.Text.Json;
using FusionRpg.Core.Notify;
using FusionRpg.Core.World.Notify;
using Xunit;

namespace FusionRpg.Core.Tests.World.Notify;

/// <summary>world-notify-source spec §4, Testing 9 — Guard coherence join, never a count: every
/// category the classifier can produce is registered with domain "world", and every "world"
/// category the catalog registers is reachable, either by the classifier's table or by the
/// release forecast (`loam.release`, the one documented exception).</summary>
public class WorldNotifyCatalogCoherenceTests
{
    const string ForecastOnlyCategory = "loam.release";

    [Fact]
    public void Every_classifier_category_is_registered_with_domain_world()
    {
        var catalog = NotificationCatalogLoader.Parse(File.ReadAllText(FindRepoFile("data/tuning/notification-catalog.v3.json")));
        foreach (var category in WorldTurnNotificationClassifier.KnownCategories)
        {
            Assert.True(catalog.TryGet(category, out var row), $"classifier category '{category}' has no catalog row");
            Assert.Equal("world", row!.Domain);
        }
    }

    [Fact]
    public void Every_world_domain_category_is_emitted_by_the_classifier_or_the_forecast()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FindRepoFile("data/tuning/notification-catalog.v3.json")));
        var worldIds = doc.RootElement.GetProperty("categories").EnumerateArray()
            .Where(c => c.GetProperty("domain").GetString() == "world")
            .Select(c => c.GetProperty("id").GetString()!)
            .ToHashSet();

        Assert.Contains(ForecastOnlyCategory, worldIds); // sanity: the exception itself is really registered

        foreach (var id in worldIds)
        {
            if (id == ForecastOnlyCategory) continue; // documented exception - the forecast, not the classifier
            Assert.Contains(id, WorldTurnNotificationClassifier.KnownCategories);
        }
    }

    static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        throw new FileNotFoundException($"could not locate {relative} from {AppContext.BaseDirectory}");
    }
}
