using FusionRpg.Contracts;

namespace FusionRpg.Core.Notify;

/// <summary>notify-service spec §1 - a pure, source-authored draft. `DedupKey` is stable and
/// deterministic (R-N5), never a Guid. `SubjectKey`/`WorldTurn` together drive the repeat-window
/// suppression (notify-service §2 step 2); a draft with either absent is never suppressed.</summary>
public sealed record NotificationDraft(
    string DedupKey,
    string CategoryId,
    NotifySeverity Severity,
    string SourceId,
    string MessageKey,
    IReadOnlyList<NotifyArg> Args,
    string? SubjectKey,
    string? WorldId,
    int? WorldTurn);
