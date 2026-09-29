using System.Text.Json.Serialization;

namespace FusionRpg.Contracts;

public sealed class CommanderListResponse
{
    [JsonPropertyName("defaultLawnCommanderId")] public string DefaultLawnCommanderId { get; set; } = "";
    [JsonPropertyName("commanders")] public List<CommanderListRowDto> Commanders { get; set; } = new();
}

public sealed class CommanderListRowDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("isDefault")] public bool IsDefault { get; set; }
    [JsonPropertyName("activeAuraId")] public string? ActiveAuraId { get; set; }
    [JsonPropertyName("activeAuraName")] public string? ActiveAuraName { get; set; }
    [JsonPropertyName("locationStub")] public string? LocationStub { get; set; }
    [JsonPropertyName("legionStub")] public string? LegionStub { get; set; }
    [JsonPropertyName("equipment")] public CommanderEquipmentDto? Equipment { get; set; }
}

public sealed class CommanderEquipmentDto
{
    [JsonPropertyName("instanceId")] public string InstanceId { get; set; } = "";
    [JsonPropertyName("containerId")] public string ContainerId { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "standard";
}

public sealed class SetDefaultLawnCommanderRequest
{
    [JsonPropertyName("playerId")] public long? PlayerId { get; set; }
    [JsonPropertyName("commanderId")] public string? CommanderId { get; set; }
}

public sealed class DefaultLawnCommanderResponse
{
    [JsonPropertyName("defaultLawnCommanderId")] public string DefaultLawnCommanderId { get; set; } = "";
}

/// <summary>EP3.3 (`commander-roster`, spec-commander-roster.md "Routes"): grant or revoke the commander
/// role on a specimen. `grant: false` revokes. The role is a binding — the creature is unchanged either
/// way — and a revoke of the seated default resets the default in the same transaction.</summary>
public sealed class SetCommanderRoleRequest
{
    [JsonPropertyName("playerId")] public long? PlayerId { get; set; }
    [JsonPropertyName("instanceId")] public string? InstanceId { get; set; }
    [JsonPropertyName("grant")] public bool Grant { get; set; }
}

/// <summary>The result of a grant/revoke: the specimen, which way it went, and the save's default lawn
/// commander afterwards (so a caller that just unseated the default sees the reset without a second
/// read).</summary>
public sealed class CommanderRoleResponse
{
    [JsonPropertyName("instanceId")] public string InstanceId { get; set; } = "";
    [JsonPropertyName("granted")] public bool Granted { get; set; }
    [JsonPropertyName("defaultLawnCommanderId")] public string DefaultLawnCommanderId { get; set; } = "";
}
