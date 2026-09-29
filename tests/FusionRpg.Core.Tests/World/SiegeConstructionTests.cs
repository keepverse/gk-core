using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Siege;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// base-defense `siege-construction` (spec-siege-construction.md): the `rubble`/`ironwork` two-stock
/// economy, the lossy/gated refine chain (decision 28), and the one placement validator shared by all
/// four acquisition paths (§6) — scoped to the pure stock arithmetic and the board-level gate; the
/// `WorldCommandKinds.Assault` order-kind plumbing and the action-system wiring for
/// `Assembled`/`Summoned`/`Laboured` are named as deferred scope in `SiegeConstruction.cs`'s own doc
/// comment, not built here.
/// </summary>
public class SiegeConstructionTests
{
    static WorldState World(params WorldSector[] sectors) => new() { TemplateId = "t", Sectors = sectors };

    [Fact]
    public void World_goldens_are_byte_identical_at_zero_stock()
    {
        var withDefaults = World(new WorldSector { SectorId = "s1" });
        var withoutFields = World(new WorldSector { SectorId = "s1" });

        Assert.Equal(WorldCanonical.Write(withoutFields), WorldCanonical.Write(withDefaults));
        Assert.DoesNotContain("sector-rubble", WorldCanonical.Write(withDefaults), StringComparison.Ordinal);
        Assert.DoesNotContain("sector-ironwork", WorldCanonical.Write(withDefaults), StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_gains_exactly_one_row_per_nonzero_stock()
    {
        var world = World(new WorldSector { SectorId = "s1", RubbleStock = 40, IronworkStock = 0 });
        var text = WorldCanonical.Write(world);

        Assert.Contains("sector-rubble\ts1\t40", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sector-ironwork", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_stocks_round_trip_as_long_fields()
    {
        var sector = new WorldSector { SectorId = "s1", RubbleStock = 9_000_000_000L, IronworkStock = 1_234_567L };
        Assert.Equal(9_000_000_000L, sector.RubbleStock);
        Assert.Equal(1_234_567L, sector.IronworkStock);
    }

    [Fact]
    public void Refining_is_lossy()
    {
        // Decision 28: 4 rubble does not become 4 ironwork at any authored yield below 1000‰.
        var ironwork = SiegeConstruction.Refine(rubbleSpent: 4, yieldMilli: 600);
        Assert.True(ironwork < 4);
        Assert.Equal(2, ironwork); // checked(4 * 600 / 1000) = 2
    }

    [Fact]
    public void Refine_divides_by_1000_last_and_is_checked()
    {
        Assert.Equal(0, SiegeConstruction.Refine(rubbleSpent: 1, yieldMilli: 600)); // 600/1000 truncates to 0
        Assert.Throws<OverflowException>(() => SiegeConstruction.Refine(long.MaxValue, 600));
    }

    [Fact]
    public void Refine_rejects_negative_inputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SiegeConstruction.Refine(-1, 600));
        Assert.Throws<ArgumentOutOfRangeException>(() => SiegeConstruction.Refine(4, -1));
    }

    [Fact]
    public void Refining_is_gated_by_a_refinery_structure_not_a_cooldown()
    {
        Assert.Equal(0, SiegeConstruction.RefineGated(hasWorkingRefinery: false, rubbleSpent: 100, yieldMilli: 600));
        Assert.Equal(60, SiegeConstruction.RefineGated(hasWorkingRefinery: true, rubbleSpent: 100, yieldMilli: 600));
    }

    [Fact]
    public void Refinery_joins_LoamSource_and_Storage_as_a_real_structure_kind()
    {
        var def = new StructureDef
        {
            StructureId = "test-refinery", Name = "Test Refinery", Kind = StructureKind.Refinery,
            RequiredSlotKind = SlotKind.Wildland, AcquisitionPaths = new[] { AcquisitionPath.Built }
        };
        Assert.Equal(StructureKind.Refinery, def.Kind);
        // Validate does not throw for the new kind -- it carries no kind-specific validation rule.
        StructureCatalog.Validate(new[] { def });
    }

    // -- ConstructionPlacement: the one validator shared by all four acquisition paths (§6) --

    static (BoardState board, GridSpec spec) Board(int rows, int cols)
    {
        var spec = new GridSpec(rows, cols);
        return (new BoardState(spec), spec);
    }

    // With side=20, coreSideMilli=400: coreSideCells=max(1,400*20/1000)=8, coreHalfCeil=(8+1)/2=4, so
    // every cell within Chebyshev distance 3 of (10,10) is Core. (10,15)/(10,16) sit at distance 5/6 —
    // safely in Approach, and adjacent to each other — the fixture used by every non-Core test below.
    static readonly GridPos NonCoreBuilder = new(10, 15);
    static readonly GridPos NonCoreTarget = new(10, 16);

    [Fact]
    public void Adjacent_open_unoccupied_non_core_cell_is_placeable()
    {
        var (board, spec) = Board(20, 20);

        Assert.True(ConstructionPlacement.CanPlace(
            board, spec, NonCoreTarget, NonCoreBuilder,
            boardSide: 20, coreSideMilli: 400, rampartThickness: 1, requiredSlotKindSatisfied: true));
    }

    [Fact]
    public void Placement_requires_adjacency()
    {
        var (board, spec) = Board(20, 20);
        var farCell = new GridPos(10, 18); // Chebyshev distance 3 from NonCoreBuilder

        Assert.False(ConstructionPlacement.CanPlace(
            board, spec, farCell, NonCoreBuilder,
            boardSide: 20, coreSideMilli: 400, rampartThickness: 1, requiredSlotKindSatisfied: true));
    }

    [Fact]
    public void Occupied_cell_is_rejected()
    {
        var (board, spec) = Board(20, 20);
        board.Place("other-actor", NonCoreTarget);

        Assert.False(ConstructionPlacement.CanPlace(
            board, spec, NonCoreTarget, NonCoreBuilder,
            boardSide: 20, coreSideMilli: 400, rampartThickness: 1, requiredSlotKindSatisfied: true));
    }

    [Fact]
    public void Blocking_cell_is_rejected()
    {
        var cells = Enumerable.Repeat(CellTerrain.Open, 400).ToList();
        cells[10 * 20 + 16] = CellTerrain.Blocking;
        var spec = new GridSpec(20, 20, cells);
        var board = new BoardState(spec);

        Assert.False(ConstructionPlacement.CanPlace(
            board, spec, NonCoreTarget, NonCoreBuilder,
            boardSide: 20, coreSideMilli: 400, rampartThickness: 1, requiredSlotKindSatisfied: true));
    }

    [Fact]
    public void Nothing_can_be_built_in_the_core()
    {
        var (board, spec) = Board(20, 20);
        // DistrictLayout.ZoneOf centers the Core on (side/2, side/2) = (10, 10).
        var center = new GridPos(10, 10);
        var builder = new GridPos(10, 9);

        Assert.False(ConstructionPlacement.CanPlace(
            board, spec, center, builder,
            boardSide: 20, coreSideMilli: 400, rampartThickness: 1, requiredSlotKindSatisfied: true));
    }

    [Fact]
    public void Off_board_cell_is_rejected()
    {
        var (board, spec) = Board(5, 5);
        var builder = new GridPos(4, 4);
        var offBoard = new GridPos(5, 5);

        Assert.False(ConstructionPlacement.CanPlace(
            board, spec, offBoard, builder,
            boardSide: 5, coreSideMilli: 400, rampartThickness: 1, requiredSlotKindSatisfied: true));
    }

    [Fact]
    public void Required_slot_kind_mismatch_is_rejected()
    {
        var (board, spec) = Board(20, 20);

        Assert.False(ConstructionPlacement.CanPlace(
            board, spec, NonCoreTarget, NonCoreBuilder,
            boardSide: 20, coreSideMilli: 400, rampartThickness: 1, requiredSlotKindSatisfied: false));
    }

    [Fact]
    public void Either_side_may_build_anywhere_legal_because_the_gate_has_no_ownership_parameter()
    {
        // Decision 4: no ownership check anywhere in placement. Enforced structurally here — the same
        // board, same cell, same builder position produces the same verdict regardless of which side
        // is asking, because CanPlace's signature has no faction/owner parameter to differ on.
        var (boardA, specA) = Board(20, 20);
        var (boardB, specB) = Board(20, 20);

        var attackerVerdict = ConstructionPlacement.CanPlace(
            boardA, specA, NonCoreTarget, NonCoreBuilder, boardSide: 20, coreSideMilli: 400, rampartThickness: 1,
            requiredSlotKindSatisfied: true);
        var defenderVerdict = ConstructionPlacement.CanPlace(
            boardB, specB, NonCoreTarget, NonCoreBuilder, boardSide: 20, coreSideMilli: 400, rampartThickness: 1,
            requiredSlotKindSatisfied: true);

        Assert.True(attackerVerdict);
        Assert.Equal(attackerVerdict, defenderVerdict);
    }

    [Fact]
    public void Construct_costs_default_to_zero_so_every_shipped_row_is_unaffected()
    {
        var def = new StructureDef { StructureId = "test.default-cost", Name = "Test", AcquisitionPaths = new[] { AcquisitionPath.Built } };
        Assert.Equal(0, def.ConstructRubbleCost);
        Assert.Equal(0, def.ConstructIronworkCost);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void A_negative_construct_cost_is_rejected_at_load(long rubble, long ironwork)
    {
        var bad = new StructureDef
        {
            StructureId = "test.negative-cost",
            Name = "Test",
            ConstructRubbleCost = rubble,
            ConstructIronworkCost = ironwork,
            AcquisitionPaths = new[] { AcquisitionPath.Built }
        };
        Assert.Throws<InvalidOperationException>(() => StructureCatalog.Validate(new[] { bad }));
    }

    [Fact]
    public void Every_shipped_structure_still_validates_with_the_new_fields_present()
    {
        // Confirms the additive schema change moves nothing: the real catalog (built at module load)
        // still validates end to end with ConstructRubbleCost/ConstructIronworkCost defaulted to 0.
        Assert.NotEmpty(StructureCatalog.All);
    }

    // ---- 15.4: the two raw faucets --------------------------------------------------------------

    static WorldSector Sector(params WorldSlot[] slots) => new() { SectorId = "s1", Slots = slots };

    [Fact]
    public void An_intact_shard_vein_or_material_seam_yields_nothing()
    {
        var sector = Sector(
            new WorldSlot { SlotIndex = 0, SlotTypeId = "shard-vein", GuardState = GuardState.Intact },
            new WorldSlot { SlotIndex = 1, SlotTypeId = "material-seam", GuardState = GuardState.Intact });

        var (ironwork, rubble) = SiegeConstruction.Yield(sector);

        Assert.Equal(0, ironwork);
        Assert.Equal(0, rubble);
    }

    [Fact]
    public void A_cleared_shard_vein_yields_ironwork_and_a_cleared_material_seam_yields_rubble()
    {
        var sector = Sector(
            new WorldSlot { SlotIndex = 0, SlotTypeId = "shard-vein", GuardState = GuardState.Cleared },
            new WorldSlot { SlotIndex = 1, SlotTypeId = "material-seam", GuardState = GuardState.Cleared });

        var (ironwork, rubble) = SiegeConstruction.Yield(sector);

        // The spec's own numbers, matched to what the guards already say (GuardHeavy x4, GuardMedium x3).
        Assert.Equal(SiegeTuningPolicy.Construction.ShardVeinYieldPerTurn, ironwork);
        Assert.Equal(SiegeTuningPolicy.Construction.MaterialSeamYieldPerTurn, rubble);
        Assert.True(ironwork > 0);
        Assert.True(rubble > 0);
    }

    [Fact]
    public void Multiple_cleared_veins_in_one_sector_sum()
    {
        var sector = Sector(
            new WorldSlot { SlotIndex = 0, SlotTypeId = "shard-vein", GuardState = GuardState.Cleared },
            new WorldSlot { SlotIndex = 1, SlotTypeId = "shard-vein", GuardState = GuardState.Cleared });

        var (ironwork, _) = SiegeConstruction.Yield(sector);

        Assert.Equal(SiegeTuningPolicy.Construction.ShardVeinYieldPerTurn * 2, ironwork);
    }

    [Fact]
    public void Other_slot_kinds_and_unknown_slot_type_ids_never_yield()
    {
        var sector = Sector(
            new WorldSlot { SlotIndex = 0, SlotTypeId = "wildland", GuardState = GuardState.Cleared },
            new WorldSlot { SlotIndex = 1, SlotTypeId = "not-a-real-slot-type", GuardState = GuardState.Cleared });

        var (ironwork, rubble) = SiegeConstruction.Yield(sector);

        Assert.Equal(0, ironwork);
        Assert.Equal(0, rubble);
    }

    [Fact]
    public void Production_credits_the_yield_into_the_sectors_own_stocks()
    {
        var world = World(Sector(
            new WorldSlot { SlotIndex = 0, SlotTypeId = "shard-vein", GuardState = GuardState.Cleared },
            new WorldSlot { SlotIndex = 1, SlotTypeId = "material-seam", GuardState = GuardState.Cleared })
            with { RubbleStock = 10, IronworkStock = 5 });

        var next = SiegeConstruction.Production(world, new TurnReport(), "Production");
        var sector = next.Sectors.Single();

        Assert.Equal(10 + SiegeTuningPolicy.Construction.MaterialSeamYieldPerTurn, sector.RubbleStock);
        Assert.Equal(5 + SiegeTuningPolicy.Construction.ShardVeinYieldPerTurn, sector.IronworkStock);
    }

    [Fact]
    public void Production_leaves_a_sector_with_no_yield_byte_identical()
    {
        var world = World(Sector(new WorldSlot { SlotIndex = 0, SlotTypeId = "wildland" }));

        var next = SiegeConstruction.Production(world, new TurnReport(), "Production");

        Assert.Equal(world.Sectors.Single(), next.Sectors.Single());
    }

    [Fact]
    public void Production_never_produces_a_negative_stock()
    {
        // Sanity anchor, not a real overflow test: Yield is additive-only (no subtraction anywhere in
        // this phase), so a negative result would mean a sign error, not legitimate spend.
        var world = World(Sector(new WorldSlot { SlotIndex = 0, SlotTypeId = "shard-vein", GuardState = GuardState.Cleared }));

        var next = SiegeConstruction.Production(world, new TurnReport(), "Production");

        Assert.True(next.Sectors.Single().IronworkStock >= 0);
    }

    // ---- 15.4b / audit F10 / decision 48: the per-slot depletion increment -----------------------

    static WorldSector OwnedSector(params WorldSlot[] slots) =>
        Sector(slots) with { OwnerFactionId = "dave" };

    [Fact]
    public void An_owned_rootbed_slot_advances_depletion_by_one_harvest_worth()
    {
        var world = World(OwnedSector(new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId }));

        var next = SiegeConstruction.AdvanceDepletion(world, new TurnReport(), "Production");

        Assert.Equal(StructurePolicy.DepletionPerHarvestMilli, next.Sectors.Single().Slots.Single().SlotDepletionMilli);
    }

    [Fact]
    public void An_unowned_sectors_rootbed_slot_never_advances()
    {
        var world = World(Sector(new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId }));

        var next = SiegeConstruction.AdvanceDepletion(world, new TurnReport(), "Production");

        Assert.Equal(0, next.Sectors.Single().Slots.Single().SlotDepletionMilli);
    }

    [Fact]
    public void A_cleared_shard_vein_and_material_seam_both_advance_depletion()
    {
        var world = World(Sector(
            new WorldSlot { SlotIndex = 0, SlotTypeId = "shard-vein", GuardState = GuardState.Cleared },
            new WorldSlot { SlotIndex = 1, SlotTypeId = "material-seam", GuardState = GuardState.Cleared }));

        var next = SiegeConstruction.AdvanceDepletion(world, new TurnReport(), "Production");

        Assert.All(next.Sectors.Single().Slots, s => Assert.Equal(StructurePolicy.DepletionPerHarvestMilli, s.SlotDepletionMilli));
    }

    [Fact]
    public void A_guarded_shard_vein_never_advances()
    {
        var world = World(Sector(new WorldSlot { SlotIndex = 0, SlotTypeId = "shard-vein", GuardState = GuardState.Intact }));

        var next = SiegeConstruction.AdvanceDepletion(world, new TurnReport(), "Production");

        Assert.Equal(0, next.Sectors.Single().Slots.Single().SlotDepletionMilli);
    }

    [Fact]
    public void Other_slot_kinds_never_advance_depletion()
    {
        var world = World(OwnedSector(new WorldSlot { SlotIndex = 0, SlotTypeId = "wildland" }));

        var next = SiegeConstruction.AdvanceDepletion(world, new TurnReport(), "Production");

        Assert.Equal(0, next.Sectors.Single().Slots.Single().SlotDepletionMilli);
    }

    [Fact]
    public void A_non_yielding_world_is_byte_identical()
    {
        var world = World(Sector(new WorldSlot { SlotIndex = 0, SlotTypeId = "wildland" }));

        var next = SiegeConstruction.AdvanceDepletion(world, new TurnReport(), "Production");

        Assert.Equal(world.Sectors.Single(), next.Sectors.Single());
    }

    [Fact]
    public void Crossing_the_exhaustion_threshold_fires_the_event_exactly_once_not_again_next_turn()
    {
        var almostExhausted = new WorldSlot
        {
            SlotIndex = 0, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId,
            SlotDepletionMilli = 1000 - StructurePolicy.DepletionPerHarvestMilli,
        };
        var world = World(OwnedSector(almostExhausted));
        var report = new TurnReport();

        var afterFirstTurn = SiegeConstruction.AdvanceDepletion(world, report, "Production");
        var slot = afterFirstTurn.Sectors.Single().Slots.Single();
        Assert.True(StructurePolicy.IsExhausted(slot.SlotDepletionMilli));
        var exhaustedEvents = report.Entries.Where(e => e.Detail == "slot.exhausted:0").ToList();
        Assert.Single(exhaustedEvents);
        Assert.Equal("s1", exhaustedEvents[0].SectorId);
        Assert.Equal("dave", exhaustedEvents[0].Audience);

        // A second, already-exhausted turn keeps advancing the counter (no clamp, per BoardEconomy's
        // own "no clamp" precedent) but must NOT fire the transition event again.
        var afterSecondTurn = SiegeConstruction.AdvanceDepletion(afterFirstTurn, report, "Production");
        Assert.True(afterSecondTurn.Sectors.Single().Slots.Single().SlotDepletionMilli > slot.SlotDepletionMilli);
        Assert.Single(report.Entries.Where(e => e.Detail == "slot.exhausted:0"));
    }

    [Fact]
    public void A_slot_not_yielding_this_turn_never_fires_the_exhaustion_event()
    {
        var world = World(Sector(new WorldSlot
        {
            SlotIndex = 0, SlotTypeId = "shard-vein", GuardState = GuardState.Intact,
            SlotDepletionMilli = 1000 - StructurePolicy.DepletionPerHarvestMilli,
        }));
        var report = new TurnReport();

        SiegeConstruction.AdvanceDepletion(world, report, "Production");

        Assert.Empty(report.Entries);
    }
}
