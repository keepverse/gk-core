using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Siege;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Siege;

/// <summary>
/// base-defense `siege-construction`/`siege-ai`: `ConstructionAi.ChooseBuiltSite` proven standalone,
/// before any live wiring commits to it — the same "mechanism before wiring" precedent this program
/// has used repeatedly (`siege-fog`, 17.4's `SiegeAiIntentSource`, 17.8's `RetargetLedger`).
/// </summary>
public class ConstructionAiTests
{
    static (BoardState Board, GridSpec Spec) Board(int rows, int cols)
    {
        var spec = new GridSpec(rows, cols);
        return (new BoardState(spec), spec);
    }

    static StructureDef Def(string id, long rubble, long ironwork) => new()
    {
        StructureId = id, Name = id, AcquisitionPaths = new[] { AcquisitionPath.Built },
        ConstructRubbleCost = rubble, ConstructIronworkCost = ironwork,
    };

    static readonly GridPos Builder = new(10, 15); // safely in Approach on a 20x20 board (matches SiegeConstructionTests' own fixture)

    [Fact]
    public void Picks_the_first_affordable_structure_at_the_first_legal_compass_cell()
    {
        var (board, spec) = Board(20, 20);
        var moat = Def("moat", rubble: 10, ironwork: 5);

        var choice = ConstructionAi.ChooseBuiltSite(
            new[] { moat }, Builder, board, spec,
            boardSide: 20, coreSideMilli: 400, rampartThickness: 1,
            sectorRubble: 10, sectorIronwork: 5, requiredSlotKindSatisfiedAt: (_, _) => true);

        Assert.NotNull(choice);
        Assert.Equal("moat", choice!.Value.Structure.StructureId);
        Assert.Equal(new GridPos(9, 15), choice.Value.Cell); // first compass offset: (-1, 0)
    }

    [Fact]
    public void Skips_an_unaffordable_structure_and_tries_the_next_one()
    {
        var (board, spec) = Board(20, 20);
        var expensive = Def("keep", rubble: 100, ironwork: 100);
        var cheap = Def("moat", rubble: 10, ironwork: 5);

        var choice = ConstructionAi.ChooseBuiltSite(
            new[] { expensive, cheap }, Builder, board, spec,
            boardSide: 20, coreSideMilli: 400, rampartThickness: 1,
            sectorRubble: 10, sectorIronwork: 5, requiredSlotKindSatisfiedAt: (_, _) => true);

        Assert.NotNull(choice);
        Assert.Equal("moat", choice!.Value.Structure.StructureId);
    }

    [Fact]
    public void Skips_an_occupied_cell_and_tries_the_next_compass_direction()
    {
        var (board, spec) = Board(20, 20);
        var moat = Def("moat", rubble: 0, ironwork: 0);
        board.Place("other-actor", new GridPos(9, 15)); // the first compass offset: (-1, 0)

        var choice = ConstructionAi.ChooseBuiltSite(
            new[] { moat }, Builder, board, spec,
            boardSide: 20, coreSideMilli: 400, rampartThickness: 1,
            sectorRubble: 0, sectorIronwork: 0, requiredSlotKindSatisfiedAt: (_, _) => true);

        Assert.NotNull(choice);
        Assert.Equal(new GridPos(9, 16), choice!.Value.Cell); // second compass offset: (-1, 1)
    }

    [Fact]
    public void Returns_null_when_nothing_is_affordable()
    {
        var (board, spec) = Board(20, 20);
        var moat = Def("moat", rubble: 100, ironwork: 100);

        var choice = ConstructionAi.ChooseBuiltSite(
            new[] { moat }, Builder, board, spec,
            boardSide: 20, coreSideMilli: 400, rampartThickness: 1,
            sectorRubble: 10, sectorIronwork: 5, requiredSlotKindSatisfiedAt: (_, _) => true);

        Assert.Null(choice);
    }

    [Fact]
    public void Returns_null_when_no_adjacent_cell_is_legal_anywhere()
    {
        var (board, spec) = Board(20, 20);
        var moat = Def("moat", rubble: 0, ironwork: 0);
        for (var d = -1; d <= 1; d++)
            for (var e = -1; e <= 1; e++)
                if (d != 0 || e != 0) board.Place($"blocker-{d}-{e}", new GridPos(Builder.Row + d, Builder.Col + e));

        var choice = ConstructionAi.ChooseBuiltSite(
            new[] { moat }, Builder, board, spec,
            boardSide: 20, coreSideMilli: 400, rampartThickness: 1,
            sectorRubble: 0, sectorIronwork: 0, requiredSlotKindSatisfiedAt: (_, _) => true);

        Assert.Null(choice);
    }

    [Fact]
    public void Never_chooses_a_core_cell()
    {
        // side=20, coreSideMilli=400 -> coreSideCells=8, coreHalfCeil=4: cells within Chebyshev
        // distance 3 of the centre (10,10) are Core. A builder straddling that boundary has SOME
        // Core-zone neighbours (correctly rejected) and some Approach/Rampart ones (legal) -- this
        // asserts the invariant directly via ZoneOf rather than hand-picking which cell should win.
        var (board, spec) = Board(20, 20);
        var moat = Def("moat", rubble: 0, ironwork: 0);
        var builderStraddlingCore = new GridPos(10, 6); // Chebyshev 4 from centre (Rampart) -- 3 of its 8 neighbours dip to Chebyshev 3 (Core)

        var choice = ConstructionAi.ChooseBuiltSite(
            new[] { moat }, builderStraddlingCore, board, spec,
            boardSide: 20, coreSideMilli: 400, rampartThickness: 1,
            sectorRubble: 0, sectorIronwork: 0, requiredSlotKindSatisfiedAt: (_, _) => true);

        Assert.NotNull(choice);
        Assert.NotEqual(FusionRpg.Core.World.District.DistrictZone.Core,
            FusionRpg.Core.World.District.DistrictLayout.ZoneOf(choice!.Value.Cell, side: 20, coreSideMilli: 400, rampartThickness: 1));
    }

    [Fact]
    public void The_required_slot_kind_gate_is_respected()
    {
        var (board, spec) = Board(20, 20);
        var moat = Def("moat", rubble: 0, ironwork: 0);

        var choice = ConstructionAi.ChooseBuiltSite(
            new[] { moat }, Builder, board, spec,
            boardSide: 20, coreSideMilli: 400, rampartThickness: 1,
            sectorRubble: 0, sectorIronwork: 0, requiredSlotKindSatisfiedAt: (_, _) => false);

        Assert.Null(choice); // every cell is otherwise legal, but the slot-kind gate refuses all of them
    }

    [Fact]
    public void Same_inputs_produce_the_same_choice_10000_times()
    {
        var moat = Def("moat", rubble: 10, ironwork: 5);
        (StructureDef Structure, GridPos Cell)? first = null;

        for (var i = 0; i < 10_000; i++)
        {
            var (board, spec) = Board(20, 20);
            var choice = ConstructionAi.ChooseBuiltSite(
                new[] { moat }, Builder, board, spec,
                boardSide: 20, coreSideMilli: 400, rampartThickness: 1,
                sectorRubble: 10, sectorIronwork: 5, requiredSlotKindSatisfiedAt: (_, _) => true);

            first ??= choice;
            Assert.Equal(first!.Value.Structure.StructureId, choice!.Value.Structure.StructureId);
            Assert.Equal(first.Value.Cell, choice.Value.Cell);
        }
    }
}
