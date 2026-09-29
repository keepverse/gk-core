using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats;

namespace FusionRpg.Core.Stats.Derived.Subsystems;

/// <summary>
/// battle-hub-fuse T5 — Hub twin of <see cref="Battle.BattleStatComposer"/>'s seed block: defense
/// plus the BaseAccuracy/BaseDodge/BaseCritRate/BaseCritResist Θ seeds. Re-home only: the same
/// <c>BattleRuleset.BaseX(theta)</c> calls with the same theta the composer reads
/// (<c>setup.ThetaActor ?? setup.Level</c>), sourced <c>rpg.battle.base</c>.
///
/// <para>Per-context delegate, matching <see cref="StarLoyaltySubsystem"/>: the module owns turning
/// level+defense into channel values, not where those durable values are stored. Each seed is
/// emitted minus its channel's registry <c>DefaultValue</c> — the old composer seeds absolutely
/// (<c>FromValues</c>) while Hub compose adds the default underneath, so the raw seed would
/// double-count any nonzero base (the turn.speed twin proved it). Reading the default from the
/// same registry instance that composes keeps this self-tracking. Order 90 — a seed like
/// <see cref="ResourceBaselineSubsystem"/>, before the Order-100 flats that stack on top.</para>
/// </summary>
public sealed class BattleBaselineSubsystem : IActorStatSubsystem
{
    readonly Func<StatContext, (int Theta, long Defense)> _seeds;
    readonly DerivedStatRegistry _registry;

    public BattleBaselineSubsystem(
        Func<StatContext, (int Theta, long Defense)>? seeds = null,
        DerivedStatRegistry? registry = null)
    {
        _seeds = seeds ?? (_ => (1, 0));
        _registry = registry ?? DerivedStatRegistry.CreateDefault();
    }

    public string SubsystemId => "rpg.battle.baseline";
    public int Order => 90;

    public void ContributeDerived(StatContext ctx, ICollection<DerivedModifier> mods)
    {
        var (theta, defense) = _seeds(ctx);
        const string source = ContributionSourceIds.BattleBaseline;
        mods.Add(new DerivedModifier(DerivedStatChannels.CombatDefenseOmni, DerivedModifierOp.Flat, defense - DefaultOf(DerivedStatChannels.CombatDefenseOmni), SourceId: source));
        mods.Add(new DerivedModifier(DerivedStatChannels.CombatAccuracyOmni, DerivedModifierOp.Flat, BattleRuleset.BaseAccuracy(theta) - DefaultOf(DerivedStatChannels.CombatAccuracyOmni), SourceId: source));
        mods.Add(new DerivedModifier(DerivedStatChannels.CombatDodgeOmni, DerivedModifierOp.Flat, BattleRuleset.BaseDodge(theta) - DefaultOf(DerivedStatChannels.CombatDodgeOmni), SourceId: source));
        mods.Add(new DerivedModifier(DerivedStatChannels.CombatCritRateOmni, DerivedModifierOp.Flat, BattleRuleset.BaseCritRate(theta) - DefaultOf(DerivedStatChannels.CombatCritRateOmni), SourceId: source));
        mods.Add(new DerivedModifier(DerivedStatChannels.CombatCritResistOmni, DerivedModifierOp.Flat, BattleRuleset.BaseCritResist(theta) - DefaultOf(DerivedStatChannels.CombatCritResistOmni), SourceId: source));
    }

    double DefaultOf(string channel) =>
        _registry.TryResolveChannel(channel, out var def) ? def.DefaultValue : 0;
}
