using FusionRpg.Core.Power;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Stats.Derived.Subsystems;

/// <summary>
/// Progression tier power — P1/P2 ADR, amended by power-plan.md T3.2.
///
/// <para><c>progression.power</c> reads Θ from <see cref="IPowerIndexProvider.ActorIndex"/> — the
/// POC curve (<c>2^min(level,12)</c>) is retired (deleted, <c>ProgressionPowerCurve.cs</c> is gone).
/// Defaults to <see cref="StubPowerIndexProvider"/> (Θ=0), matching every other un-hydrated
/// power-index consumer's "no data yet" contract — a real behaviour change from the old stub's 1.0,
/// which was itself only ever an artifact of the retired curve's own <c>level&lt;=0 → 1.0</c> branch.
/// </para>
///
/// <para><c>progression.realm</c> stays the stub constant permanently (SSOT: "realm advancement is
/// additive in Θ, never a contest multiplier" — not this subsystem's concern to change).</para>
///
/// <para>class-system-todo.md P3.3 (2026-08-27) retired the level-gated
/// <c>progression.bonus.{maxHp,atk,defense}</c> flat curve this subsystem used to own (was already
/// latent — no host ever passed the delegate that drove it) — those five bridge channels
/// (<c>ProgressionBonus{MaxHp,Atk,Defense,Arm1,Arm2}</c>) are allocation-sourced now, fed by
/// <see cref="Subsystems"/>' sibling <see cref="AptitudeSubsystem"/> through the same
/// <c>ActorHub.Register</c> seam, for whichever aptitudes have an edge into them (`Vigor`→maxHp/arm2,
/// `Might`/`Ferocity`→atk, `Fortitude`/`Bulwark`→defense/arm1). Retired in ssot-power-scale.md §10.1's
/// same change (the inventory row this stub earned there is cleared, not left stale).</para>
/// </summary>
public sealed class RpgProgressionSubsystem : IActorStatSubsystem
{
    readonly IPowerIndexProvider _powerIndex;

    public RpgProgressionSubsystem(IPowerIndexProvider? powerIndex = null)
    {
        _powerIndex = powerIndex ?? new StubPowerIndexProvider();
    }

    public string SubsystemId => "rpg.progression";
    public int Order => 100;

    public void ContributeDerived(StatContext ctx, ICollection<DerivedModifier> mods)
    {
        mods.Add(new DerivedModifier(
            DerivedStatChannels.ProgressionPower,
            DerivedModifierOp.Replace,
            _powerIndex.ActorIndex(ctx),
            SourceId: ContributionSourceIds.Progression));
        mods.Add(new DerivedModifier(
            DerivedStatChannels.ProgressionRealm,
            DerivedModifierOp.Replace,
            StatusPolicy.ProgressionPowerStubDefault,
            SourceId: ContributionSourceIds.Progression));
    }
}
