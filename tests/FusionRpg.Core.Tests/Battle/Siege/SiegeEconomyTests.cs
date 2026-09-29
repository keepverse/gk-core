using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Siege;

/// <summary>
/// base-defense `siege-economy` (spec-siege-economy.md): board income by occupation (never by world
/// ownership, §2b), a battle-scoped depot that reconciles spend-only, and F11's capture-transfers-the-
/// stockpile rule. Deliberately decoupled from `BoardState`'s live occupancy and the turn engine — see
/// `BoardEconomy.cs`'s own doc comment for what is named as deferred wiring.
/// </summary>
public class SiegeEconomyTests
{
    static GridPos Cell(int r, int c) => new(r, c);

    // -- YieldsFor --

    [Fact]
    public void Ungarrisoned_nodes_yield_nothing()
    {
        var spec = new GridSpec(5, 5);
        var nodes = new[] { new BoardNode(Cell(0, 0), Exhausted: false) };
        var yields = BoardEconomy.YieldsFor(spec, nodes, Array.Empty<BoardOccupant>(), loamPerRound: 5, ironworkPerRound: 3);
        Assert.Empty(yields);
    }

    [Fact]
    public void A_structure_occupying_a_node_yields_nothing()
    {
        var spec = new GridSpec(5, 5);
        var nodes = new[] { new BoardNode(Cell(0, 0), Exhausted: false) };
        var occupants = new[] { new BoardOccupant(Cell(0, 0), "attacker", CombatantKind.Structure) };
        var yields = BoardEconomy.YieldsFor(spec, nodes, occupants, loamPerRound: 5, ironworkPerRound: 3);
        Assert.Empty(yields);
    }

    [Fact]
    public void Income_accrues_to_the_occupant_not_the_owner()
    {
        var spec = new GridSpec(5, 5);
        var nodes = new[] { new BoardNode(Cell(0, 0), Exhausted: false) };
        var occupants = new[] { new BoardOccupant(Cell(0, 0), "attacker", CombatantKind.Animate) };
        var yields = BoardEconomy.YieldsFor(spec, nodes, occupants, loamPerRound: 5, ironworkPerRound: 3);
        var y = Assert.Single(yields);
        Assert.Equal("attacker", y.Side);
        Assert.Equal(5, y.LoamAmount);
        Assert.Equal(3, y.IronworkAmount);
    }

    [Fact]
    public void Exhausted_nodes_yield_nothing()
    {
        var spec = new GridSpec(5, 5);
        var nodes = new[] { new BoardNode(Cell(0, 0), Exhausted: true) };
        var occupants = new[] { new BoardOccupant(Cell(0, 0), "attacker", CombatantKind.Animate) };
        var yields = BoardEconomy.YieldsFor(spec, nodes, occupants, loamPerRound: 5, ironworkPerRound: 3);
        Assert.Empty(yields);
    }

    [Fact]
    public void Income_order_is_ordinal_by_cell_index_over_many_runs()
    {
        var spec = new GridSpec(5, 5);
        var nodes = new[]
        {
            new BoardNode(Cell(4, 4), Exhausted: false),
            new BoardNode(Cell(0, 0), Exhausted: false),
            new BoardNode(Cell(2, 2), Exhausted: false),
        };
        var occupants = new[]
        {
            new BoardOccupant(Cell(4, 4), "a", CombatantKind.Animate),
            new BoardOccupant(Cell(0, 0), "b", CombatantKind.Animate),
            new BoardOccupant(Cell(2, 2), "c", CombatantKind.Animate),
        };

        IReadOnlyList<GridPos> Order() => BoardEconomy.YieldsFor(spec, nodes, occupants, 1, 1).Select(y => y.Cell).ToList();
        var first = Order();
        for (var i = 0; i < 200; i++)
            Assert.Equal(first, Order());

        Assert.Equal(new[] { Cell(0, 0), Cell(2, 2), Cell(4, 4) }, first);
    }

    [Fact]
    public void Depletion_advances_on_harvest_not_on_time()
    {
        var before = 500;
        Assert.Equal(before, BoardEconomy.AdvanceDepletionMilli(before, yieldedThisRound: false));
        Assert.True(BoardEconomy.AdvanceDepletionMilli(before, yieldedThisRound: true) > before);
    }

