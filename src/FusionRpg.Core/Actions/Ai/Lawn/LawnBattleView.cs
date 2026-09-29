using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Match.Ai;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Actions.Ai.Lawn;

/// <summary>
/// combat-ai `lawn-actor-view` (CAI4.1, spec-lawn-actor-view.md "Answering all ten members, including
/// the ones the lawn does not have"): an <see cref="IBattleView"/> over the frozen lawn census. Every
/// member has a STATED answer, because guessing on the ones a lawn does not have is how a view silently
/// lies to a scorer.
///
/// <para><b>Perspective-scoped, because a relation is.</b> <see cref="IBattleView.SideOf"/> is absolute
/// (its own doc defines enemy as <c>SideOf(other) != SideOf(self)</c>) while
/// <see cref="IOwnSideOracle.RelationOf"/> is relative — both shipped oracles are constructed with a
/// perspective. This view reconciles them by being built FOR one perspective, and it reads side ONLY
/// through the oracle: a unit whose raw board side says `zombie` but whose oracle answers `Ally` is an
/// ally here, and a ptr no oracle knows falls to the other side via
/// <c>LawnUnitViewFactory</c>'s own resolution. <b>No member reads <see cref="BoardEntitySnap.Side"/></b>
/// — that field is why hypnosis would otherwise read backwards.</para>
///
/// <para><b>No fog, and that is a stated decision rather than an omission.</b> The PvZ lawn has no
/// fog-of-war, so this view composes nothing over itself: <see cref="DerivedOf"/> returns a real
/// snapshot for every live actor (null on this seam means HIDDEN, and nothing on a lawn is hidden) and
/// <see cref="AggressionOf"/> is not fog-gated, unlike battle and siege where
/// <c>FoggedBattleView</c> gates it because a target's aggression is board information.</para>
///
/// <para><b>Lazy at the decision edge.</b> The census delegate is invoked only when a decision actually
/// asks for something; a frame with no decision edge never builds the snapshot and allocates nothing.
/// <see cref="BeginFrame"/> drops both the census materialisation and the derived memo.</para>
/// </summary>
public sealed class LawnBattleView : IBattleView
{
    /// <summary>Structural on a CLOSED set: a lawn has exactly two deciding perspectives (there are two
    /// commanders on it), so the host caches at most one view per perspective per frame. Not a
    /// progression cap and not a work bound.</summary>
    public const int PerspectiveCount = 2;

    /// <summary>The two side codes <see cref="IBattleView.SideOf"/> already documents: 0 == "my side"
    /// (Self/Ally), 1 == "the other side" (Enemy). Never <c>EntityFacts.Side</c> read off the raw board.</summary>
    public const int MySideCode = 0;
    public const int OtherSideCode = 1;

    readonly IOwnSideOracle _relation;
    readonly Func<BoardSnapshot> _censusOf;
    readonly Func<string, ILawnUnitView?> _unitOf;
    readonly Func<string, IReadOnlyList<CompiledAction>> _heldActionsOf;
    readonly Func<string, int> _elementOf;
    readonly Func<string, ulong> _statusMaskOf;
    readonly LawnDerivedCache _derived;

    // Per-frame materialisation: built on the first read of the frame, never in the constructor.
    BoardSnapshot? _census;
    readonly List<string> _keys = new();

    /// <param name="relation">The composed oracle (production: <see cref="LawnRelationChain"/>), already
    /// scoped to this view's perspective by its own construction.</param>
    /// <param name="censusOf">The frozen census. Invoked lazily, once per frame, and never for a frame
    /// with no decision edge.</param>
    /// <param name="unitOf">The per-ptr visible-unit half (<c>ILawnUnitView</c>) that carries the HP pair
    /// the census does not.</param>
    /// <param name="heldActionsOf">Module 16's registry read. Until it exists the delegate returns an
    /// empty list, which <c>StubIntentSource</c> already handles as "cannot act at all".</param>
    /// <param name="elementOf">The already-per-ptr-cached lawn element resolve — the same one the grant
    /// binder uses, so the view and the hit path agree on an actor's element.</param>
    /// <param name="statusMaskOf">The effect runtime's status mask for this ptr.</param>
    /// <param name="derived">The per-frame, revision-keyed Hub memo.</param>
    public LawnBattleView(
        IOwnSideOracle relation,
        Func<BoardSnapshot> censusOf,
        Func<string, ILawnUnitView?> unitOf,
        Func<string, IReadOnlyList<CompiledAction>> heldActionsOf,
        Func<string, int> elementOf,
        Func<string, ulong> statusMaskOf,
        LawnDerivedCache derived)
    {
        _relation = relation ?? throw new ArgumentNullException(nameof(relation));
        _censusOf = censusOf ?? throw new ArgumentNullException(nameof(censusOf));
        _unitOf = unitOf ?? throw new ArgumentNullException(nameof(unitOf));
        _heldActionsOf = heldActionsOf ?? throw new ArgumentNullException(nameof(heldActionsOf));
        _elementOf = elementOf ?? throw new ArgumentNullException(nameof(elementOf));
        _statusMaskOf = statusMaskOf ?? throw new ArgumentNullException(nameof(statusMaskOf));
        _derived = derived ?? throw new ArgumentNullException(nameof(derived));
    }

    BoardSnapshot Census => _census ??= _censusOf();

