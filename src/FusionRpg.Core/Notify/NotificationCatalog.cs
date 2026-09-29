using System.Text.Json;
using FusionRpg.Contracts;

namespace FusionRpg.Core.Notify;

public sealed class NotificationCatalogRejection : Exception
{
    public NotificationCatalogRejection(string message) : base(message) { }
}

/// <summary>One open-registry row (R-N2). Never carries a channel or a severity — only
/// `promotions` may raise a category, and that is a reviewed diff in one block.</summary>
public sealed record NotificationCategoryRow(string Id, string Domain, string DisplayName, IReadOnlyList<string> MessageKeys);

/// <summary>notify-vocabulary spec §1 — the runtime catalog (tunables-ssot §1). Pure parser + query
/// surface, no file I/O (Core never reads a file; hosts load and inject). The category list is
/// open (a feature adds rows for the categories it owns) and its size is never asserted; the
/// promotions block is closed and reviewed (R-N2, R-N4).</summary>
public sealed class NotificationCatalog
{
    readonly Dictionary<string, NotificationCategoryRow> _rows;
    readonly HashSet<string> _toast;
    readonly HashSet<string> _critical;

    internal NotificationCatalog(Dictionary<string, NotificationCategoryRow> rows, HashSet<string> toast, HashSet<string> critical)
    {
        _rows = rows;
        _toast = toast;
        _critical = critical;
    }

    public bool TryGet(string categoryId, out NotificationCategoryRow? row) => _rows.TryGetValue(categoryId, out row);

    /// <summary>The default channel a registered-but-unpromoted category gets is a web concern
    /// (`shell/notify/catalog.ts` `defaultChannelOf`); this only answers whether `promotions.toast`
    /// names the id, which that web function reads from the same JSON.</summary>
    public bool IsToast(string categoryId) => _toast.Contains(categoryId);

    /// <summary>Critical if the id is in `promotions.critical`, otherwise Important — the ceiling a
    /// source's draft may not exceed (NotificationContract.Validate). An unregistered id still
    /// answers Important: it never earned a ceiling above the one every category starts with.</summary>
    public NotifySeverity CeilingOf(string categoryId) =>
        _critical.Contains(categoryId) ? NotifySeverity.Critical : NotifySeverity.Important;
}

/// <summary>Pure parser over `data/tuning/notification-catalog.v{n}.json`. Rejects: a duplicate id;
/// a promotion naming an unregistered id; a `critical` id missing from `toast`; an empty
/// `messageKeys`; a row carrying a `channel` or `severity` field (R-N2's no-self-promotion rule).</summary>
public static class NotificationCatalogLoader
{
    public static NotificationCatalog Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new NotificationCatalogRejection("notification-catalog: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new NotificationCatalogRejection($"notification-catalog: not valid JSON - {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("categories", out var categoriesEl) || categoriesEl.ValueKind != JsonValueKind.Array)
                throw new NotificationCatalogRejection("notification-catalog: missing or non-array 'categories'");

            var rows = new Dictionary<string, NotificationCategoryRow>(StringComparer.Ordinal);
            foreach (var row in categoriesEl.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    throw new NotificationCatalogRejection("notification-catalog: a category row must be an object");
                if (row.TryGetProperty("channel", out _))
                    throw new NotificationCatalogRejection("notification-catalog: a category row may not carry 'channel' (R-N2 - only promotions may set delivery)");
                if (row.TryGetProperty("severity", out _))
                    throw new NotificationCatalogRejection("notification-catalog: a category row may not carry 'severity' (R-N4 - only promotions may raise the ceiling)");
                if (!row.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(idEl.GetString()))
                    throw new NotificationCatalogRejection("notification-catalog: a category row is missing a non-empty 'id'");
                var id = idEl.GetString()!;
                if (rows.ContainsKey(id))
                    throw new NotificationCatalogRejection($"notification-catalog: duplicate category id '{id}'");
                if (!row.TryGetProperty("domain", out var domainEl) || domainEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(domainEl.GetString()))
                    throw new NotificationCatalogRejection($"notification-catalog: category '{id}' is missing a non-empty 'domain'");
                if (!row.TryGetProperty("displayName", out var nameEl) || nameEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameEl.GetString()))
                    throw new NotificationCatalogRejection($"notification-catalog: category '{id}' is missing a non-empty 'displayName'");
                if (!row.TryGetProperty("messageKeys", out var keysEl) || keysEl.ValueKind != JsonValueKind.Array || keysEl.GetArrayLength() == 0)
                    throw new NotificationCatalogRejection($"notification-catalog: category '{id}' must have a non-empty 'messageKeys'");

                var keys = new List<string>();
                foreach (var keyEl in keysEl.EnumerateArray())
                {
                    if (keyEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(keyEl.GetString()))
                        throw new NotificationCatalogRejection($"notification-catalog: category '{id}' has a non-string or empty messageKey");
                    keys.Add(keyEl.GetString()!);
                }
                rows[id] = new NotificationCategoryRow(id, domainEl.GetString()!, nameEl.GetString()!, keys);
            }

            var toast = new HashSet<string>(StringComparer.Ordinal);
            var critical = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("promotions", out var promotionsEl) && promotionsEl.ValueKind == JsonValueKind.Object)
            {
                ReadPromotionList(promotionsEl, "toast", rows, toast);
                ReadPromotionList(promotionsEl, "critical", rows, critical);
            }
            foreach (var id in critical)
            {
                if (!toast.Contains(id))
                    throw new NotificationCatalogRejection($"notification-catalog: promotions.critical '{id}' must also be in promotions.toast (critical subset of toast)");
            }
            return new NotificationCatalog(rows, toast, critical);
        }
    }

    static void ReadPromotionList(JsonElement promotions, string field, Dictionary<string, NotificationCategoryRow> rows, HashSet<string> into)
    {
        if (!promotions.TryGetProperty(field, out var listEl))
            return;
        if (listEl.ValueKind != JsonValueKind.Array)
            throw new NotificationCatalogRejection($"notification-catalog: promotions.{field} must be an array");
        foreach (var idEl in listEl.EnumerateArray())
        {
            if (idEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(idEl.GetString()))
                throw new NotificationCatalogRejection($"notification-catalog: promotions.{field} has a non-string or empty id");
            var id = idEl.GetString()!;
            if (!rows.ContainsKey(id))
                throw new NotificationCatalogRejection($"notification-catalog: promotions.{field} names unregistered category '{id}'");
            into.Add(id);
        }
    }
}

/// <summary>Process-wide holder, matching `ActionBaseTuningHub`'s own plain-holder shape: configured
/// once at host startup, throws when unconfigured, no built-in default.</summary>
public static class NotificationCatalogHub
{
    static NotificationCatalog? _catalog;

    public static void Configure(NotificationCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public static NotificationCatalog Catalog => _catalog ?? throw new InvalidOperationException(
        "NotificationCatalogHub.Configure(...) has not run. Read data/tuning/notification-catalog.v{n}.json at " +
        "startup - there is no built-in default to fall back to.");

    public static bool IsConfigured => _catalog != null;

    /// <summary>Tests only.</summary>
    public static void Reset() => _catalog = null;
}
