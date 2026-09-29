using FusionRpg.Core.Saves;

namespace FusionRpg.Data.Notifications;

/// <summary>notify-store spec §2 - one draft to insert for one save. `ArgsJson` is the already-shaped
/// `NotifyArgDto[]` wire array, stored as-is and never re-rendered by the store (words belong to the
/// web, R-N3).</summary>
public sealed record NotificationInsert(
    string DedupKey,
    string Category,
    string Severity,
    string SourceId,
    string MessageKey,
    string ArgsJson,
    string? SubjectKey,
    string? WorldId,
    int? WorldTurn);

/// <summary>One save's rows for one `AppendNotificationTurn` call.</summary>
public sealed record NotificationSaveAppend(SaveId SaveId, IReadOnlyList<NotificationInsert> Rows);

/// <summary>Where a source's `rpg_notification_source_cursor` row should land, in the SAME
/// transaction as the rows it vouches for.</summary>
public sealed record NotificationCursorAdvance(string SourceId, string ScopeKey, long Position);

/// <summary>A stored row, exactly as `rpg_notification` holds it.</summary>
public sealed record NotificationRow(
    long Seq,
    long Rev,
    string DedupKey,
    string Category,
    string Severity,
    string SourceId,
    string MessageKey,
    string ArgsJson,
    string? SubjectKey,
    string? WorldId,
    int? WorldTurn,
    string State,
    string CreatedUtc);

/// <summary>A bounded page of rows. `HasMore` is true only when the store found more rows than
/// `limit` asked for (the store over-fetches by one internally to answer this without a second
/// query).</summary>
public sealed record NotificationPage(IReadOnlyList<NotificationRow> Items, bool HasMore);

/// <summary>One row's outcome from `SetNotificationState` - only rows that actually changed.</summary>
public sealed record NotificationStateChange(long Seq, long Rev);
