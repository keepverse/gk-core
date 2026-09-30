using FusionRpg.Core.Notify;
using FusionRpg.Core.World.Notify;
using FusionRpg.Server.Notifications;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests.Notifications;

/// <summary>
/// The producer → catalogue **message-key** join, for both domains this program produces. A draft is
/// refused at publish time unless its category's catalogue row declares the draft's message key
/// (`NotificationContract.Validate`, notify-vocabulary §1) — so this test makes that refusal a CI
/// failure instead of a runtime surprise, the same way the category join already does.
///
/// <para>Why it was missing: the category join (`WorldNotifyCatalogCoherenceTests`,
/// `NotificationCatalogCoherenceTests`) checks that a category is registered and emitted, but not that
/// the *key* a producer passes with it is declared by that row. A reworded key — or a row whose
/// `messageKeys` was narrowed — would have passed every green suite and failed only in production.</para>
/// </summary>
[Collection("NotificationHub")]
public class NotificationProducerJoinTests
{
    [Fact]
    public void Every_world_category_the_classifier_emits_declares_the_turn_entry_key()
    {
        var catalog = ShippedCatalog();
        foreach (var category in WorldTurnNotificationClassifier.KnownCategories)
        {
            Assert.True(catalog.TryGet(category, out var row), $"classifier category '{category}' has no catalogue row");
            Assert.True(row!.MessageKeys.Contains(WorldReportNotificationSource.TurnEntryMessageKey),
                $"catalogue row '{category}' does not declare '{WorldReportNotificationSource.TurnEntryMessageKey}', " +
                "which every report-entry draft passes with it");
        }
    }

    [Fact]
    public void The_release_forecast_key_is_declared_by_its_category()
    {
        var catalog = ShippedCatalog();
        Assert.True(catalog.TryGet(WorldReportNotificationSource.ReleaseCategory, out var row),
            $"forecast category '{WorldReportNotificationSource.ReleaseCategory}' has no catalogue row");
        Assert.True(row!.MessageKeys.Contains(WorldReportNotificationSource.ReleaseMessageKey),
            $"catalogue row '{WorldReportNotificationSource.ReleaseCategory}' does not declare " +
            $"'{WorldReportNotificationSource.ReleaseMessageKey}'");
    }

    [Fact]
    public void Every_cache_category_declares_its_message_key()
    {
        var catalog = ShippedCatalog();
        var pairs = new[]
        {
            (Category: CacheNotificationSource.CreatedCategory, Key: CacheNotificationSource.CreatedMessageKey),
            (Category: CacheNotificationSource.DecayedCategory, Key: CacheNotificationSource.DecayedMessageKey)
        };

        foreach (var (category, key) in pairs)
        {
            Assert.True(catalog.TryGet(category, out var row), $"cache category '{category}' has no catalogue row");
            Assert.True(row!.MessageKeys.Contains(key),
                $"catalogue row '{category}' does not declare its producer's message key '{key}'");
        }
    }

    static NotificationCatalog ShippedCatalog() =>
        NotificationCatalogLoader.Parse(File.ReadAllText(CatalogPath()));

    static string CatalogPath() =>
        Path.Combine(FindRepoRoot(), "data", "tuning", "notification-catalog.v3.json");

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
