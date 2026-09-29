using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Power;

namespace FusionRpg.Core.World.Turn;

/// <summary>
/// `drop-tables` `siege-loot` (2026-09-07) — base-defense's first `loot_source` binding. Reuses the
/// exact same danger-band-to-content-level formula `WorldSectorLootSource` already established
/// (`PowerIndexComposer.MapLevel`) rather than authoring a private curve for district strength — a
/// district's own sector already carries a `DangerBand`, the same signal a sector claim reads.
///
/// <para>Unlike `WorldSectorLootSource` (deliberately free of `FusionRpg.Core.World`), this type lives
/// under `World.Turn` on purpose: it is called from `BattleReporting.Fight`, itself already inside the
/// world-turn engine, so there is no "loot lane loads a world" concern to guard against here.</para>
/// </summary>
public static class SiegeLoot
{
    /// <summary>One of <c>DropTableValidator.KnownSourceKinds</c>.</summary>
    public const string SourceKind = "siege-assault";

    public static string TableIdFor(string sectorTypeId) => $"drop.siege-assault.{sectorTypeId}";

    /// <summary>
    /// The loot source for a won district assault, or a rejection naming why there is none.
    /// <paramref name="turn"/> makes the source id — and therefore the correlation id — distinct per
    /// siege: a district retaken more than once (won, lost, retaken, won again) is a distinct loot
    /// event each time, never a replay of an earlier siege's manifest.
    /// </summary>
    public static AtomRejection TryResolve(
        string sectorId, int turn, int dangerBand, string sectorTypeId, PowerTuning tuning,
        out LootSourceRow? source)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        source = null;

        if (string.IsNullOrWhiteSpace(sectorId))
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                "a siege-assault loot source needs the district's own sector id");

        if (string.IsNullOrWhiteSpace(sectorTypeId))
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                "a siege-assault loot source needs the sector's own type id — one table per type, "
                + "matching sector-loot-wiring's identical split");

        var contentLevel = PowerIndexComposer.MapLevel(dangerBand, tuning);
        if (contentLevel < 1)
            return AtomRejection.ContentRule("drop.sector-band-safe",
                $"district '{sectorId}' is at danger band {dangerBand}; mapLevel({dangerBand}) = {contentLevel}, "
                + "and content starts at 1 — refused by name rather than floored to 1");

        source = new LootSourceRow(SourceKind, $"{sectorId}:{turn}", TableIdFor(sectorTypeId), contentLevel);
        return AtomRejection.Ok;
    }
}
