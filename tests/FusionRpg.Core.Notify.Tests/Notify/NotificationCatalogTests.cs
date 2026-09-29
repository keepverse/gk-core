using FusionRpg.Contracts;
using FusionRpg.Core.Notify;
using Xunit;

namespace FusionRpg.Core.Tests.Notify;

/// <summary>notify-vocabulary §1, §2, §4 - the catalog and tuning parsers. Never asserts how many
/// categories exist (an open, growing population, validation-ssot); asserts only the contract.</summary>
public class NotificationCatalogTests
{
    const string ShippedV1 = """
        { "schemaVersion": 1, "version": 1, "categories": [], "promotions": { "toast": [], "critical": [] } }
        """;

    [Fact]
    public void The_shipped_v1_file_parses_as_a_valid_empty_catalog()
    {
        var json = File.ReadAllText(FindRepoFile("data/tuning/notification-catalog.v1.json"));
        var catalog = NotificationCatalogLoader.Parse(json);
        Assert.False(catalog.TryGet("anything", out _));
    }

    [Fact]
    public void The_shipped_v1_tuning_file_parses()
    {
        var json = File.ReadAllText(FindRepoFile("data/tuning/notification.v1.json"));
        var tuning = NotificationTuningLoader.Parse(json);
        Assert.Equal(100, tuning.RetainPerCategory);
        Assert.Equal(3, tuning.RepeatWindowWorldTurns);
    }

    [Fact]
    public void An_empty_catalog_answers_unknown_for_any_id()
    {
        var catalog = NotificationCatalogLoader.Parse(ShippedV1);
        Assert.False(catalog.TryGet("loam.shortfall", out var row));
        Assert.Null(row);
        Assert.False(catalog.IsToast("loam.shortfall"));
        Assert.Equal(NotifySeverity.Important, catalog.CeilingOf("loam.shortfall"));
    }

    [Fact]
    public void A_registered_category_round_trips_its_fields()
    {
        var json = """
            { "categories": [
                { "id": "loam.shortfall", "domain": "world", "displayName": "Shortfall", "messageKeys": ["world.turn-entry"] }
              ], "promotions": { "toast": ["loam.shortfall"], "critical": [] } }
            """;
        var catalog = NotificationCatalogLoader.Parse(json);
        Assert.True(catalog.TryGet("loam.shortfall", out var row));
        Assert.Equal("world", row!.Domain);
        Assert.Equal("Shortfall", row.DisplayName);
        Assert.Equal("world.turn-entry", Assert.Single(row.MessageKeys));
        Assert.True(catalog.IsToast("loam.shortfall"));
        Assert.Equal(NotifySeverity.Important, catalog.CeilingOf("loam.shortfall"));
    }

    [Fact]
    public void Critical_promotion_raises_the_ceiling()
    {
        var json = """
            { "categories": [
                { "id": "territory.lost", "domain": "world", "displayName": "Lost", "messageKeys": ["world.turn-entry"] }
              ], "promotions": { "toast": ["territory.lost"], "critical": ["territory.lost"] } }
            """;
        var catalog = NotificationCatalogLoader.Parse(json);
        Assert.Equal(NotifySeverity.Critical, catalog.CeilingOf("territory.lost"));
    }

    [Fact]
    public void Duplicate_category_id_is_rejected()
    {
        var json = """
            { "categories": [
                { "id": "a", "domain": "world", "displayName": "A", "messageKeys": ["k"] },
                { "id": "a", "domain": "world", "displayName": "A2", "messageKeys": ["k"] }
              ] }
            """;
        var ex = Assert.Throws<NotificationCatalogRejection>(() => NotificationCatalogLoader.Parse(json));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'a'", ex.Message);
    }

    [Fact]
    public void A_promotion_naming_an_unregistered_id_is_rejected()
    {
        var json = """{ "categories": [], "promotions": { "toast": ["ghost"], "critical": [] } }""";
        var ex = Assert.Throws<NotificationCatalogRejection>(() => NotificationCatalogLoader.Parse(json));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public void A_critical_id_missing_from_toast_is_rejected()
    {
        var json = """
            { "categories": [
                { "id": "a", "domain": "world", "displayName": "A", "messageKeys": ["k"] }
              ], "promotions": { "toast": [], "critical": ["a"] } }
            """;
        var ex = Assert.Throws<NotificationCatalogRejection>(() => NotificationCatalogLoader.Parse(json));
        Assert.Contains("toast", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_messageKeys_is_rejected()
    {
        var json = """{ "categories": [ { "id": "a", "domain": "world", "displayName": "A", "messageKeys": [] } ] }""";
        var ex = Assert.Throws<NotificationCatalogRejection>(() => NotificationCatalogLoader.Parse(json));
        Assert.Contains("messageKeys", ex.Message);
    }

    [Fact]
    public void A_row_carrying_channel_is_rejected()
    {
        var json = """{ "categories": [ { "id": "a", "domain": "world", "displayName": "A", "messageKeys": ["k"], "channel": "toast" } ] }""";
        var ex = Assert.Throws<NotificationCatalogRejection>(() => NotificationCatalogLoader.Parse(json));
        Assert.Contains("channel", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_row_carrying_severity_is_rejected()
    {
        var json = """{ "categories": [ { "id": "a", "domain": "world", "displayName": "A", "messageKeys": ["k"], "severity": "critical" } ] }""";
        var ex = Assert.Throws<NotificationCatalogRejection>(() => NotificationCatalogLoader.Parse(json));
        Assert.Contains("severity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_retainPerCategory_throws_naming_the_key()
    {
        var ex = Assert.Throws<NotificationTuningRejection>(
            () => NotificationTuningLoader.Parse("""{ "repeatWindowWorldTurns": 3 }"""));
        Assert.Contains("retainPerCategory", ex.Message);
    }

    [Fact]
    public void Missing_repeatWindowWorldTurns_throws_naming_the_key()
    {
        var ex = Assert.Throws<NotificationTuningRejection>(
            () => NotificationTuningLoader.Parse("""{ "retainPerCategory": 100 }"""));
        Assert.Contains("repeatWindowWorldTurns", ex.Message);
    }

    [Fact]
    public void NotificationCatalogHub_throws_until_configured()
    {
        NotificationCatalogHub.Reset();
        Assert.False(NotificationCatalogHub.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => NotificationCatalogHub.Catalog);
        NotificationCatalogHub.Configure(NotificationCatalogLoader.Parse(ShippedV1));
        Assert.True(NotificationCatalogHub.IsConfigured);
        Assert.NotNull(NotificationCatalogHub.Catalog);
        NotificationCatalogHub.Reset();
    }

    [Fact]
    public void NotificationTuningHub_throws_until_configured()
    {
        NotificationTuningHub.Reset();
        Assert.False(NotificationTuningHub.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => NotificationTuningHub.Tuning);
        NotificationTuningHub.Configure(new NotificationTuning(100, 3));
        Assert.True(NotificationTuningHub.IsConfigured);
        NotificationTuningHub.Reset();
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
