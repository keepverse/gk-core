using FusionRpg.Core.Expeditions;
using FusionRpg.Core.Stats;

namespace FusionRpg.Core.Stats.Derived.Subsystems;

/// <summary>
/// channelmods-hub T2 — Hub contributor for <see cref="ExpeditionResolver.InjuryDerivedModifiers"/>:
/// the registered producer the sheet and (post-fuse) battle share, replacing the private
/// <c>BattleChannelMod</c> append in <c>ExpeditionResolver.ApplyInjuries</c>.
///
/// <para>Per-context delegates, matching <see cref="StarLoyaltySubsystem"/>: the module owns turning
/// an injury count into channel values, not where the counts are stored. The victim's Atk comes from
/// <see cref="StatContext.Baseline"/> — the same <c>s.Atk</c> <c>ApplyInjuries</c> reads from the
/// setup — keyed by <see cref="StatContext.EntityKey"/>, the same key the injuries map uses. Order
/// 100 — FlatSum is commutative, so it shares the tier with progression/aptitude/star-loyalty.
/// Contributes nothing for an uninjured actor, so bare resolves stay green.</para>
/// </summary>
public sealed class ExpeditionInjurySubsystem : IActorStatSubsystem
{
    readonly Func<StatContext, IReadOnlyDictionary<string, int>> _injuries;

    public ExpeditionInjurySubsystem(Func<StatContext, IReadOnlyDictionary<string, int>>? injuries = null) =>
        _injuries = injuries ?? (_ => new Dictionary<string, int>(StringComparer.Ordinal));

    public string SubsystemId => "rpg.expedition.injury";
    public int Order => 100;

    public void ContributeDerived(StatContext ctx, ICollection<DerivedModifier> mods)
    {
        var map = _injuries(ctx);
        if (map is null || !map.TryGetValue(ctx.EntityKey, out var count) || count <= 0) return;
        foreach (var m in ExpeditionResolver.InjuryDerivedModifiers(ctx.EntityKey, count, ctx.Baseline.Atk))
            mods.Add(m);
    }
}
