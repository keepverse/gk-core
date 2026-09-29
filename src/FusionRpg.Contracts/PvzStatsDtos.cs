using System.Text.Json.Serialization;

namespace FusionRpg.Contracts;

/// <summary>One PvzStats SSOT row (player-bound Xi). Not RPG progression.</summary>
public sealed class PvzStatModifierDto
{
    [JsonPropertyName("pluginId")] public string PluginId { get; set; } = "";
    [JsonPropertyName("sourceKind")] public string SourceKind { get; set; } = "";
    [JsonPropertyName("sourceId")] public string SourceId { get; set; } = "";
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    [JsonPropertyName("op")] public string Op { get; set; } = "Flat";
    [JsonPropertyName("value")] public double Value { get; set; }
    [JsonPropertyName("priority")] public int Priority { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("detailJson")] public string? DetailJson { get; set; }
}

public sealed class PvzStatContributionDto
{
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    [JsonPropertyName("pluginId")] public string PluginId { get; set; } = "";
    [JsonPropertyName("sourceKind")] public string SourceKind { get; set; } = "";
    [JsonPropertyName("sourceId")] public string SourceId { get; set; } = "";
    [JsonPropertyName("op")] public string Op { get; set; } = "";
    [JsonPropertyName("value")] public double Value { get; set; }
    [JsonPropertyName("priority")] public int Priority { get; set; }
    [JsonPropertyName("detailJson")] public string? DetailJson { get; set; }
}

public sealed class PvzStatsChannelSummaryDto
{
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    [JsonPropertyName("final")] public double Final { get; set; }
    [JsonPropertyName("sourceCount")] public int SourceCount { get; set; }
}

/// <summary>Derived monitor sheet — cache only, never re-apply as SSOT.</summary>
public sealed class PvzStatsSheetDto
{
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    [JsonPropertyName("revision")] public long Revision { get; set; }
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";
    [JsonPropertyName("channels")] public List<PvzStatsChannelSummaryDto> Channels { get; set; } = new();
}

public sealed class PvzStatsChannelDetailDto
{
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    [JsonPropertyName("revision")] public long Revision { get; set; }
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    [JsonPropertyName("final")] public double Final { get; set; }
    [JsonPropertyName("contributions")] public List<PvzStatContributionDto> Contributions { get; set; } = new();
}

public sealed class PvzStatsModifiersDto
{
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    [JsonPropertyName("revision")] public long Revision { get; set; }
    [JsonPropertyName("modifiers")] public List<PvzStatModifierDto> Modifiers { get; set; } = new();
}
