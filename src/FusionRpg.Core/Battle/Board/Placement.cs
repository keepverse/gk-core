using FusionRpg.Core.Actions;

namespace FusionRpg.Core.Battle.Board;

public sealed class PlacementRejection : Exception
{
    public PlacementRejection(string message) : base(message) { }
}

/// <summary>
/// base-defense `siege-positions` §6: deterministic initial placement. Actors are placed in ORDINAL
/// KEY order, never roster order and never dictionary order — the same discipline
/// <c>LegionSupply.Resolve</c>'s own <c>.OrderBy(x => x.Entity.EntityId, StringComparer.Ordinal)</c>
/// already established. Placement order determines which actor gets a contested cell, so it
/// determines the battle — this is not cosmetic.
///
/// <para>Deliberately a standalone, pure mechanism rather than something wired into
/// `BattleRunState`'s own constructor: nothing today supplies real district-derived candidate cells
/// (that data flow is `siege-resolver`'s job, a later level-7 module, once it assembles a real
/// `BattleSetup` from `DistrictLayout`'s zone geometry). This module proves the ORDERING rule is
/// correct and deterministic; wiring it to a real cell source is the consuming module's job.</para>
/// </summary>
public static class Placement
{
    /// <summary>
    /// Places each of <paramref name="actorKeys"/> onto <paramref name="board"/>, taking cells from
    /// <paramref name="candidateCells"/> IN THE ORDER GIVEN, one per actor, assigning to actors sorted
    /// ordinally by key. A caller supplying fewer cells than actors is a caller error — thrown loudly,
    /// never a silent partial placement.
    /// </summary>
    public static void PlaceActors(BoardState board, IEnumerable<string> actorKeys, IReadOnlyList<GridPos> candidateCells)
    {
        if (board is null) throw new ArgumentNullException(nameof(board));
        if (candidateCells is null) throw new ArgumentNullException(nameof(candidateCells));

        var ordered = actorKeys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (candidateCells.Count < ordered.Count)
            throw new PlacementRejection(
                $"placement: {ordered.Count} actor(s) but only {candidateCells.Count} candidate cell(s)");

        for (var i = 0; i < ordered.Count; i++)
            board.Place(ordered[i], candidateCells[i]);
    }
}
