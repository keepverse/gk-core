using FusionRpg.Core.Actions;

namespace FusionRpg.Core.Battle.Board;

/// <summary>
/// base-defense `siege-positions` §"name collision": projects the tactical board
/// (<see cref="BoardState"/>, this program's own grid) into the injector-shaped
/// <see cref="Combat.BoardSnapshot"/> the existing combat consumers (`TargetResolver`,
/// `CombatDamageDispatcher`, `EffectBag.BoardSnapshot`) already read — an ADAPTER, deliberately.
/// <see cref="Combat.BoardSnapshot"/>'s field shape mirrors the injector's live lawn capture and must
/// not drift toward a grid; the tactical board must not grow ptr semantics. Two representations, one
/// conversion, in one place.
///
/// <para>Takes <see cref="IBattleView"/> rather than <c>BattleRunState</c> directly — the latter is
/// private/nested per B13's own deviation note, and <c>IBattleView</c> is already the documented seam
/// everything outside <c>BattleEngine</c> reads a battle through (`StubIntentSource` does the same).
/// No new member on the interface: <see cref="IBattleView.SideOf"/> already returns exactly the two
/// values (0/1) this adapter needs to reconstruct the string side <see cref="Combat.BoardEntitySnap"/>
/// wants.</para>
/// </summary>
public static class BoardSnapshotAdapter
{
    /// <summary>Only actors with a real board position are included — an actor in a boardless battle
    /// (every caller until this module) never appears, so the resulting snapshot is empty exactly like
    /// <see cref="Combat.BoardSnapshot.Empty"/> for anyone who never wires it in.</summary>
    public static Combat.BoardSnapshot ToCombatSnapshot(IBattleView view)
    {
        var entities = new List<Combat.BoardEntitySnap>();
        foreach (var key in view.LiveActorKeys)
        {
            if (view.PositionOf(key) is not { } pos) continue;
            var facts = view.FactsOf(key);
            entities.Add(new Combat.BoardEntitySnap
            {
                Ptr = key,
                Side = view.SideOf(key) == 0 ? "squad" : "wave",
                TypeId = facts.TypeId,
                Row = pos.Row,
                Col = pos.Col,
                MindControlled = facts.IsMindControlled,
                Living = true // LiveActorKeys already filters to Active actors
            });
        }
        return new Combat.BoardSnapshot(entities);
    }
}
