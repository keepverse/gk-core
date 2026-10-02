namespace FusionRpg.Core.Creatures.Generation;

/// <summary>
/// Record shapes for `creature-seed` module 1 (`corpus-dump`, spec-corpus-dump.md). Held here, next
/// to the game code rather than inside the console tool, so a test can construct and compare them
/// without spawning a process — the same split `CreatureCorpusEmit`/`CreatureCorpusBuilder` already use.
/// </summary>
public static class DumpFormat
{
    /// <summary>Bumped by hand when the on-disk shape changes. Ask-first per spec-corpus-dump.md.</summary>
    public const int Version = 1;
}

public sealed record DumpAlmanacRow(
    string Side,
    int TypeId,
    string? TypeName,
    string? DisplayName,
    string? FlavorInfo,
    string? FlavorIntroduce,
    int? SunCost,
    double? CooldownSec,
    string CostStatus,
    long? Hp,
    long? Attack,
    long? Armor,
    long? ArmorMax,
    bool StatsObserved,
    int ContractVersion,
    string RebuiltUtc,
    DumpEnrichment? Enrichment);

public sealed record DumpEnrichment(
    string[]? Qualities,
    string? UnlockCondition,
    string? TypeClass,
    string? WeaknessesText,
    string? DamageVsText,
    string? Description,
    string Source);

public sealed record DumpSpawnBaseline(
    string Side,
    int TypeId,
    string StatsJson,
    string CapturedUtc);

public sealed record DumpRecipe(
    int ParentA,
    string? ParentAName,
    int ParentB,
    string? ParentBName,
    int Result,
    string? ResultName);

/// <summary>Everything <c>corpus-dump</c> exports, already sorted the way the writer requires it.</summary>
public sealed record DumpPayload(
    IReadOnlyList<DumpAlmanacRow> PlantAlmanac,
    IReadOnlyList<DumpAlmanacRow> ZombieAlmanac,
    IReadOnlyList<DumpSpawnBaseline> SpawnBaselines,
    IReadOnlyList<DumpRecipe> Recipes);

/// <summary>
/// The committed envelope (`_manifest.json`). <see cref="CapturedUtc"/> is the store's own
/// <c>max(RebuiltUtc)</c> — never wall-clock time (spec-corpus-dump.md §2).
///
/// <para><b>Two hashes, because they answer different questions.</b>
/// <see cref="ContentHash"/> is over the payload files' raw bytes: it answers "are the bytes on disk
/// the bytes I wrote", and any drift in ANY field — including a timestamp — must move it, or
/// <c>--verify</c> would wave through a re-captured tree. <see cref="DataHash"/> is the same four files
/// with the volatile stamp fields normalised to a fixed sentinel: it answers "did the GAME DATA
/// change", which is the question a generator's staleness key asks.</para>
///
/// <para>They had to be split because the payloads carry 986 stamp fields (677 plant + 227 zombie
/// rebuiltUtc, 82 spawn-baseline capturedUtc), so a single hash made every derived record stale every
/// time the dump was re-captured — even with no data change. Measured: changing only the timestamps
/// moved the old hash while a one-field data change was indistinguishable from it. spec-anchor-emit.md
/// already requires staleness to be "compared by recorded value, not by timestamp", and this is the
/// field that finally lets an implementation honour that.</para>
/// </summary>
public sealed record DumpManifest(
    int DumpFormatVersion,
    string CapturedUtc,
    string ContentHash,
    string DataHash,
    int PlantCount,
    int ZombieCount,
    int BaselineCount,
    int RecipeCount);

/// <summary>
/// One row of the game's own static base-stat table (<c>type_base_stats</c>), captured by the
/// injector's enum sweep (<c>GameHooks.EnqueueBaseStats</c>) and exported here so a generator can
/// read measured game stats WITHOUT opening the local, uncommitted <c>rpg-hot.sqlite</c>.
///
/// <para><see cref="StatsJson"/> is carried verbatim as the store holds it — a string, not parsed
/// columns — for the same reason <c>RpgStore.TypeBaseStats</c> stores it that way: the field set
/// differs per side and may grow with a game update, and every consumer reads named keys out of it
/// anyway. Re-serialising it into typed columns here would add a migration surface this dump does
/// not need and would break the byte-round-trip <c>--verify</c> depends on.</para>
/// </summary>
public sealed record DumpTypeBaseStats(
    string Side,
    int TypeId,
    string? TypeName,
    string StatsJson,
    string CapturedUtc);

/// <summary>
/// The committed <c>type-base-stats.json</c> envelope. Deliberately SELF-CONTAINED — its own
/// <see cref="ContentHash"/> covers only its own entries, and it is NOT folded into
/// <see cref="DumpManifest.ContentHash"/>.
///
/// <para>The reason is a different capture, not a stylistic one: the four almanac/baseline/recipe
/// payload files are one snapshot of <c>almanac_seed</c> (captured 2026-08-23); the static
/// base-stat sweep is an independent read of the game's own enums (captured 2026-09-17). Folding
/// the second into the first's hash would force regenerating all four files — a large, unrelated
/// diff — to publish a capture that shares none of their rows, and would invalidate every anchor's
/// recorded <c>dumpHash</c> for a change none of those anchors were derived from.</para>
/// </summary>
public sealed record DumpTypeBaseStatsFile(
    int DumpFormatVersion,
    string CapturedUtc,
    string ContentHash,
    int PlantCount,
    int ZombieCount,
    IReadOnlyList<DumpTypeBaseStats> Entries);
