using FusionRpg.Core.Items.Consumables;
using FusionRpg.Core.Stats;

namespace FusionRpg.Core.Stats.Derived.Subsystems;

/// <summary>
/// channelmods-hub T2 — Hub contributor for <see cref="DraughtProjection.ToDerivedModifiers"/>: the
/// registered producer the sheet and (post-fuse) battle share, replacing the private
/// <c>BattleChannelMod</c> append in <c>DraughtProjection.Apply</c>.
///
/// <para>Per-context delegate, matching <see cref="StarLoyaltySubsystem"/>: the module owns turning a
/// manifest into channel values, not where the manifest is stored. Draughts are per-squad (every
/// member receives every mod), so the delegate returns the whole manifest regardless of context.
/// Order 100 — FlatSum is commutative, so it shares the tier with progression/aptitude/star-loyalty.
/// Contributes nothing on an empty manifest, so bare resolves stay green.</para>
/// </summary>
public sealed class DraughtSubsystem : IActorStatSubsystem
{
    readonly Func<StatContext, IReadOnlyList<DraughtMod>> _manifest;

    public DraughtSubsystem(Func<StatContext, IReadOnlyList<DraughtMod>>? manifest = null) =>
        _manifest = manifest ?? (_ => Array.Empty<DraughtMod>());

    public string SubsystemId => "rpg.draught";
    public int Order => 100;

    public void ContributeDerived(StatContext ctx, ICollection<DerivedModifier> mods)
    {
        foreach (var m in DraughtProjection.ToDerivedModifiers(_manifest(ctx)))
            mods.Add(m);
    }
}
