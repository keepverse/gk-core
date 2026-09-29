using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;

namespace FusionRpg.Core.Items.Drops;

/// <summary>
/// The <c>world-sector</c> loot source, resolved at RUNTIME from a sector's live danger band.
///
/// <para><b>What changed, and why this file exists.</b> Module 11 shipped the
/// <c>drop.world.sector-clear</c> table (Correction 1 calibrates it at 1.50) with no
/// <c>loot_source</c> pointing at it, because <c>sectorLevel(danger_band)</c> was recorded as owed by
/// the world program. It is not owed: <c>ssot-power-scale.md</c> §5.3/§10.3 closed it by owner
/// decision on 2026-08-23 as <c>mapLevel(M) = Wm · DangerBand(M)</c> with <c>Wm = 5</c> derived from
/// the shipped <c>SectorTypeCatalog</c>, and <c>spec-content-authoring.md</c> §2.1 (owner approved
/// 2026-08-24) names the identical formula for this exact <c>contentLevel</c> row. What was missing
/// was the code. <see cref="PowerIndexComposer.MapLevel"/> is now that code, and this file is the one
/// call site the loot lane needs — no private <c>f(level)</c> is declared here.</para>
///
/// <para>⛔ <b>The row is resolved, never authored, and that is forced by the idempotency gate.</b>
/// <c>LootCorrelation.Derive("world-sector", sourceId)</c> is <c>loot:sector:{sourceId}</c>, and the
/// pipeline keys step 1 on <c>(player_id, correlation_id)</c>. Authoring eight static rows in
/// <c>gk-data/packs/fusion/data/seed/loot/tables.v1.json</c> — one per SECTOR TYPE, the shape the other eight sources use —
/// would make every sector of a type share one correlation id, so the second <c>boss-lair</c> a player
/// cleared would replay the first and mint nothing. The sector's own id has to be the key, and a
/// sector id is generated per world, so no seed file can hold it. The danger band is live state
/// (<c>SectorState.DangerBand</c>) rather than authored content for the same reason.</para>
///
/// <para>This type stays free of <c>FusionRpg.Core.World</c> on purpose: it takes the band as an
/// <c>int</c>, so the loot lane never has to load a world to price a sector. The tests are what walk
/// the real <c>SectorTypeCatalog</c>.</para>
/// </summary>
public static class WorldSectorLootSource
{
    static WorldSectorLootSource() => ContentRuleNamespaces.Register("drop");

    /// <summary>One of <c>DropTableValidator.KnownSourceKinds</c>, reserved since module 11.</summary>
    public const string SourceKind = "world-sector";

    /// <summary>Correction 1's 1.50-yield table — the ORIGINAL, shared table every sector type drew
    /// from until `sector-loot-wiring` (2026-09-07) split it per type. Kept only as the historical
    /// citation for that yield calibration; <see cref="TableIdFor"/> is what every real caller uses
    /// now.</summary>
    public const string SectorClearTableId = "drop.world.sector-clear";

    /// <summary>
    /// `drop-tables` `sector-loot-wiring` (2026-09-07): one real table PER `SectorTypeCatalog` entry,
    /// closing the "every sector type shares one undifferentiated pool" gap the whole drop-tables
    /// initiative names as its own reason to exist. `sectorTypeId` is never validated against
    /// `SectorTypeCatalog` here — this file stays free of `FusionRpg.Core.World` on purpose (its own
    /// class doc) — an unauthored type is refused downstream, at import, by
    /// `DropTableValidator.ValidateSource`'s existing "table not found" check.
    /// </summary>
    public static string TableIdFor(string sectorTypeId) => $"{SectorClearTableId}.{sectorTypeId}";

    /// <summary>
    /// The loot source for clearing one sector, or a rejection naming why there is none.
    ///
    /// <para>⛔ <b>A band that prices below content level 1 is refused BY NAME, never floored.</b>
    /// The decided formula yields 0 at danger band 0 — <c>SectorTypeCatalog</c>'s homeworld, whose own
    /// comment reads "0 = safe ground" — and <c>DropTableValidator.ValidateSource</c> already refuses
    /// <c>content_level &lt; 1</c> with "item level is content, and content starts at 1". Flooring to
    /// 1 here would invent a content level the owner decision does not contain, which is the exact
    /// defect the <c>pvz-run</c> refusal exists to avoid. Safe ground is not content to clear.</para>
    /// </summary>
    public static AtomRejection TryResolve(
        string sectorId, int dangerBand, string sectorTypeId, PowerTuning tuning, out LootSourceRow? source)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        source = null;

        if (string.IsNullOrWhiteSpace(sectorId))
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                "a world-sector loot source needs the sector's OWN id — the correlation id is derived "
                + "from it (§4.4), and a shared id would make two sectors one loot event");

        if (string.IsNullOrWhiteSpace(sectorTypeId))
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                "a world-sector loot source needs the sector's own type id — the table is per-type "
                + "since sector-loot-wiring (2026-09-07), never one shared table");

        var contentLevel = PowerIndexComposer.MapLevel(dangerBand, tuning);
        if (contentLevel < 1)
            return AtomRejection.ContentRule("drop.sector-band-safe",
                $"sector '{sectorId}' is at danger band {dangerBand}; mapLevel({dangerBand}) = {contentLevel} "
                + "(ssot-power-scale.md §5.3/§10.3), and content starts at 1. Band 0 is safe ground — "
                + "refused by name rather than floored to 1, which would invent a level the decision "
                + "does not contain");

        source = new LootSourceRow(SourceKind, sectorId, TableIdFor(sectorTypeId), contentLevel);
        return AtomRejection.Ok;
    }
}
