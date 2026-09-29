using System.Text.Json.Serialization;

namespace FusionRpg.Contracts;

/// <summary>UniqueActor durable specimen phases (Cold Server / Data). W4.</summary>
public static class UniqueActorPhases
{
    public const string Roster = "Roster";
    public const string Deploying = "Deploying";
    public const string ActiveBound = "ActiveBound";
    public const string Recovering = "Recovering";
    public const string Retired = "Retired";
}

public sealed class UniqueActorDto
{
    [JsonPropertyName("instanceId")] public string InstanceId { get; set; } = "";
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    /// <summary>`save-identity` SE4.31 — the empire that owns this specimen (`rpg_unique_actors.empire_id`).
    /// `playerId` stays and means the SAVE; this is the owner inside it. Null only for a row the
    /// migration has not stamped (pre-`save-identity` history, or an unseeded row), never a guess.</summary>
    [JsonPropertyName("empireId")] public string? EmpireId { get; set; }
    [JsonPropertyName("side")] public string Side { get; set; } = "";
    [JsonPropertyName("typeId")] public int TypeId { get; set; }
    [JsonPropertyName("phase")] public string Phase { get; set; } = UniqueActorPhases.Roster;
    [JsonPropertyName("level")] public long Level { get; set; } = 1;
    [JsonPropertyName("xp")] public long Xp { get; set; }
    [JsonPropertyName("matchKey")] public string? MatchKey { get; set; }
    [JsonPropertyName("lastPtr")] public string? LastPtr { get; set; }
    [JsonPropertyName("deployCorrelationId")] public string? DeployCorrelationId { get; set; }
    [JsonPropertyName("revision")] public long Revision { get; set; }
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";
}

public sealed class UniqueActorListDto
{
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    [JsonPropertyName("items")] public List<UniqueActorDto> Items { get; set; } = new();
}

public sealed class CreateUniqueActorRequest
{
    [JsonPropertyName("playerId")] public long? PlayerId { get; set; }
    [JsonPropertyName("side")] public string Side { get; set; } = "plant";
    [JsonPropertyName("typeId")] public int TypeId { get; set; }
}

public sealed class DeployUniqueActorRequest
{
    [JsonPropertyName("playerId")] public long? PlayerId { get; set; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; set; }
    [JsonPropertyName("col")] public int? Col { get; set; }
    [JsonPropertyName("row")] public int? Row { get; set; }
    [JsonPropertyName("matchKey")] public string? MatchKey { get; set; }
    /// <summary>Optional minimal loadout JSON (absolutes + grants). Merged with mods stub.</summary>
    [JsonPropertyName("loadoutJson")] public string? LoadoutJson { get; set; }
}

public sealed class UniqueActorDeployResultDto
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("queued")] public bool Queued { get; set; }
    [JsonPropertyName("correlationId")] public string CorrelationId { get; set; } = "";
    [JsonPropertyName("actor")] public UniqueActorDto? Actor { get; set; }
}

public sealed class UniqueEquipmentSlotDto
{
    [JsonPropertyName("slot")] public string Slot { get; set; } = "";
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = "";
}

public sealed class UniqueEquipmentListDto
{
    [JsonPropertyName("instanceId")] public string InstanceId { get; set; } = "";
    [JsonPropertyName("phase")] public string Phase { get; set; } = "";
    [JsonPropertyName("items")] public List<UniqueEquipmentSlotDto> Items { get; set; } = new();
    [JsonPropertyName("modsJson")] public string ModsJson { get; set; } = "{}";
}

public sealed class PutUniqueEquipmentRequest
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = "";
}

public sealed class AwardUniqueActorXpRequest
{
    // XP is an integer magnitude end to end (docs/architecture/numeric-types.md numeric rules): long on the wire, long in
    // the store, INTEGER in the column. Was `double` until 2026-09-05 -- the same defect the
    // 2026-09-04 XP pass fixed on rpg_actor_progression, surviving on the sibling table.
    [JsonPropertyName("delta")] public long Delta { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

/// <summary>A real, seeded relic definition (T14). Equipping persists as a module 4
/// <c>rpg_item_assignment</c> row (`decision-d1-durable-ownership.md` §10 M1/M2, 2026-09-06);
/// <see cref="Slot"/> stays a legacy <c>weapon|armor|trinket</c> label on the wire and is mapped to
/// its canonical role in the store — see <see cref="UniqueEquipmentSlotDto"/>.</summary>
public sealed class RelicDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("rarity")] public int Rarity { get; set; } = 1;
    [JsonPropertyName("slot")] public string Slot { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("effectId")] public string EffectId { get; set; } = "";
}

public sealed class RelicCatalogListDto
{
    [JsonPropertyName("items")] public List<RelicDto> Items { get; set; } = new();
}
