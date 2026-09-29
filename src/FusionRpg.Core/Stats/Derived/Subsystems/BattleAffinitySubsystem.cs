using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats;

namespace FusionRpg.Core.Stats.Derived.Subsystems;

/// <summary>
/// battle-hub-fuse T5 — Hub twin of the deleted composer's <c>AddAffinity</c>: the actor's own
/// element channels carry Atk/Defense shares by the same divisors. Re-home only, including the
/// integer division (a <c>long / int</c> computed as <c>long</c> first, exactly like the composer did).
///
/// <para>battle-hub-fuse T6 — reads <see cref="BattleTuningHub.Tuning"/> directly, the composer's own
/// former home for these divisors. Order 100 — FlatSum-commutative.</para>
/// </summary>
public sealed class BattleAffinitySubsystem : IActorStatSubsystem
{
    readonly Func<StatContext, (long Atk, long Defense, ElementTypeId? Primary, ElementTypeId? Secondary)> _affinity;

    public BattleAffinitySubsystem(
        Func<StatContext, (long Atk, long Defense, ElementTypeId? Primary, ElementTypeId? Secondary)>? affinity = null) =>
        _affinity = affinity ?? (_ => (0, 0, null, null));

    public string SubsystemId => "rpg.battle.affinity";
    public int Order => 100;

    public void ContributeDerived(StatContext ctx, ICollection<DerivedModifier> mods)
    {
        var (atk, defense, primary, secondary) = _affinity(ctx);
        const string source = ContributionSourceIds.BattleBaseline;
        if (primary is { } p)
        {
            mods.Add(new DerivedModifier($"combat.power.{p.ToElementId()}", DerivedModifierOp.Flat, atk / BattleTuningHub.Tuning.PrimaryAffinityDivisor, SourceId: source));
            mods.Add(new DerivedModifier($"combat.defense.{p.ToElementId()}", DerivedModifierOp.Flat, defense / BattleTuningHub.Tuning.PrimaryAffinityDivisor, SourceId: source));
        }
        if (secondary is { } s)
        {
            mods.Add(new DerivedModifier($"combat.power.{s.ToElementId()}", DerivedModifierOp.Flat, atk / BattleTuningHub.Tuning.SecondaryAffinityDivisor, SourceId: source));
            mods.Add(new DerivedModifier($"combat.defense.{s.ToElementId()}", DerivedModifierOp.Flat, defense / BattleTuningHub.Tuning.SecondaryAffinityDivisor, SourceId: source));
        }
    }
}
