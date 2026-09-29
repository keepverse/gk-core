using System.Text.Json;
using System.Text.Json.Serialization;

namespace FusionRpg.Contracts;

/// <summary>Serializes an enum as its camelCase member name (e.g. <c>Routine</c> -&gt;
/// <c>"routine"</c>, <c>CatchUp</c> -&gt; <c>"catchUp"</c>). No other Contracts DTO serializes an
/// enum as a string today (most hold ints, or a hand-written Name()/TryParse() pair like
/// <see cref="RelationKind"/>); notify-vocabulary spec §3 asks for a camelCase wire string here so
/// the web's string-union types (<c>NotifySeverity</c>, <c>NotifyDelivery</c>, …) match the server
/// byte for byte, so this program carries its own attribute-applied converter instead of a global
/// <c>JsonSerializerOptions</c> change. Non-generic (<c>net6.0</c> target — the generic
/// <c>JsonStringEnumConverter&lt;TEnum&gt;</c> needs net7+).</summary>
public sealed class NotifyCamelCaseEnumConverter : JsonStringEnumConverter
{
    public NotifyCamelCaseEnumConverter() : base(JsonNamingPolicy.CamelCase) { }
}

/// <summary>Closed. Ordered: a larger value outranks a smaller one in the toast selection
/// (notify-client §Design 4). Raising a category's ceiling is an edit to the catalog's
/// promotions block, never a field a source sets for itself (R-N4).</summary>
[JsonConverter(typeof(NotifyCamelCaseEnumConverter))]
public enum NotifySeverity { Routine = 0, Important = 1, Critical = 2 }

/// <summary>Closed. The typed slots a message may carry. <see cref="DomainToken"/> is opaque to
/// the shared primitives. Only the translator of the domain that owns the category may read it.</summary>
[JsonConverter(typeof(NotifyCamelCaseEnumConverter))]
public enum NotifyArgKind { Magnitude, Count, WorldTurn, Ref, DomainToken }

/// <summary>Closed. Whether a batch may toast. Live = it just happened (the newest turn of a
/// commit). CatchUp = it is late (a boot run, a lagging turn); the client never toasts it
/// (notify-service §2).</summary>
[JsonConverter(typeof(NotifyCamelCaseEnumConverter))]
public enum NotifyDelivery { Live, CatchUp }

/// <summary>Closed. What a <c>ref</c> argument points at.</summary>
[JsonConverter(typeof(NotifyCamelCaseEnumConverter))]
public enum NotifyRefKind { Sector, Lane, Faction, Legion, Structure, Cache }

public sealed class NotifyArgDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("kind")] public NotifyArgKind Kind { get; set; }
    /// <summary>Kind-shaped JSON: Magnitude = {unit,value,op?,channel?} (the web's <c>Magnitude</c>,
    /// contract/types.ts:94); Count = long; WorldTurn = int; Ref = {refKind,id}; DomainToken =
    /// whatever OBJECT the owning domain defines — the world domain's is the report entry,
    /// <c>{kind,subject,detail,sectorId}</c> (<c>worldTranslator.ts</c> owns that shape), and the
    /// cache domain's is the bare place-kind string. Shared code never reads it.</summary>
    [JsonPropertyName("value")] public JsonElement Value { get; set; }
}

public sealed class NotificationDto
{
    [JsonPropertyName("seq")] public long Seq { get; set; }                 // store row id; the history `before` cursor
    [JsonPropertyName("rev")] public long Rev { get; set; }                 // per-save change counter; the catch-up `since` cursor
    [JsonPropertyName("dedupKey")] public string DedupKey { get; set; } = ""; // R-N5 - stable across push and GET
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("severity")] public NotifySeverity Severity { get; set; }
    [JsonPropertyName("sourceId")] public string SourceId { get; set; } = "";
    [JsonPropertyName("messageKey")] public string MessageKey { get; set; } = "";
    [JsonPropertyName("args")] public List<NotifyArgDto> Args { get; set; } = new();
    [JsonPropertyName("subjectKey")] public string? SubjectKey { get; set; }
    [JsonPropertyName("worldId")] public string? WorldId { get; set; }
    [JsonPropertyName("worldTurn")] public int? WorldTurn { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "unread"; // unread | read | dismissed
    [JsonPropertyName("createdUtc")] public string CreatedUtc { get; set; } = "";
}

public sealed class NotificationBatchDto
{
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }       // the SaveId (R17); wire name kept
    [JsonPropertyName("delivery")] public NotifyDelivery Delivery { get; set; }
    [JsonPropertyName("items")] public List<NotificationDto> Items { get; set; } = new();
}

public sealed class NotificationStateChangedDto
{
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }       // the SaveId (R17)
    [JsonPropertyName("state")] public string State { get; set; } = "";     // read | dismissed
    [JsonPropertyName("changes")] public List<NotificationStateChangeDto> Changes { get; set; } = new();
}

public sealed class NotificationStateChangeDto
{
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("rev")] public long Rev { get; set; }
}

public static class NotificationEvents
{
    public const string Batch = "NotificationBatch";          // R-N6: the only content event
    public const string StateChanged = "NotificationStateChanged";
}

/// <summary>notify-service spec §4 - `GET /api/notifications/{playerId}` and its `/history` sibling.
/// `nextSince` is the cursor a caller replays on its next catch-up call (the highest `rev`/`seq`
/// among `items`, or the caller's own `since`/`before` unchanged when `items` is empty).</summary>
public sealed class NotificationPageDto
{
    [JsonPropertyName("items")] public List<NotificationDto> Items { get; set; } = new();
    [JsonPropertyName("nextSince")] public long NextSince { get; set; }
    [JsonPropertyName("hasMore")] public bool HasMore { get; set; }
}

public sealed class SetNotificationStateRequest
{
    [JsonPropertyName("seqs")] public List<long> Seqs { get; set; } = new();
    [JsonPropertyName("state")] public string State { get; set; } = ""; // "read" | "dismissed"
}

public sealed class SetNotificationStateResponseDto
{
    [JsonPropertyName("changed")] public List<NotificationStateChangeDto> Changed { get; set; } = new();
}
