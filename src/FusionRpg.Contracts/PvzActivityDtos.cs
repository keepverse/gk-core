using System.Text.Json.Serialization;

namespace FusionRpg.Contracts;

public sealed class PvzActivityFactDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    [JsonPropertyName("runId")] public long? RunId { get; set; }
    [JsonPropertyName("t")] public string T { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("pluginId")] public string PluginId { get; set; } = "";
    [JsonPropertyName("sourceKind")] public string SourceKind { get; set; } = "";
    [JsonPropertyName("sourceId")] public string SourceId { get; set; } = "";
    [JsonPropertyName("payloadJson")] public string? PayloadJson { get; set; }
    [JsonPropertyName("matchKey")] public string? MatchKey { get; set; }
    [JsonPropertyName("dedupeKey")] public string? DedupeKey { get; set; }
}

public sealed class PvzActivityRollupDto
{
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    [JsonPropertyName("revision")] public long Revision { get; set; }
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";
    [JsonPropertyName("matchesStarted")] public long MatchesStarted { get; set; }
    [JsonPropertyName("matchesEnded")] public long MatchesEnded { get; set; }
    [JsonPropertyName("victories")] public long Victories { get; set; }
    [JsonPropertyName("defeats")] public long Defeats { get; set; }
    [JsonPropertyName("zombiesKilled")] public long ZombiesKilled { get; set; }
    [JsonPropertyName("plantsLost")] public long PlantsLost { get; set; }
    [JsonPropertyName("plantsPlaced")] public long PlantsPlaced { get; set; }
    [JsonPropertyName("extraSpawnsFired")] public long ExtraSpawnsFired { get; set; }
}

public sealed class PvzActivityFactsPageDto
{
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    [JsonPropertyName("revision")] public long Revision { get; set; }
    [JsonPropertyName("items")] public List<PvzActivityFactDto> Items { get; set; } = new();
}

public sealed class PvzActivityAppendRequest
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("pluginId")] public string? PluginId { get; set; }
    [JsonPropertyName("sourceKind")] public string? SourceKind { get; set; }
    [JsonPropertyName("sourceId")] public string? SourceId { get; set; }
    [JsonPropertyName("payloadJson")] public string? PayloadJson { get; set; }
    [JsonPropertyName("runId")] public long? RunId { get; set; }
    [JsonPropertyName("matchKey")] public string? MatchKey { get; set; }
    [JsonPropertyName("dedupeKey")] public string? DedupeKey { get; set; }
}

public sealed class PvzSpawnExtraRequest
{
    [JsonPropertyName("typeId")] public int TypeId { get; set; }
    [JsonPropertyName("col")] public int? Col { get; set; }
    [JsonPropertyName("row")] public int? Row { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; set; }
    [JsonPropertyName("side")] public string? Side { get; set; }
    [JsonPropertyName("playerId")] public long? PlayerId { get; set; }
}
