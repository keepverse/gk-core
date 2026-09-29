namespace FusionRpg.Server.Notifications;

/// <summary>notify-service spec §2 step 1 - a draft that violates the catalog contract. This is a
/// code defect, not a runtime choice: `world-notify-source`'s classifier test (and every other
/// source's own tests) catch a bad draft before it ever reaches this validator in production.</summary>
public sealed class NotificationContractException : Exception
{
    public string CategoryId { get; }
    public string MessageKey { get; }

    public NotificationContractException(string categoryId, string messageKey, string message)
        : base($"notification contract: category '{categoryId}', key '{messageKey}': {message}")
    {
        CategoryId = categoryId;
        MessageKey = messageKey;
    }
}
