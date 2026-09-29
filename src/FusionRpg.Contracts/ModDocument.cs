using System.Text.Json.Serialization;

namespace FusionRpg.Contracts;

/// <summary>
/// Revisioned mod document — SSOT for cheats now and RPG loadouts later.
/// Absence of a field means unset (never encode unset as -1 / 0 / 1).
/// </summary>
public sealed class ModDocument
{
    [JsonPropertyName("revision")] public long Revision { get; set; }
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "web";
    [JsonPropertyName("mods")] public List<ModEntry> Mods { get; set; } = new();
}

public sealed class ModEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    [JsonPropertyName("op")] public string Op { get; set; } = "";
    [JsonPropertyName("value")] public double? Value { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}