    /// <summary>Drops the frame-scoped materialisation: the census list and the derived memo. One call
    /// per frame, at the decision edge.</summary>
    public void BeginFrame()
    {
        _census = null;
        _keys.Clear();
        _derived.BeginFrame();
    }

    /// <summary>The perspective's ptr list, in census order (the census is already filtered to living
    /// units). Reuses one list per frame, so repeated reads in a frame allocate nothing.</summary>
    public IReadOnlyList<string> LiveActorKeys
    {
        get
        {
            var entities = Census.Entities;
            _keys.Clear();
            for (var i = 0; i < entities.Count; i++) _keys.Add(entities[i].Ptr);
            return _keys;
        }
    }

    /// <summary>The same list: a lawn has no view-order decorators (there is no fog, and the
    /// bloodthirsty reorder is a battle/siege trait), so the viewer-relative read IS the absolute one.</summary>
    public IReadOnlyList<string> LiveActorKeysFor(string actorKey) => LiveActorKeys;

    /// <summary>Side ONLY through the oracle. <c>Self</c>/<c>Ally</c> are my side; <c>Enemy</c>,
    /// <c>Any</c> and an unknown ptr are the other side — the last of those because
    /// <c>LawnUnitViewFactory</c> has already resolved an un-owned vanilla unit to those.</summary>
    public int SideOf(string actorKey) =>
        _relation.RelationOf(actorKey) switch
        {
            RelationKind.Self or RelationKind.Ally => MySideCode,
            _ => OtherSideCode,
        };

    /// <summary>Never null for a live actor: the lawn always has a board, so `StubIntentSource`'s
    /// no-board `SourceOrder` fallback is unreachable here.</summary>
    public GridPos? PositionOf(string actorKey)
    {
        var snap = Census.FindPtr(actorKey);
        return snap is null ? null : new GridPos(snap.Row, snap.Col);
    }

    public EntityFacts FactsOf(string actorKey)
    {
        var snap = Census.FindPtr(actorKey);
        var unit = _unitOf(actorKey);

        return new EntityFacts(
            Side: SideOf(actorKey),
            TypeId: snap?.TypeId ?? -1,
            HpMilli: HpMilliOf(unit),
            ElementId: _elementOf(actorKey),
            Row: snap?.Row ?? -1,
            Col: snap?.Col ?? -1,
            IsMindControlled: snap?.MindControlled ?? false,
            // IsKiller is a battle-TURN concept with no lawn producer; false is the neutral value every
            // consumer already treats as "no".
            IsKiller: false,
            StatusMask: _statusMaskOf(actorKey));
    }

    /// <summary>A bounded ratio 0..1000 of a live magnitude, not a progression cap (AGENTS.md "Caps"):
    /// 0 at <c>hpMax == 0</c> (no ratio is defined), 1000 at or above max, and rounded down between.
    /// The result is a per-mille RATIO, so <c>int</c> is its correct type — the same shape
    /// <c>EntityFacts.HpMilli</c> already commits to. The multiply runs in <c>long</c> because both
    /// <see cref="ILawnUnitView"/> HP members are <c>long</c>, and the narrowing is <c>checked</c> so a
    /// future miswiring throws rather than silently wrapping.</summary>
    static int HpMilliOf(ILawnUnitView? unit)  // overflow-bounded: [0, 1000] per-mille ratio, see the comment above
    {
        if (unit is null || unit.HpMax <= 0) return 0;
        if (unit.HpCurrent >= unit.HpMax) return 1000;
        if (unit.HpCurrent <= 0) return 0;
        return checked((int)(unit.HpCurrent * 1000 / unit.HpMax));
    }

    /// <summary>The memo'd Hub snapshot. Never null for a live actor — see "no fog" above.</summary>
    public ActorDerivedSnapshot? DerivedOf(string actorKey) => _derived.Get(actorKey);

    /// <summary>Delegated, so this view lands and is testable before module 16's registry exists.</summary>
    public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey) => _heldActionsOf(actorKey);

    /// <summary>Always null: there are no siege emplacements on a lawn, and null is that member's own
    /// documented absence convention.</summary>
    public string? GarrisonedStructureKeyOf(string actorKey) => null;

    /// <summary>Always null: its own doc already says null for "every non-siege battle", so the core's
    /// objective weight contributes nothing on the lawn. Correct, not a gap.</summary>
    public GridPos? ObjectivePositionOf(string actorKey) => null;

    public long? MaxHpOf(string actorKey) => _unitOf(actorKey)?.HpMax;

    /// <summary>The Hub-composed <c>ai.aggression</c> out of the memo'd snapshot, never a private fold
    /// and never a second read path. Saturation onto the closed tier range is
    /// <c>CandidateScorer.EffectiveTier</c>'s job at the scoring site, because <c>aggressionRange</c> is
    /// a PROFILE value this view is not given.</summary>
    public int AggressionOf(string actorKey) =>
        (int)Math.Round(_derived.Get(actorKey).Get(DerivedStatChannels.AiAggression));

    /// <summary>Empty, and answered EXPLICITLY rather than inherited from the interface's default: a
    /// lawn has no downed-party state (that is the delve's `TurnState.Downed`, gated on
    /// `DownedOnDeplete`, which no lawn profile sets). Stating an absence is this view's whole contract.</summary>
    public IReadOnlyList<string> DownedAllyKeysOf(string actorKey) => Array.Empty<string>();
}
