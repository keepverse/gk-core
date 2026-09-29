using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Actions;

/// <summary>
/// combat-ai `intent-router` (module 4, CAI1.11, spec-intent-router.md §2): the decorator that makes a
/// policy's ONE bound view trait-aware. `bloodthirsty`'s whole effect is a reordering of
/// <see cref="IBattleView.LiveActorKeys"/> (`BasicAttack.BloodthirstyView`, unchanged), and which
/// reordering is correct depends on which actor is asking — so the member that carries the decoration
/// is the viewer-relative <see cref="IBattleView.LiveActorKeysFor"/>, not the actor-less
/// <see cref="IBattleView.LiveActorKeys"/>.
///
/// <para><b>This is the seam the CAI1.11 ruling names, not a view swap.</b> A policy such as
/// <see cref="FusionRpg.Core.Battle.Siege.SiegeAiIntentSource"/> binds <see cref="IBattleView"/> once at
/// construction and keeps one view for its whole life (it owns a persistent `RetargetLedger` and
/// `BattleTrace`, so rebuilding it per decision is not an option). Handing it this wrapper means that
/// one view IS trait-aware: no `IIntentSource` signature changed, no policy field reassigned per call,
/// and the persistent ledgers untouched. This is the same decorating shape
/// <see cref="FoggedBattleView"/> already established for this interface — composing the two is
/// ordinary.</para>
///
/// <para><b>Only the viewer-relative read is decorated.</b> `bloodthirsty` changes nothing but the
/// live-actor ORDER, so every other member forwards to <paramref name="inner"/> untouched and costs
/// nothing. A future decorator that changes a per-actor fact read (<c>DerivedOf</c>, <c>FactsOf</c>, …)
/// must extend this class AND say how it resolves the deciding actor for that read — the trait
/// vocabulary stays with the decorator, here in the engine-adjacent seam, never in `Core/Actions`'
/// scoring code.</para>
/// </summary>
public sealed class TraitAwareBattleView : IBattleView
{
    readonly IBattleView _inner;
    readonly IReadOnlyList<ITraitDecorator> _decorators;

    public TraitAwareBattleView(IBattleView inner, IReadOnlyList<ITraitDecorator> decorators)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _decorators = decorators ?? throw new ArgumentNullException(nameof(decorators));
    }

    /// <summary>The actor-less read stays the raw, undecorated order — there is no viewer to decorate
    /// FOR. Every decision path uses <see cref="LiveActorKeysFor"/>; this member exists because the
    /// interface has it (board adapters iterate it outside any decision).</summary>
    public IReadOnlyList<string> LiveActorKeys => _inner.LiveActorKeys;

    /// <summary>The decorated order for THIS deciding actor: apply each applicable decorator in list
    /// order to the inner view, then read the reordered live set. Indexed `for`, never `foreach` — the
    /// boxed enumerator is the exact per-decision allocation this program forbids.</summary>
    public IReadOnlyList<string> LiveActorKeysFor(string actorKey)
    {
        var view = _inner;
        for (var i = 0; i < _decorators.Count; i++)
        {
            var decorator = _decorators[i];
            if (decorator.AppliesTo(actorKey))
                view = decorator.Decorate(actorKey, view);
        }

        return view.LiveActorKeys;
    }

    public int SideOf(string actorKey) => _inner.SideOf(actorKey);

    public GridPos? PositionOf(string actorKey) => _inner.PositionOf(actorKey);

    public EntityFacts FactsOf(string actorKey) => _inner.FactsOf(actorKey);

    public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey) => _inner.HeldActionsOf(actorKey);

    public FusionRpg.Core.Stats.Derived.ActorDerivedSnapshot? DerivedOf(string actorKey) => _inner.DerivedOf(actorKey);

    public string? GarrisonedStructureKeyOf(string actorKey) => _inner.GarrisonedStructureKeyOf(actorKey);

    public GridPos? ObjectivePositionOf(string actorKey) => _inner.ObjectivePositionOf(actorKey);

    public long? MaxHpOf(string actorKey) => _inner.MaxHpOf(actorKey);

    public int AggressionOf(string actorKey) => _inner.AggressionOf(actorKey);
    /// <summary>CAI3.4: forwarded — a trait decorator changes only the live ORDER, so it has nothing
    /// to add to the downed roster and must not hide it.</summary>
    public IReadOnlyList<string> DownedAllyKeysOf(string actorKey) => _inner.DownedAllyKeysOf(actorKey);
}