    [Fact]
    public void Contested_nodes_deplete_faster_than_an_uncontested_one()
    {
        // "Contested" here means harvested every round by whichever side holds it, vs harvested rarely.
        var contested = 0;
        var quiet = 0;
        for (var round = 0; round < 5; round++)
        {
            contested = BoardEconomy.AdvanceDepletionMilli(contested, yieldedThisRound: true);
            if (round % 4 == 0) quiet = BoardEconomy.AdvanceDepletionMilli(quiet, yieldedThisRound: true);
        }
        Assert.True(contested > quiet);
    }

    // -- SiegeDepot --

    [Fact]
    public void Board_income_never_reaches_world_stock()
    {
        var depot = SiegeDepot.SeedFromSectorStock(sectorLoam: 1000, sectorIronwork: 0, sectorRubble: 0, depotSeedMilli: 1000);
        depot = depot.CreditLoam(500).CreditLoam(500).CreditLoam(9000); // earn heavily
        Assert.Equal(0, depot.LoamSpentFromWorld); // spend nothing
    }

    [Fact]
    public void Only_spend_crosses_back()
    {
        var depot = SiegeDepot.SeedFromSectorStock(sectorLoam: 100, sectorIronwork: 0, sectorRubble: 0, depotSeedMilli: 1000);
        depot = depot.CreditLoam(30); // total balance 130, 100 world-seeded + 30 board-earned
        depot = depot.SpendLoam(80); // 30 from board, 50 from world
        Assert.Equal(50, depot.LoamSpentFromWorld);
        Assert.Equal(50, depot.Loam);
    }

    [Fact]
    public void Board_income_is_spent_before_world_stock()
    {
        var depot = SiegeDepot.SeedFromSectorStock(sectorLoam: 100, sectorIronwork: 0, sectorRubble: 0, depotSeedMilli: 1000);
        depot = depot.CreditLoam(40);
        depot = depot.SpendLoam(40); // exactly the board-earned amount
        Assert.Equal(0, depot.LoamSpentFromWorld);
        Assert.Equal(100, depot.Loam); // world-seeded portion untouched
    }

    [Fact]
    public void Spending_more_than_the_balance_is_rejected()
    {
        var depot = SiegeDepot.SeedFromCarriedLoam(10);
        Assert.Throws<InvalidOperationException>(() => depot.SpendLoam(11));
    }

    [Fact]
    public void Depot_seed_milli_scales_the_defenders_reachable_stock()
    {
        var depot = SiegeDepot.SeedFromSectorStock(sectorLoam: 1000, sectorIronwork: 1000, sectorRubble: 1000, depotSeedMilli: 250);
        Assert.Equal(250, depot.Loam);
        Assert.Equal(250, depot.Ironwork);
        Assert.Equal(250, depot.Rubble);
    }

    [Fact]
    public void Defender_budget_seeds_from_the_sectors_own_stock()
    {
        var sector = new WorldSector { SectorId = "s1", LoamStock = 400, IronworkStock = 60, RubbleStock = 25 };
        var depot = SiegeDepot.SeedFromSectorStock(sector.LoamStock, sector.IronworkStock, sector.RubbleStock, depotSeedMilli: 1000);
        Assert.Equal(400, depot.Loam);
        Assert.Equal(60, depot.Ironwork);
        Assert.Equal(25, depot.Rubble);
    }

    [Fact]
    public void Attacker_budget_seeds_from_carried_loam_and_is_finite()
    {
        // Decision 27's whole reason for four acquisition paths: an attacker has no empire stockpile.
        var depot = SiegeDepot.SeedFromCarriedLoam(carriedLoam: 250);
        Assert.Equal(250, depot.Loam);
        Assert.Equal(0, depot.Ironwork);
    }

    [Fact]
    public void Board_logic_never_reads_slot_owner_faction()
    {
        // Source scan, per the spec's own §2b acceptance test — literal, not just argued.
        var text = System.IO.File.ReadAllText(FindSourceFile("BoardEconomy.cs"));
        Assert.DoesNotContain("OwnerFactionId", text, StringComparison.Ordinal);
    }

