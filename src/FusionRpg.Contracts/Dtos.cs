using System.Globalization;
using System.Text.Json.Serialization;

namespace FusionRpg.Contracts;

public sealed class StatMod
{
    [JsonPropertyName("hpPercent")] public float HpPercent { get; set; } = 1f;
    [JsonPropertyName("hpFlat")] public long HpFlat { get; set; }
    [JsonPropertyName("attackPercent")] public float AttackPercent { get; set; } = 1f;
    [JsonPropertyName("attackFlat")] public long AttackFlat { get; set; }
    [JsonPropertyName("defensePercent")] public float DefensePercent { get; set; } = 1f;
    [JsonPropertyName("defenseFlat")] public long DefenseFlat { get; set; }
}

public sealed class StatsConfig
{
    [JsonPropertyName("plants")] public StatMod Plants { get; set; } = new();
    [JsonPropertyName("zombies")] public StatMod Zombies { get; set; } = new();
    // Per-hit telemetry is opt-in: the old default=true made every player pay per-hit
    // *.damage + combat.hit emission unless a server pull happened to say otherwise.
    [JsonPropertyName("logDamage")] public bool LogDamage { get; set; }
    [JsonPropertyName("applyStats")] public bool ApplyStats { get; set; } = true;
}

public sealed class EventEnvelope
{
    [JsonPropertyName("id")] public long? Id { get; set; }
    [JsonPropertyName("t")] public string T { get; set; } = "";
    [JsonPropertyName("game")] public string Game { get; set; } = "pvzrh-3.8.1";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("matchKey")] public string? MatchKey { get; set; }
    [JsonPropertyName("playerId")] public long? PlayerId { get; set; }
    [JsonPropertyName("runId")] public long? RunId { get; set; }
    [JsonPropertyName("payload")] public object? Payload { get; set; }
}

public sealed class EventBatch
{
    [JsonPropertyName("events")] public List<EventEnvelope>? Events { get; set; }
}

public sealed class HealthDto
{
    [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
    [JsonPropertyName("injectorConnected")] public bool InjectorConnected { get; set; }
    [JsonPropertyName("lastHeartbeatUtc")] public string? LastHeartbeatUtc { get; set; }
    [JsonPropertyName("simEnabled")] public bool SimEnabled { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "none";
    [JsonPropertyName("currentPlayerId")] public long CurrentPlayerId { get; set; }
    [JsonPropertyName("ingestQueued")] public int IngestQueued { get; set; }
    [JsonPropertyName("lastFlushMs")] public double LastFlushMs { get; set; }
    /// <summary>Events the ingest writer could not store since the server started (lawn-combat-wire L-N26/L-N29). A failed
    /// writer batch is retried one event at a time, so this counts only the events that failed on their own.</summary>
    [JsonPropertyName("ingestDroppedEvents")] public long IngestDroppedEvents { get; set; }

    // E46 (player-content-boot): a player install never ran the seed importer, so its content tables
    // stayed empty forever and it ran on the shipped code fallback with nobody able to tell. These
    // three fields are that mode reported on a surface both the player and the owner can read — see
    // FusionRpg.Data.Seed.SeedImportRunner and RpgStore.RecordContentBootOutcome.
    /// <summary>"imported" once the catalog tables genuinely hold content (this launch or an earlier
    /// one); "codeFallback" while nothing has ever been imported successfully.</summary>
    [JsonPropertyName("contentSource")] public string ContentSource { get; set; } = "codeFallback";
    [JsonPropertyName("catalogRevision")] public long CatalogRevision { get; set; }
    /// <summary>Why the self-healing startup import did not run or did not succeed. Null when content
    /// is imported — a failed or skipped import is reported here, never only logged.</summary>
    [JsonPropertyName("contentImportError")] public string? ContentImportError { get; set; }
}

public sealed class HeartbeatDto
{
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("game")] public string Game { get; set; } = "pvzrh-3.8.1";
}

public sealed class ProbeRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("scenario")] public string? Scenario { get; set; }
    [JsonPropertyName("data")] public object? Data { get; set; }
}

public sealed class MetricItem
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("value")] public double Value { get; set; }
    [JsonPropertyName("ts")] public string Ts { get; set; } = "";
}

