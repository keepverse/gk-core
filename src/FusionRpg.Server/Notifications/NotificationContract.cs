using FusionRpg.Contracts;
using FusionRpg.Core.Notify;

namespace FusionRpg.Server.Notifications;

/// <summary>notify-service spec §2 step 1 - validates a draft against the open catalog and the
/// closed argument-kind vocabulary before it is ever appended. A failure is loud (throws), never a
/// silent drop: an invalid draft is a source's own bug.</summary>
public sealed class NotificationContract
{
    readonly NotificationCatalog _catalog;

    public NotificationContract(NotificationCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public void Validate(NotificationDraft draft)
    {
        if (!_catalog.TryGet(draft.CategoryId, out var row) || row is null)
            throw new NotificationContractException(draft.CategoryId, draft.MessageKey, "category is not registered");

        if (!row.MessageKeys.Contains(draft.MessageKey, StringComparer.Ordinal))
            throw new NotificationContractException(draft.CategoryId, draft.MessageKey,
                $"'{draft.MessageKey}' is not one of the category's declared messageKeys");

        var ceiling = _catalog.CeilingOf(draft.CategoryId);
        if (draft.Severity > ceiling)
            throw new NotificationContractException(draft.CategoryId, draft.MessageKey,
                $"severity {draft.Severity} exceeds the category's ceiling {ceiling}");

        foreach (var arg in draft.Args)
        {
            if (!Enum.IsDefined(typeof(NotifyArgKind), arg.Kind))
                throw new NotificationContractException(draft.CategoryId, draft.MessageKey,
                    $"arg '{arg.Name}' has an unknown kind");
        }
    }
}
