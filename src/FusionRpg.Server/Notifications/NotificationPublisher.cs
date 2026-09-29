using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Notify;
using FusionRpg.Core.Saves;
using FusionRpg.Data;
using FusionRpg.Data.Notifications;
using FusionRpg.Core.Time;

namespace FusionRpg.Server.Notifications;

/// <summary>notify-service spec §2 - validate, drop repeats, append durably, THEN push. A push
/// carries only rows the store reports as newly inserted (notify-store §2), so a re-run pushes
/// nothing and a dropped frame is recovered by the catch-up GET (map "Durable first, then push").</summary>
public sealed class NotificationPublisher
{
    readonly RpgStore _store;
    readonly NotificationContract _contract;
    readonly IPlayerPush _push;

    public NotificationPublisher(RpgStore store, NotificationContract contract, IPlayerPush push)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _contract = contract ?? throw new ArgumentNullException(nameof(contract));
        _push = push ?? throw new ArgumentNullException(nameof(push));
    }

    /// <summary>One save, no cursor - the same call as `PublishTurn` with one addressed draft set.</summary>
    public void Publish(SaveId saveId, IReadOnlyList<NotificationDraft> drafts, NotifyDelivery delivery) =>
        PublishTurn(drafts.Select(d => new AddressedDraft(saveId, d)).ToList(), delivery, cursor: null);

    public void PublishTurn(IReadOnlyList<AddressedDraft> drafts, NotifyDelivery delivery, NotificationCursorAdvance? cursor)
    {
        foreach (var d in drafts) _contract.Validate(d.Draft); // loud: a defect, not a runtime choice

        var tuning = NotificationTuningHub.Tuning;
        var perSave = drafts
            .Where(d => !Suppressed(d, tuning))
            .GroupBy(d => d.SaveId)
            .Select(g => new NotificationSaveAppend(g.Key, g.Select(d => ToInsert(d.Draft)).ToList()))
            .ToList();

        var inserted = _store.AppendNotificationTurn(perSave, tuning.RetainPerCategory,
            ServerClock.UtcNowDateTime.ToString("o"), cursor); // durable FIRST, cursor in the same transaction

        foreach (var (saveId, rows) in inserted)
        {
            if (rows.Count == 0) continue; // a re-run (or an all-suppressed turn) pushes nothing
            // R-N6: severity descending, then seq ascending - one fixed order for client preemption.
            var items = rows.OrderByDescending(r => SeverityWire.FromWire(r.Severity)).ThenBy(r => r.Seq)
                .Select(NotificationRowMapping.ToDto).ToList();
            _push.Push(saveId, NotificationEvents.Batch,
                new NotificationBatchDto { PlayerId = saveId.Value, Delivery = delivery, Items = items });
        }
    }

    bool Suppressed(AddressedDraft d, NotificationTuning tuning)
    {
        var draft = d.Draft;
        if (draft.Severity == NotifySeverity.Critical) return false; // R-N4: never throttled
        if (draft.SubjectKey is null || draft.WorldTurn is null) return false;
        return _store.HasRecentNotification(d.SaveId, draft.CategoryId, draft.SubjectKey,
            draft.WorldTurn.Value - tuning.RepeatWindowWorldTurns);
    }

    static NotificationInsert ToInsert(NotificationDraft draft) => new(
        draft.DedupKey, draft.CategoryId, SeverityWire.ToWire(draft.Severity), draft.SourceId, draft.MessageKey,
        SerializeArgs(draft.Args), draft.SubjectKey, draft.WorldId, draft.WorldTurn);

    static string SerializeArgs(IReadOnlyList<NotifyArg> args)
    {
        var dtoArgs = args.Select(a => new NotifyArgDto
        {
            Name = a.Name,
            Kind = a.Kind,
            Value = JsonSerializer.SerializeToElement(a.Value)
        }).ToList();
        return JsonSerializer.Serialize(dtoArgs);
    }

}