public sealed class RunItem
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("playerId")] public long PlayerId { get; set; }
    [JsonPropertyName("matchKey")] public string? MatchKey { get; set; }
    [JsonPropertyName("startedUtc")] public string StartedUtc { get; set; } = "";
    [JsonPropertyName("endedUtc")] public string? EndedUtc { get; set; }
    [JsonPropertyName("levelName")] public string? LevelName { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("mowersUsed")] public int MowersUsed { get; set; }
    [JsonPropertyName("plantsPlanted")] public int PlantsPlanted { get; set; }
    [JsonPropertyName("plantsDied")] public int PlantsDied { get; set; }
    [JsonPropertyName("zombiesKilled")] public int ZombiesKilled { get; set; }
    [JsonPropertyName("summary")] public object? Summary { get; set; }
    [JsonPropertyName("levelType")] public string? LevelType { get; set; }
    [JsonPropertyName("boardLevel")] public int? BoardLevel { get; set; }
    [JsonPropertyName("modifiers")] public object? Modifiers { get; set; }
    [JsonPropertyName("archiveUri")] public string? ArchiveUri { get; set; }
    /// <summary>Game profile that produced this run (pvzrh-* or webrpg-1). Legacy rows default to pvzrh.</summary>
    [JsonPropertyName("game")] public string Game { get; set; } = RpgConstants.GameId;
}

public sealed class RecipeItem
{
    [JsonPropertyName("parentA")] public int ParentA { get; set; }
    [JsonPropertyName("parentAName")] public string? ParentAName { get; set; }
    [JsonPropertyName("parentB")] public int ParentB { get; set; }
    [JsonPropertyName("parentBName")] public string? ParentBName { get; set; }
    [JsonPropertyName("result")] public int Result { get; set; }
    [JsonPropertyName("resultName")] public string? ResultName { get; set; }
}

public sealed class SpawnStatItem
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("runId")] public long RunId { get; set; }
    [JsonPropertyName("ptr")] public string Ptr { get; set; } = "";
    [JsonPropertyName("side")] public string Side { get; set; } = "";
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("capturedUtc")] public string CapturedUtc { get; set; } = "";
    [JsonPropertyName("stats")] public object? Stats { get; set; }
}

public sealed class PlayerDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("createdUtc")] public string CreatedUtc { get; set; } = "";

    /// <summary>"The whole save"'s own root (spec-world-seed.md, T5.1) — rolled once at creation,
    /// surfaced here so the UI can show/share it (Q7). Every per-player roll this program and
    /// creature-seed make derives from this value; it is never regenerated for an existing player.</summary>
    [JsonPropertyName("worldSeed")] public long WorldSeed { get; set; }
}

public sealed class CreatePlayerRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

/// <summary>
/// `save-identity` SE4.31 — one empire of a save, as data
/// (`GET /api/players/{playerId}/empires`). <c>controller</c> is the closed two-member vocabulary the
/// authored registry and <c>rpg_save_empires.controller</c> already carry (`human` | `ai`); a third
/// kind of decider is a design change, not a new value.
/// </summary>
public sealed class SaveEmpireDto
{
    [JsonPropertyName("empireId")] public string EmpireId { get; set; } = "";
    [JsonPropertyName("controller")] public string Controller { get; set; } = "";
}

public sealed class SelectPlayerRequest
{
    [JsonPropertyName("id")] public long Id { get; set; }
}

public sealed class PlayersListDto
{
    [JsonPropertyName("items")] public List<PlayerDto> Items { get; set; } = new();
    [JsonPropertyName("currentPlayerId")] public long CurrentPlayerId { get; set; }
}

public sealed class TypeItem
{
    [JsonPropertyName("game")] public string Game { get; set; } = RpgConstants.GameId;
    [JsonPropertyName("side")] public string Side { get; set; } = "";
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("typeName")] public string? TypeName { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("sampleJson")] public string? SampleJson { get; set; }
    [JsonPropertyName("hpBase")] public long? HpBase { get; set; }
    [JsonPropertyName("maxHpBase")] public long? MaxHpBase { get; set; }
    [JsonPropertyName("attackBase")] public long? AttackBase { get; set; }
    [JsonPropertyName("armorBase")] public long? ArmorBase { get; set; }
    [JsonPropertyName("armorMaxBase")] public long? ArmorMaxBase { get; set; }
    [JsonPropertyName("seenCount")] public int SeenCount { get; set; }
    [JsonPropertyName("killedCount")] public int KilledCount { get; set; }
    [JsonPropertyName("firstSeenUtc")] public string? FirstSeenUtc { get; set; }
    [JsonPropertyName("lastSeenUtc")] public string? LastSeenUtc { get; set; }
}

public sealed class HelloDto
{
    [JsonPropertyName("game")] public string Game { get; set; } = "pvzrh-3.8.1";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    /// <summary>Additive 2026-09-15: the injector's own debug-session state at Hello. The server keeps an in-memory
    /// mirror that a server restart resets while the game (and its session) lives on; Hello re-syncs it.</summary>
    [JsonPropertyName("debugSessionActive")] public bool DebugSessionActive { get; set; }
    [JsonPropertyName("debugScenarioId")] public string? DebugScenarioId { get; set; }
}

