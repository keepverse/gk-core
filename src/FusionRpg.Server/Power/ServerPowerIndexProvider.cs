using FusionRpg.Core.Commanders;
using FusionRpg.Core.Power;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats;
using FusionRpg.Data;

namespace FusionRpg.Server.Power;

/// <summary>
/// Server-side Θ index — hydrates <see cref="ActorIndex"/> from <c>rpg_actor_progression</c> via
/// <see cref="RpgStore"/> (T1.4; tunables-ssot.md §7.2 boundary — SQL stays inside FusionRpg.Data,
/// this class only calls an existing store method, so it never trips <c>guard-dal.ps1</c>).
///
/// <para><b>Partial hydration, documented rather than hidden:</b> only <c>daveLevel</c> has a
/// persistent column today. <c>realmsAdvanced</c> and <c>pvzRuns</c> have no column anywhere in the
/// schema — <c>empire-economy-ssot.md §4</c>, which ssot-power-scale.md §5 cites as realmsAdvanced's
/// source, does not currently define one either (searched; zero matches for "realm" in that doc).
/// World retirement/prestige is an unbuilt feature, not a wiring gap this task can close. Both
/// therefore read as 0 via <see cref="PowerIndexComposer"/>'s existing "absence, not corruption"
/// clamp — the same contract an un-hydrated actor already gets, so this is not a special case.</para>
///
/// <para><see cref="ContentIndex"/> needs no store access at all: every content-side input
/// (dangerBand/worldTier/zombossLevel/realmsAdvanced) arrives already resolved on
/// <see cref="ContentContext"/> — the caller's job (a later phase's), not this provider's.</para>
/// </summary>
public sealed class ServerPowerIndexProvider : IPowerIndexProvider
{
    readonly RpgStore _store;
    readonly PowerTuning _tuning;

    public ServerPowerIndexProvider(RpgStore store, PowerTuning tuning)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
    }

    public int ActorIndex(StatContext ctx) => PowerIndexComposer.ActorExplain(_tuning, ReadSnapshot(ctx)).Total;

    /// <summary>
    /// `ai-empire-species` EP4.16 (R23) - the empire-keyed twin of <see cref="ActorIndex"/>: Theta for any
    /// empire of a save, composed by the SAME <see cref="PowerIndexComposer.ActorExplain"/> over the SAME
    /// ladder, from that empire's OWN commander level (`RpgStore.CommanderLevelOf`,
    /// `zomboss-commander-clock` SP7.3's one seam). Declared on <see cref="IPowerIndexProvider"/> so a
    /// caller holding only the interface (the aptitudes endpoint's per-empire pool map, EP4.18) can ask
    /// it; this is the only implementation that composes rather than repeating a constant.
    ///
    /// <para><b>A wiring gap closed, never a second curve.</b> Before this, the provider read only
    /// `ctx.PlayerId`, so Zomboss's commander level had no way into the composer and his point budget had
    /// no Theta. No new `f(level)` is introduced: the level is an INPUT to the one ladder, exactly as
    /// `ReadSnapshot`'s `daveLevel` is. `realmsAdvanced`/`pvzRuns` stay 0 for the same reason they are 0
    /// there (no column exists; see this class's own doc).</para>
    ///
    /// <para><b>Magnitudes on the lawn are unchanged.</b> This sets only the size of that empire's
    /// commander point budget (R23); nothing reads it as a combat magnitude.</para>
    /// </summary>
    public int ActorIndexFor(SaveId save, EmpireId empire) =>
        PowerIndexComposer.ActorExplain(
            _tuning,
            new ActorLadderSnapshot(
                checked((int)_store.CommanderLevelOf(save, empire)), RealmsAdvanced: 0, PvzRuns: 0)).Total;

    public int ContentIndex(ContentContext ctx) => PowerIndexComposer.ContentExplain(_tuning, ctx).Total;

    public PowerAxisReport Explain(StatContext ctx) => PowerIndexComposer.ActorExplain(_tuning, ReadSnapshot(ctx));

    ActorLadderSnapshot ReadSnapshot(StatContext ctx)
    {
        if (ctx.PlayerId is not { } playerId) return ActorLadderSnapshot.Empty;

        var summary = _store.GetRpgProgressionSummary(playerId);
        if (summary?.Player is not { } player) return ActorLadderSnapshot.Empty;

        int daveLevel = checked((int)player.Level);
        return new ActorLadderSnapshot(daveLevel, RealmsAdvanced: 0, PvzRuns: 0);
    }
}
