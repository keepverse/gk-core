using FusionRpg.Core.Battle;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Stats.Derived.Subsystems;

/// <summary>
/// Base <c>resource.max.*</c> / <c>resource.regen.*</c> for UniqueActor Hub compose — parity with
/// <see cref="Battle.BattleStatComposer"/>'s resource seed so cold <c>/sheet</c> Max comes from Hub
/// (ResourceChannelReader), not a ProjectSheet-only floor.
///
/// Opt-in via <see cref="ActorHub.CreateDefault"/> — bare CreateDefault callers stay unaffected.
/// </summary>
public sealed class ResourceBaselineSubsystem : IActorStatSubsystem
{
    readonly IPowerIndexProvider _powerIndex;
    readonly BattleResourceTuning? _tuning;

    /// <param name="tuning">Explicit battle-resources tuning; null reads the one
    /// <see cref="BattleRuleset.ConfigureResources"/> installed (every production caller).</param>
    public ResourceBaselineSubsystem(IPowerIndexProvider? powerIndex = null, BattleResourceTuning? tuning = null)
    {
        _powerIndex = powerIndex ?? new StubPowerIndexProvider();
        _tuning = tuning;
    }

    public string SubsystemId => ContributionSourceIds.ResourceBaseline;
    public int Order => 90; // before progression/aptitude flats that stack on the same FlatSum channels

    public void ContributeDerived(StatContext ctx, ICollection<DerivedModifier> mods)
    {
        var theta = Math.Max(1, _powerIndex.ActorIndex(ctx));
        // UniqueActor compose seeds Baseline.MaxHp = BattleRuleset.BaseHp(level); use that for hp pool.
        var baseHp = Math.Max(1L, ctx.Baseline.MaxHp);

        foreach (var id in DerivedStatChannels.ResourceIds)
        {
            mods.Add(new DerivedModifier(
                DerivedStatChannels.ResourceMax(id),
                DerivedModifierOp.Flat,
                _tuning is null ? BattleRuleset.BaseResourceMax(theta, id, baseHp) : BattleRuleset.BaseResourceMax(theta, id, baseHp, _tuning),
                SourceId: ContributionSourceIds.ResourceBaseline));
            mods.Add(new DerivedModifier(
                DerivedStatChannels.ResourceRegen(id),
                DerivedModifierOp.Flat,
                _tuning is null ? BattleRuleset.BaseResourceRegen(theta, id) : BattleRuleset.BaseResourceRegen(theta, id, _tuning),
                SourceId: ContributionSourceIds.ResourceBaseline));
        }
    }
}
