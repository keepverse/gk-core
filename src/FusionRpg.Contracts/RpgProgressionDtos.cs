using System.Text.Json.Serialization;

namespace FusionRpg.Contracts;

public sealed class RpgActorProgressionDto
{
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("typeId")] public int TypeId { get; set; }
    [JsonPropertyName("typeName")] public string? TypeName { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    /// <summary>Promoted PlantInfo/ZombieInfo.info from almanac dump.</summary>
    [JsonPropertyName("almanacInfo")] public string? AlmanacInfo { get; set; }
    /// <summary>Promoted ZombieInfo.introduce (flavor) from almanac dump.</summary>
    [JsonPropertyName("almanacIntroduce")] public string? AlmanacIntroduce { get; set; }
    /// <summary>Promoted PlantInfo.cost (sun/cooldown) from almanac dump.</summary>
    [JsonPropertyName("almanacCost")] public string? AlmanacCost { get; set; }
    [JsonPropertyName("level")] public long Level { get; set; }
    [JsonPropertyName("xp")] public long Xp { get; set; }
    [JsonPropertyName("xpToNext")] public long XpToNext { get; set; }
    [JsonPropertyName("highestLevel")] public long HighestLevel { get; set; }
    [JsonPropertyName("demotionCount")] public long DemotionCount { get; set; }
    [JsonPropertyName("revision")] public long Revision { get; set; }
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";
    [JsonPropertyName("curveFirst")] public double CurveFirst { get; set; }
    [JsonPropertyName("curveStep")] public double CurveStep { get; set; }
}

public sealed class RpgProgressionListDto
{
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    [JsonPropertyName("items")] public List<RpgActorProgressionDto> Items { get; set; } = new();
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; }
    [JsonPropertyName("offset")] public int Offset { get; set; }
}

public sealed class RpgProgressionSummaryDto
{
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    [JsonPropertyName("player")] public RpgActorProgressionDto? Player { get; set; }
    [JsonPropertyName("plantActorCount")] public int PlantActorCount { get; set; }
    [JsonPropertyName("zombieActorCount")] public int ZombieActorCount { get; set; }
    [JsonPropertyName("highestPlantLevel")] public long HighestPlantLevel { get; set; }
    [JsonPropertyName("highestZombieLevel")] public long HighestZombieLevel { get; set; }
    [JsonPropertyName("topPlants")] public List<RpgActorProgressionDto> TopPlants { get; set; } = new();
    [JsonPropertyName("topZombies")] public List<RpgActorProgressionDto> TopZombies { get; set; } = new();
}

public sealed class RpgXpLedgerEntryDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("typeId")] public int TypeId { get; set; }
    [JsonPropertyName("typeName")] public string? TypeName { get; set; }
    [JsonPropertyName("runId")] public long RunId { get; set; }
    [JsonPropertyName("t")] public string T { get; set; } = "";
    [JsonPropertyName("delta")] public long Delta { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("activityFactId")] public long? ActivityFactId { get; set; }
    [JsonPropertyName("levelBefore")] public long LevelBefore { get; set; }
    [JsonPropertyName("xpBefore")] public long XpBefore { get; set; }
    [JsonPropertyName("levelAfter")] public long LevelAfter { get; set; }
    [JsonPropertyName("xpAfter")] public long XpAfter { get; set; }
    [JsonPropertyName("demotionBefore")] public long DemotionBefore { get; set; }
    [JsonPropertyName("demotionAfter")] public long DemotionAfter { get; set; }
    [JsonPropertyName("payloadJson")] public string? PayloadJson { get; set; }
}

public sealed class RpgXpLedgerPageDto
{
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    [JsonPropertyName("items")] public List<RpgXpLedgerEntryDto> Items { get; set; } = new();
    [JsonPropertyName("limit")] public int Limit { get; set; }
    [JsonPropertyName("nextAfterId")] public long? NextAfterId { get; set; }
}

public sealed class RpgXpReasonStatDto
{
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("sumDelta")] public double SumDelta { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
}

public sealed class RpgLevelBucketDto
{
    [JsonPropertyName("level")] public long Level { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
}

public sealed class RpgRecentDeltaDto
{
    [JsonPropertyName("t")] public string T { get; set; } = "";
    [JsonPropertyName("delta")] public long Delta { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

public sealed class RpgProgressionStatsDto
{
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    [JsonPropertyName("xpByReason")] public List<RpgXpReasonStatDto> XpByReason { get; set; } = new();
    [JsonPropertyName("plantLevels")] public List<RpgLevelBucketDto> PlantLevels { get; set; } = new();
    [JsonPropertyName("zombieLevels")] public List<RpgLevelBucketDto> ZombieLevels { get; set; } = new();
    [JsonPropertyName("recentDeltas")] public List<RpgRecentDeltaDto> RecentDeltas { get; set; } = new();
}