    static string FindSourceFile(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir != null; i++)
        {
            var candidate = System.IO.Directory.GetFiles(dir, fileName, new System.IO.EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System,
            });
            if (candidate.Length > 0) return candidate[0];
            dir = System.IO.Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException($"{fileName} not found by walking up from {AppContext.BaseDirectory}");
    }

    // -- F11: capture transfers the stockpile --

    [Fact]
    public void Capture_transfers_proportionally_to_surviving_hp()
    {
        var recovered = SiegeDepot.RecoveredOnCapture(stored: 1000, structureHp: 500, maxHp: 1000, captureRecoveryMilli: 1000);
        Assert.Equal(500, recovered);
    }

    [Fact]
    public void Destroying_storage_destroys_the_stores()
    {
        var recovered = SiegeDepot.RecoveredOnCapture(stored: 1000, structureHp: 0, maxHp: 1000, captureRecoveryMilli: 1000);
        Assert.Equal(0, recovered);
    }

    [Fact]
    public void Capture_from_an_indestructible_structure_does_not_divide_by_zero()
    {
        // MaxHp == 0 is a legal, shipped value on all four existing structure rows.
        var recovered = SiegeDepot.RecoveredOnCapture(stored: 1000, structureHp: 0, maxHp: 0, captureRecoveryMilli: 1000);
        Assert.Equal(1000, recovered); // indestructible: HP concept does not apply, full amount recovered
    }

    [Fact]
    public void Capture_recovery_milli_scales_on_top_of_the_hp_proportion()
    {
        var recovered = SiegeDepot.RecoveredOnCapture(stored: 1000, structureHp: 1000, maxHp: 1000, captureRecoveryMilli: 500);
        Assert.Equal(500, recovered);
    }

    [Fact]
    public void Transfer_overflows_loudly()
    {
        Assert.Throws<OverflowException>(() =>
            SiegeDepot.RecoveredOnCapture(stored: long.MaxValue, structureHp: long.MaxValue, maxHp: 1, captureRecoveryMilli: 1000));
    }

    [Fact]
    public void Recovered_credits_into_the_captors_depot()
    {
        var captor = SiegeDepot.SeedFromCarriedLoam(0);
        var recovered = SiegeDepot.RecoveredOnCapture(stored: 400, structureHp: 400, maxHp: 400, captureRecoveryMilli: 1000);
        captor = captor.CreditIronwork(recovered);
        Assert.Equal(400, captor.Ironwork);
    }

    // -- Rubble (2026-09-06): `SiegeDepot` had never tracked this stock at all, despite `Built`'s own
    // ConstructRubbleCost existing on every StructureDef — same shape as Loam/Ironwork throughout.

    [Fact]
    public void Rubble_credits_and_spends_board_income_before_world_seed()
    {
        var depot = SiegeDepot.SeedFromSectorStock(sectorLoam: 0, sectorIronwork: 0, sectorRubble: 100, depotSeedMilli: 1000);
        depot = depot.CreditRubble(30); // total balance 130, 100 world-seeded + 30 board-earned
        depot = depot.SpendRubble(30); // exactly the board-earned amount
        Assert.Equal(0, depot.RubbleSpentFromWorld);
        Assert.Equal(100, depot.Rubble); // world-seeded portion untouched
    }

    [Fact]
    public void Rubble_spend_past_board_income_crosses_into_world_seed()
    {
        var depot = SiegeDepot.SeedFromSectorStock(sectorLoam: 0, sectorIronwork: 0, sectorRubble: 100, depotSeedMilli: 1000);
        depot = depot.CreditRubble(30);
        depot = depot.SpendRubble(80); // 30 from board, 50 from world
        Assert.Equal(50, depot.RubbleSpentFromWorld);
        Assert.Equal(50, depot.Rubble);
    }

    [Fact]
    public void Rubble_spend_past_the_balance_is_rejected()
    {
        var depot = SiegeDepot.SeedFromSectorStock(sectorLoam: 0, sectorIronwork: 0, sectorRubble: 10, depotSeedMilli: 1000);
        Assert.Throws<InvalidOperationException>(() => depot.SpendRubble(11));
    }

    [Fact]
    public void Rubble_never_reaches_the_attackers_carried_loam_seed()
    {
        var depot = SiegeDepot.SeedFromCarriedLoam(500);
        Assert.Equal(0, depot.Rubble);
    }

    [Fact]
    public void Recovered_credits_rubble_into_the_captors_depot()
    {
        var captor = SiegeDepot.SeedFromCarriedLoam(0);
        var recovered = SiegeDepot.RecoveredOnCapture(stored: 200, structureHp: 200, maxHp: 200, captureRecoveryMilli: 1000);
        captor = captor.CreditRubble(recovered);
        Assert.Equal(200, captor.Rubble);
    }
}
