using System.Text.Json;

namespace FusionRpg.Core.Notify;

/// <summary>notify-vocabulary spec §2 — the program's only two numbers, both a reading depth over
/// world turns. `RetainPerCategory` is R-N2's retention tail (rows, per save+category);
/// `RepeatWindowWorldTurns` is the non-Critical dedup suppression window, counted only on the world
/// clock (ideal finding M6 - a wall-clock source brings its own separately named key). Both are
/// `int`, not a magnitude: notify-store spec §3 - a row count a structural page bound already caps
/// well below `int`'s range, matching `AppendNotificationTurn`'s own `retainPerCategory` parameter.</summary>
public sealed record NotificationTuning(int RetainPerCategory, int RepeatWindowWorldTurns);

public sealed class NotificationTuningRejection : Exception
{
    public NotificationTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser over `data/tuning/notification.v{n}.json` (tunables-ssot.md §7.2: Core never
/// reads a file). A missing key rejects the load by name (T5) - there is no built-in default.</summary>
public static class NotificationTuningLoader
{
    public static NotificationTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new NotificationTuningRejection("notification tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new NotificationTuningRejection($"notification tuning: not valid JSON - {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("retainPerCategory", out var retainEl) || retainEl.ValueKind != JsonValueKind.Number || !retainEl.TryGetInt32(out var retain))
                throw new NotificationTuningRejection("notification tuning: missing or non-integer 'retainPerCategory'");
            if (retain <= 0)
                throw new NotificationTuningRejection($"notification tuning: retainPerCategory must be > 0; got {retain}");

            if (!root.TryGetProperty("repeatWindowWorldTurns", out var windowEl) || windowEl.ValueKind != JsonValueKind.Number || !windowEl.TryGetInt32(out var window))
                throw new NotificationTuningRejection("notification tuning: missing or non-integer 'repeatWindowWorldTurns'");
            if (window < 0)
                throw new NotificationTuningRejection($"notification tuning: repeatWindowWorldTurns must be >= 0; got {window}");

            return new NotificationTuning(retain, window);
        }
    }
}

/// <summary>Process-wide holder, matching `ActionBaseTuningHub`'s own plain-holder shape.</summary>
public static class NotificationTuningHub
{
    static NotificationTuning? _tuning;

    public static void Configure(NotificationTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static NotificationTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "NotificationTuningHub.Configure(...) has not run. Read data/tuning/notification.v{n}.json at " +
        "startup - there is no built-in default to fall back to.");

    public static bool IsConfigured => _tuning != null;

    /// <summary>Tests only.</summary>
    public static void Reset() => _tuning = null;
}
