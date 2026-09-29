using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats;

namespace FusionRpg.Core.Stats.Derived.Subsystems;

/// <summary>
/// battle-hub-fuse T5 — Hub twin of the composer's trait fold
/// (<c>BattleStatComposer.Compose</c>'s per-distinct-trait <c>TraitAtomSource.ModsFor</c> loop):
/// the same mods as attributed <c>Flat</c> contributions, source <c>trait:{traitId}</c> (passed
/// through raw by fiction labels — a dedicated family is a tracked follow-up, not invented here).
/// Migrated-atom traits and catalog-fallback traits flow through the identical mapping, exactly
/// like <c>ModsFor</c> itself serves both. Dedupe matches the composer (once per distinct trait).
/// Order 100 — FlatSum-commutative.
/// </summary>
public sealed class BattleTraitSubsystem : IActorStatSubsystem
{
    readonly TraitAtomSource _source;
    readonly Func<StatContext, IReadOnlyList<string>> _traitIds;

    public BattleTraitSubsystem(
        TraitAtomSource? source = null,
        Func<StatContext, IReadOnlyList<string>>? traitIds = null)
    {
        _source = source ?? TraitAtomSource.Shipped();
        _traitIds = traitIds ?? (_ => Array.Empty<string>());
    }

    public string SubsystemId => "rpg.battle.traits";
    public int Order => 100;

    public void ContributeDerived(StatContext ctx, ICollection<DerivedModifier> mods)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var traitId in _traitIds(ctx))
        {
            if (!seen.Add(traitId)) continue;
            foreach (var mod in _source.ModsFor(traitId))
                mods.Add(new DerivedModifier(mod.ChannelId, DerivedModifierOp.Flat, mod.Amount, SourceId: $"trait:{traitId}"));
        }
    }
}
