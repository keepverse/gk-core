using System.Text.Json.Serialization;

namespace FusionRpg.Contracts;

public sealed class SoulBalanceDto
{
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    [JsonPropertyName("balance")] public long Balance { get; set; }
    [JsonPropertyName("earnedTotal")] public long EarnedTotal { get; set; }
    [JsonPropertyName("spentTotal")] public long SpentTotal { get; set; }
    [JsonPropertyName("revision")] public long Revision { get; set; }
    [JsonPropertyName("updatedUtc")] public string UpdatedUtc { get; set; } = "";
}

public sealed class SoulLedgerEntryDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("runId")] public long RunId { get; set; }
    [JsonPropertyName("delta")] public long Delta { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("refKind")] public string? RefKind { get; set; }
    [JsonPropertyName("refId")] public string? RefId { get; set; }
    [JsonPropertyName("t")] public string T { get; set; } = "";
}

public sealed class SoulLedgerDto
{
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    [JsonPropertyName("items")] public List<SoulLedgerEntryDto> Items { get; set; } = new();
}
