using System.Text.Json.Serialization;

namespace FusionRpg.Contracts;

/// <summary>Creature profile — the identity superset over a UniqueActor specimen (spec-creature-core.md).</summary>
public sealed class CreatureProfileDto
{
    [JsonPropertyName("instanceId")] public string InstanceId { get; set; } = "";
    [JsonPropertyName("speciesId")] public string SpeciesId { get; set; } = "";
    [JsonPropertyName("rarity")] public string Rarity { get; set; } = "chaff";
    /// <summary>spec-species-rank.md §5: the specimen's species' rank id, copied off the runtime
    /// species catalog (never re-derived). Null when the pipeline skipped it (unresolved input) or
    /// the catalog does not know the species — never a fabricated bottom rung.</summary>
    [JsonPropertyName("rank")] public string? Rank { get; set; }
    /// <summary>spec-species-rank.md §5: the player-facing display copy beside <see cref="Rank"/>;
    /// null exactly when <see cref="Rank"/> is.</summary>
    [JsonPropertyName("rankDisplayName")] public string? RankDisplayName { get; set; }
    [JsonPropertyName("variant")] public string Variant { get; set; } = "normal";
    [JsonPropertyName("elementPrimary")] public string ElementPrimary { get; set; } = "";
    [JsonPropertyName("elementSecondary")] public string? ElementSecondary { get; set; }
    [JsonPropertyName("traitIds")] public List<string> TraitIds { get; set; } = new();
    [JsonPropertyName("origin")] public string Origin { get; set; } = "";
    [JsonPropertyName("nickname")] public string? Nickname { get; set; }
    [JsonPropertyName("locked")] public bool Locked { get; set; }
    [JsonPropertyName("star")] public int Star { get; set; }
    [JsonPropertyName("promoted")] public bool Promoted { get; set; }
    [JsonPropertyName("createdUtc")] public string CreatedUtc { get; set; } = "";
    [JsonPropertyName("revision")] public long Revision { get; set; }
}

/// <summary>Roster row: the specimen (actor) with its creature profile.</summary>
public sealed class CreatureSpecimenDto
{
    [JsonPropertyName("actor")] public UniqueActorDto Actor { get; set; } = new();
    [JsonPropertyName("profile")] public CreatureProfileDto Profile { get; set; } = new();
}

public sealed class CreatureRosterDto
{
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    [JsonPropertyName("items")] public List<CreatureSpecimenDto> Items { get; set; } = new();
}

public static class CreatureCodexStates
{
    public const string Seen = "seen";
    public const string Discovered = "discovered";
}

public sealed class CreatureCodexEntryDto
{
    [JsonPropertyName("speciesId")] public string SpeciesId { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = CreatureCodexStates.Seen;
    /// <summary>spec-species-rank.md §5: the species' rank id, copied off the runtime species
    /// catalog (never re-derived). Null when the pipeline skipped it or the catalog does not
    /// know the species — never a fabricated bottom rung.</summary>
    [JsonPropertyName("rank")] public string? Rank { get; set; }
    /// <summary>spec-species-rank.md §5: the player-facing display copy beside <see cref="Rank"/>;
    /// null exactly when <see cref="Rank"/> is.</summary>
    [JsonPropertyName("rankDisplayName")] public string? RankDisplayName { get; set; }
    [JsonPropertyName("firstUtc")] public string FirstUtc { get; set; } = "";
    [JsonPropertyName("updatedUtc")] public string UpdatedUtc { get; set; } = "";
}

public sealed class CreatureCodexDto
{
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    [JsonPropertyName("entries")] public List<CreatureCodexEntryDto> Entries { get; set; } = new();
}

/// <summary>Mint request fields (server-internal callers: summoning, capture, fusion, seed).</summary>
public sealed class CreatureMintSpec
{
    public string SpeciesId { get; set; } = "";
    public string Side { get; set; } = "";
    public int GameTypeId { get; set; }
    public string Rarity { get; set; } = "chaff";
    public string Variant { get; set; } = "normal";
    public string ElementPrimary { get; set; } = "";
    public string? ElementSecondary { get; set; }
    public List<string> TraitIds { get; set; } = new();
    public string Origin { get; set; } = "";
    public string? Nickname { get; set; }
}
