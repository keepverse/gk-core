using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Data.Notifications;

namespace FusionRpg.Server.Notifications;

/// <summary>The `severity` column's TEXT wire value (`routine|important|critical`) - the exact
/// camelCase names `NotifyCamelCaseEnumConverter` gives `NotifySeverity` on the wire, kept here as a
/// plain switch rather than a JSON round-trip so the DB column and the wire agree by construction.
/// Shared by `NotificationPublisher` (write) and `NotificationEndpoints` (read).</summary>
public static class SeverityWire
{
    public static string ToWire(NotifySeverity s) => s switch
    {
        NotifySeverity.Routine => "routine",
        NotifySeverity.Important => "important",
        NotifySeverity.Critical => "critical",
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, "unknown NotifySeverity")
    };

    public static NotifySeverity FromWire(string s) => s switch
    {
        "routine" => NotifySeverity.Routine,
        "important" => NotifySeverity.Important,
        "critical" => NotifySeverity.Critical,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, "unknown severity column value")
    };
}

/// <summary>`NotificationRow` (store) -&gt; `NotificationDto` (wire), shared by the publisher's push
/// and the REST reads so both serialize `args_json` identically.</summary>
public static class NotificationRowMapping
{
    public static NotificationDto ToDto(NotificationRow row) => new()
    {
        Seq = row.Seq,
        Rev = row.Rev,
        DedupKey = row.DedupKey,
        Category = row.Category,
        Severity = SeverityWire.FromWire(row.Severity),
        SourceId = row.SourceId,
        MessageKey = row.MessageKey,
        Args = JsonSerializer.Deserialize<List<NotifyArgDto>>(row.ArgsJson) ?? new(),
        SubjectKey = row.SubjectKey,
        WorldId = row.WorldId,
        WorldTurn = row.WorldTurn,
        State = row.State,
        CreatedUtc = row.CreatedUtc
    };
}
