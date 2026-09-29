namespace FusionRpg.Core.Expeditions;

/// <summary>
/// One expedition tier (spec-expeditions.md §Timeline): a fixed tick timeline with evenly
/// spaced battles; the 20h tier ends on a boss wave. Slots gate squad size at dispatch.
///
/// <para><see cref="DangerBand"/> is the tier's content danger, loaded from the expeditions tuning
/// beside its other numbers (spec-host-content-theta.md §4) and read by
/// <see cref="Narrative.Hosts.HostContentTheta.ForExpedition"/> — this catalog passes it through and
/// never derives one. The resolver does not read it, so tier hashes stay byte-identical across a
/// band change.</para>
/// </summary>
public sealed record ExpeditionTierDef(
    string TierId, string Name, int DurationMinutes, int TickCount, int BattleCount, int SquadSlots,
    int DangerBand, bool HasBossWave)
{
    public int TickMinutes => DurationMinutes / TickCount;
}

public static class ExpeditionTierCatalog
{
    /// <summary>Ids/names/hasBossWave stay here (schema); the numeric fields are loaded
    /// (tunables-ssot.md T1) — see <see cref="ExpeditionTuningHub"/>.</summary>
    public static IReadOnlyList<ExpeditionTierDef> All
    {
        get
        {
            var t = ExpeditionTuningHub.Tuning.Tiers;
            ExpeditionTierDef Row(string id, string name, bool hasBossWave)
            {
                var n = t[id];
                return new ExpeditionTierDef(id, name, n.DurationMinutes, n.TickCount, n.BattleCount, n.SquadSlots, n.DangerBand, hasBossWave);
            }
            return new[]
            {
                Row("scout-30m", "Scouting Sortie", hasBossWave: false),
                Row("forage-4h", "Soul Foraging", hasBossWave: false),
                Row("hunt-8h", "Rift Hunt", hasBossWave: false),
                Row("warpath-20h", "Warpath", hasBossWave: true)
            };
        }
    }

    // Not a static-readonly field: it would evaluate All (and so Tuning) at type-load, which can
    // run before a host's Configure(...) call. Lazy like the other migrated catalogs' _all/_byId.
    static Dictionary<string, ExpeditionTierDef>? _byId;
    static Dictionary<string, ExpeditionTierDef> ById =>
        _byId ??= All.ToDictionary(t => t.TierId, StringComparer.Ordinal);

    public static bool IsKnown(string? tierId) => tierId != null && ById.ContainsKey(tierId);

    public static ExpeditionTierDef Get(string tierId) =>
        ById.TryGetValue(tierId, out var def)
            ? def
            : throw new ArgumentException($"Unknown expedition tier id '{tierId}'.");
}
