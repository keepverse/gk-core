using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Actions;

/// <summary>
/// base-defense `siege-fog` (spec-siege-fog.md §3, module 30, 2026-09-06). Wraps a real
/// <see cref="IBattleView"/> and restricts it to what <paramref name="viewerSide"/> can currently see —
/// the SAME decorator shape <see cref="BasicAttack.BloodthirstyView"/> already established for this
/// interface. This is the swap `IBattleView`'s own doc comment named fog of war as the reason to
/// exist for, from day one: nothing that reads through it (`StubIntentSource`, and any future
/// `SiegeAiIntentSource`) needs to change at all.
///
/// <para><b>Own side is always visible</b> — fog never hides your own units; only the opposing side's
/// visibility is gated by <see cref="SiegeVisibility.IsVisible"/>. Symmetric by construction (decision
/// 1): this class carries no `PlayerSideId`-shaped special case — it applies the identical rule
/// regardless of which side `viewerSide` names, the same discipline `SiegeIntentSource`'s own fix
/// (17.1) already established for a different class.</para>
///
/// <para><b>Vision range per actor is caller-supplied</b> (<paramref name="visionRangeOf"/>), not
/// derived here. Confirmed by reading <see cref="EntityFacts"/> directly: it carries no
/// `CombatantKind`/structure-id field, so this view cannot itself tell a structure from an animate
/// combatant to look up a per-kind range (spec §2's own v1 scope). The caller — whoever assembles the
/// battle and already knows each actor's `StructureId`/`CombatantKind` — resolves the real per-kind
/// value (a `See`-role structure's own `StructureDef.VisionRangeTiles`, or the flat
/// `fog.defaultVisionRangeTiles` default for everyone else) once, the same "caller supplies domain
/// knowledge this compiler-shaped class does not need to know" precedent `AtomCompiler`'s own
/// `grantOwnerKeys`/`externalRefs` callbacks already use.</para>
/// </summary>
public sealed class FoggedBattleView : IBattleView
{
    readonly IBattleView _inner;
    readonly int _viewerSide;
    readonly Func<string, int> _visionRangeOf;
    readonly Func<GridPos, bool> _blocksVision;

    public FoggedBattleView(IBattleView inner, int viewerSide, Func<string, int> visionRangeOf, Func<GridPos, bool> blocksVision)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _visionRangeOf = visionRangeOf ?? throw new ArgumentNullException(nameof(visionRangeOf));
        _blocksVision = blocksVision ?? throw new ArgumentNullException(nameof(blocksVision));
        _viewerSide = viewerSide;
    }

    public IReadOnlyList<string> LiveActorKeys => _inner.LiveActorKeys.Where(IsKnownToViewer).ToList();

    public int SideOf(string actorKey) => _inner.SideOf(actorKey);

    public GridPos? PositionOf(string actorKey) => IsKnownToViewer(actorKey) ? _inner.PositionOf(actorKey) : null;

    public EntityFacts FactsOf(string actorKey) =>
        IsKnownToViewer(actorKey) ? _inner.FactsOf(actorKey) : UnknownFacts;

    public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey) =>
        IsKnownToViewer(actorKey) ? _inner.HeldActionsOf(actorKey) : Array.Empty<CompiledAction>();

    /// <summary>base-defense `siege-ai` (module 17, spec-siege-ai.md): same visibility gate as every
    /// other per-actor read here — a live scoring AI reading through fog gets no derived-stat estimate
    /// for a hidden actor, matching <see cref="FactsOf"/>'s own contract.</summary>
    public FusionRpg.Core.Stats.Derived.ActorDerivedSnapshot? DerivedOf(string actorKey) =>
        IsKnownToViewer(actorKey) ? _inner.DerivedOf(actorKey) : null;

    /// <summary>Ungated, like <see cref="SideOf"/> — self-knowledge ("am I garrisoning something"),
    /// never board visibility, so fog never applies here.</summary>
    public string? GarrisonedStructureKeyOf(string actorKey) => _inner.GarrisonedStructureKeyOf(actorKey);

    /// <summary>Ungated, like <see cref="GarrisonedStructureKeyOf"/> — which cell you advance toward is
    /// a fact about your OWN side, never board visibility.</summary>
    public GridPos? ObjectivePositionOf(string actorKey) => _inner.ObjectivePositionOf(actorKey);

    /// <summary>Gated like <see cref="DerivedOf"/> — an enemy's own MaxHp is board information, not
    /// self-knowledge, so fog hides it exactly like every other per-actor combat fact.</summary>
    public long? MaxHpOf(string actorKey) => IsKnownToViewer(actorKey) ? _inner.MaxHpOf(actorKey) : null;

    /// <summary>Gated like <see cref="MaxHpOf"/> — whether a target carries taunt/stealth is board
    /// information about THAT actor, not self-knowledge. 0 (neutral) for a hidden actor, matching
    /// <see cref="IBattleView.AggressionOf"/>'s own non-nullable default, never a fog-specific sentinel.</summary>
    public int AggressionOf(string actorKey) => IsKnownToViewer(actorKey) ? _inner.AggressionOf(actorKey) : 0;
    /// <summary>CAI3.4: forwarded UNGATED, the same reasoning `GarrisonedStructureKeyOf` and
    /// `ObjectivePositionOf` already record on this interface — who on YOUR OWN side is down is not
    /// something fog hides, so a fogged view must not answer "nobody" for it.</summary>
    public IReadOnlyList<string> DownedAllyKeysOf(string actorKey) => _inner.DownedAllyKeysOf(actorKey);

    /// <summary>The same "off-board" convention <see cref="EntityFacts"/>'s own `Row`/`Col` doc comment
    /// already establishes — no new sentinel invented for "unseen".</summary>
    static readonly EntityFacts UnknownFacts = new(Side: -1, TypeId: 0, HpMilli: 0, ElementId: -1, Row: -1, Col: -1,
        IsMindControlled: false, IsKiller: false, StatusMask: 0);

    bool IsKnownToViewer(string actorKey) =>
        _inner.SideOf(actorKey) == _viewerSide
        || (_inner.PositionOf(actorKey) is { } pos && SiegeVisibility.IsVisible(pos, Watchers(), _blocksVision));

    /// <summary>Recomputed on every call, never cached — spec §1's own "no caching" rule, since a
    /// cached watcher list could silently go stale the instant the underlying board moves.</summary>
    List<SiegeVisibility.Watcher> Watchers()
    {
        var live = _inner.LiveActorKeys;
        var watchers = new List<SiegeVisibility.Watcher>(live.Count);
        for (var i = 0; i < live.Count; i++)
        {
            var key = live[i];
            if (_inner.SideOf(key) != _viewerSide) continue;
            if (_inner.PositionOf(key) is not { } pos) continue;
            watchers.Add(new SiegeVisibility.Watcher(pos, _visionRangeOf(key)));
        }

        return watchers;
    }
}
