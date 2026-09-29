using FusionRpg.Core.Actions;
using FusionRpg.Core.World.District;

namespace FusionRpg.Core.Battle.Board;

/// <summary>
/// base-defense `siege-construction` §6: ONE placement validator, shared by all four acquisition
/// paths (`Built`/`Assembled`/`Summoned`/`Laboured`) rather than one per path -- the spec's own
/// framing, and the reason there is exactly one method here.
///
/// <para><b>Rule 4 (`RequiredSlotKind` match) is a caller-supplied bool, not computed here.</b> The
/// tactical board (<see cref="GridSpec"/>/<see cref="BoardState"/>) has no mapping from a
/// <see cref="Actions.GridPos"/> cell to a world-layer <c>SlotKind</c> today -- that data flow is
/// `siege-resolver`'s job once it assembles a real board from `DistrictLayout`'s geometry, the exact
/// same scoping `Placement.PlaceActors`'s own doc comment already recorded for actor placement. This
/// keeps the gate honest about what it can prove today rather than guessing at unbuilt plumbing.</para>
///
/// <para><b>Rule 5 (no ownership check, decision 4) is enforced by omission</b>: this method takes no
/// faction/owner parameter at all, so there is structurally no way for a caller to smuggle one in.
/// Either side may build anywhere legal.</para>
/// </summary>
public static class ConstructionPlacement
{
    public static bool CanPlace(
        BoardState board, GridSpec spec, GridPos cell, GridPos builderPosition,
        int boardSide, int coreSideMilli, int rampartThickness,
        bool requiredSlotKindSatisfied)
    {
        if (board is null) throw new ArgumentNullException(nameof(board));
        if (spec is null) throw new ArgumentNullException(nameof(spec));

        // Rule 1: on the board, not Blocking.
        if (!spec.Contains(cell)) return false;
        if (spec.TerrainAt(cell) == CellTerrain.Blocking) return false;

        // Rule 1b (decision 10): never inside the Core, either side, both phases.
        if (DistrictLayout.ZoneOf(cell, boardSide, coreSideMilli, rampartThickness) == DistrictZone.Core)
            return false;

        // Rule 2: unoccupied.
        if (board.OccupantAt(cell) is not null) return false;

        // Rule 3: adjacent (Chebyshev distance 1) -- you build next to yourself, not from afar.
        if (GridDistance.Chebyshev(builderPosition, cell) != 1) return false;

        // Rule 4: RequiredSlotKind match, when the structure declares one -- see the class doc comment.
        if (!requiredSlotKindSatisfied) return false;

        return true;
    }
}