public sealed class CommandDto
{
    /// <summary>Dedupes SignalR + HTTP inbox delivery.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("payload")] public object? Payload { get; set; }
}

/// <summary>
/// Recovery edge for the web lawn. The snapshot itself remains the existing
/// <see cref="EventEnvelope"/> (<c>debug.snapshot</c>) event; this small status message tells a
/// client whether that authoritative edge is still pending, failed, or has arrived.
/// </summary>
public sealed class LawnRecoveryStatusDto
{
    [JsonPropertyName("state")] public string State { get; set; } = "loading";
    [JsonPropertyName("matchKey")] public string? MatchKey { get; set; }
    [JsonPropertyName("snapshotId")] public long? SnapshotId { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class StorageSummaryDto
{
    [JsonPropertyName("archiveCount")] public long ArchiveCount { get; set; }
    [JsonPropertyName("closedRunsStillHot")] public long ClosedRunsStillHot { get; set; }
    [JsonPropertyName("openRuns")] public long OpenRuns { get; set; }
    [JsonPropertyName("activityOverTail")] public bool ActivityOverTail { get; set; }
    [JsonPropertyName("xpOverTail")] public bool XpOverTail { get; set; }
}

public sealed class StorageArchiveItemDto
{
    [JsonPropertyName("uri")] public string Uri { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("runId")] public long? RunId { get; set; }
    [JsonPropertyName("createdUtc")] public string CreatedUtc { get; set; } = "";
}

public sealed class StorageUrisRequest
{
    [JsonPropertyName("uris")] public List<string> Uris { get; set; } = new();
}

public sealed class StorageRunIdsRequest
{
    [JsonPropertyName("runIds")] public List<long> RunIds { get; set; } = new();
}

public sealed class StoragePurgeResultDto
{
    [JsonPropertyName("deleted")] public int Deleted { get; set; }
    [JsonPropertyName("refused")] public int Refused { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public static class RpgConstants
{
    /// <summary>Legacy default / 3.8.1 profile id. Prefer <see cref="GameId381"/> or injector runtime profile.</summary>
    public const string GameId = GameId381;
    public const string GameId381 = "pvzrh-3.8.1";
    public const string GameId39 = "pvzrh-3.9";
    /// <summary>
    /// Web-mode game profile: matches produced by the server's own battle resolver
    /// (standalone-charter). Event vocabulary / runs only — never a game-profiles.json
    /// entry (that catalog drives launcher fingerprint matching).
    /// </summary>
    public const string GameIdWebRpg = "webrpg-1";
    public const string InjectorGroup = "injector";
    public const string WebGroup = "web";
    /// <summary>Server → web status for the one authoritative lawn snapshot recovery edge.</summary>
    public const string LawnRecoveryEvent = "LawnRecovery";
    /// <summary>Existing injector command that emits the authoritative <c>debug.snapshot</c> event.</summary>
    public const string SnapshotCommand = "debug.snapshot";
    /// <summary>player-routing spec §1, R-N1/R17 - a per-save SignalR group. "Player" on the wire
    /// means the save; a connection joins the save it is showing, not necessarily the server-wide
    /// `current_player_id` (this constant's own consumers never read that setting). Not an auth
    /// boundary (player-routing spec "What this is not") - localhost, no auth, routing only.</summary>
    public const string PlayerGroupPrefix = "player:";
    public static string PlayerGroup(long playerId) => PlayerGroupPrefix + playerId.ToString(CultureInfo.InvariantCulture);
    public const string SourceNone = "none";
    public const string SourceSim = "sim";
    public const string SourceInjector = "injector";
    public const string SourceWeb = "web";
    public const string SimEnvVar = "FUSIONRPG_SIM";

    /// <summary>
    /// Game-mode classifier (standalone charter): null/empty = legacy injector = PvZ.
    /// SSOT for the predicate — the SQL mirror in RpgStore.Compaction.cs must match.
    /// </summary>
    public static bool IsPvzGame(string? game) =>
        string.IsNullOrEmpty(game) || game.StartsWith("pvzrh", StringComparison.Ordinal);

    public static bool IsNoisyKind(string? kind) =>
        kind is "plant.damage" or "zombie.damage" or "bullet.init" or "bullet.place" or "item.drop" or "pet.xp"
            or "shield.absorbed";

    public static bool IsDroppableWhenFull(string? kind) => IsNoisyKind(kind);
}
