using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Stats;

namespace FusionRpg.Core.Stats.Derived.Subsystems;

/// <summary>
/// battle-hub-fuse T5 — Hub twin of the deleted composer's <c>turn.speed</c> seed
/// (<c>SpeciesTempoProjection.SpeedFor</c> over the setup's own attack interval). Re-home only:
/// the same <see cref="BattleTuningHub"/> reference interval the composer used to read.
///
/// <para>Minus the channel's registry <c>DefaultValue</c>: the old composer seeds this channel
/// absolutely (<c>FromValues</c>), while Hub compose adds the default underneath every
/// contribution — emitting the raw seed would double-count the base on every battle. Reading the
/// default from the same registry instance that composes keeps this self-tracking when tuning
/// moves it. Order 100.</para>
/// </summary>
public sealed class BattleTempoSubsystem : IActorStatSubsystem
{
    readonly Func<StatContext, long> _attackIntervalMs;
    readonly DerivedStatRegistry _registry;

    public BattleTempoSubsystem(
        Func<StatContext, long>? attackIntervalMs = null,
        DerivedStatRegistry? registry = null)
    {
        _attackIntervalMs = attackIntervalMs ?? (_ => 0);
        _registry = registry ?? DerivedStatRegistry.CreateDefault();
    }

    public string SubsystemId => "rpg.battle.tempo";
    public int Order => 100;

    public void ContributeDerived(StatContext ctx, ICollection<DerivedModifier> mods)
    {
        _registry.TryResolveChannel(DerivedTurnChannels.Speed, out var def);
        var baseline = def?.DefaultValue ?? 0;
        mods.Add(new DerivedModifier(DerivedTurnChannels.Speed, DerivedModifierOp.Flat,
            SpeciesTempoProjection.SpeedFor(
                _attackIntervalMs(ctx),
                BattleTuningHub.Tuning.SpeciesTempoReferenceIntervalMs,
                DerivedStatPolicy.TurnDefaultSpeed) - baseline,
            SourceId: ContributionSourceIds.BattleBaseline));
    }
}
