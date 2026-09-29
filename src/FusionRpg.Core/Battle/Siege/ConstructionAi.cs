using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.World;

namespace FusionRpg.Core.Battle.Siege;

/// <summary>
/// base-defense `siege-construction`/`siege-ai`: the decision half of "which structure, built where"
/// for the `Built` acquisition path — proven standalone before any live wiring commits to it, this
/// program's own established "mechanism before wiring" precedent (`siege-fog`, 17.4's own
/// `SiegeAiIntentSource`, 17.8's `RetargetLedger`). Deliberately reuses
/// <see cref="ConstructionCost.CanAffordBuilt"/> and <see cref="ConstructionPlacement.CanPlace"/>
/// VERBATIM (Law 1 — no second implementation of either rule) rather than re-deriving affordability
/// or legality here.
///
/// <para><b>What this does NOT do, named rather than hidden</b>: it does not spend the cost (the
/// caller applies <see cref="ConstructionCost.SpendBuilt"/> once it actually commits), it does not
/// fire <see cref="ConstructionActivation.Fire"/>, and it is not called from any live
/// <see cref="Actions.IIntentSource"/> or <c>DistrictAssaultResolver</c> path today — confirmed by
/// construction: this file has zero non-doc-comment references outside itself and its own tests.
/// Wiring a real caller (deciding WHEN an actor should build at all, versus fight, and sourcing
/// `sectorRubble`/`sectorIronwork` from <see cref="BoardEconomy.SiegeDepot"/> rather than a raw
/// sector snapshot) is real, separate, un-started work this function does not attempt — the same
/// spec addendum that warns against shipping an unverified live AI decision-maker under time pressure
/// (Relic's five-patch cover-seeking regression) applies here with the same force it already did to
/// 17.4's own live intent source.</para>
/// </summary>
public static class ConstructionAi
{
    /// <summary>
    /// The 8 Chebyshev-adjacent cells around a builder, tried in a FIXED compass order for R5
    /// determinism — the same board, same stocks, same buildable list always yields the same choice,
    /// regardless of caller-side iteration order elsewhere.
    /// </summary>
    static readonly (int DRow, int DCol)[] CompassOffsets =
    {
        (-1, 0), (-1, 1), (0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1),
    };

    /// <summary>
    /// Picks the single (structure, cell) pair the `Built` acquisition path should place this turn,
    /// or `null` when nothing in <paramref name="buildable"/> is both affordable and legal anywhere
    /// adjacent to <paramref name="builderPosition"/>. Structures are tried in the order the caller
    /// supplies (this function does not re-sort — the caller supplies domain knowledge, e.g. a
    /// priority order, this class does not itself need to know, the same "caller-supplied, not
    /// computed here" precedent <see cref="ConstructionPlacement.CanPlace"/>'s own rule 4/5 already
    /// establish); within one structure, the 8 adjacent cells are tried in the fixed compass order
    /// above.
    /// </summary>
    public static (StructureDef Structure, GridPos Cell)? ChooseBuiltSite(
        IReadOnlyList<StructureDef> buildable, GridPos builderPosition, BoardState board, GridSpec spec,
        int boardSide, int coreSideMilli, int rampartThickness, long sectorRubble, long sectorIronwork,
        Func<StructureDef, GridPos, bool> requiredSlotKindSatisfiedAt)
    {
        if (buildable is null) throw new ArgumentNullException(nameof(buildable));
        if (board is null) throw new ArgumentNullException(nameof(board));
        if (spec is null) throw new ArgumentNullException(nameof(spec));
        if (requiredSlotKindSatisfiedAt is null) throw new ArgumentNullException(nameof(requiredSlotKindSatisfiedAt));

        for (var i = 0; i < buildable.Count; i++)
        {
            var candidate = buildable[i];
            if (!ConstructionCost.CanAffordBuilt(sectorRubble, sectorIronwork, candidate)) continue;

            for (var d = 0; d < CompassOffsets.Length; d++)
            {
                var (dRow, dCol) = CompassOffsets[d];
                var cell = new GridPos(builderPosition.Row + dRow, builderPosition.Col + dCol);
                var satisfied = requiredSlotKindSatisfiedAt(candidate, cell);

                if (ConstructionPlacement.CanPlace(
                        board, spec, cell, builderPosition, boardSide, coreSideMilli, rampartThickness, satisfied))
                    return (candidate, cell);
            }
        }

        return null;
    }
}
