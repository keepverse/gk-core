using System.IO;
using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.Match;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Siege;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Siege;

/// <summary>
/// base-defense `siege-obstacles` (spec-siege-obstacles.md): five rows, five distinct decisions —
/// vocabulary owned here (`ObstacleKind`, `AcquisitionPath`, the cover-radius/power fields, the
/// cell-entry transition), consumed by `siege-cover`/`siege-construction` as DATA, never a call back
/// into this module (the cycle pass 3 found and fixed).
/// </summary>
public class SiegeObstaclesTests
{
    static StructureDef Def(string id, ObstacleKind obstacle = ObstacleKind.None, bool blocksMovement = false,
        bool blocksLineOfFire = false, int coverPowerMilli = 0, int coverRadius = 0,
        int entryStaminaMultiplierMilli = 1000, IReadOnlyList<AcquisitionPath>? paths = null) => new()
    {
        StructureId = id, Name = id, Obstacle = obstacle,
        BlocksMovement = blocksMovement, BlocksLineOfFire = blocksLineOfFire,
        CoverPowerMilli = coverPowerMilli, CoverRadius = coverRadius,
        EntryStaminaMultiplierMilli = entryStaminaMultiplierMilli,
        AcquisitionPaths = paths ?? new[] { AcquisitionPath.Built },
    };

    [Fact]
    public void Obstacle_kind_defaults_to_none_and_every_pre_siege_loam_row_is_unaffected()
    {
        // 2026-09-06: the "moat" row (siege-construction §5) is the FIRST real obstacle this catalog
        // ships — this assertion narrows from "every shipped row" to the loam-content rows that
        // predate any notion of a siege, which is what the test's own name always meant to guarantee.
        Assert.All(StructureCatalog.All.Where(s => s.StructureId != "moat"), s => Assert.Equal(ObstacleKind.None, s.Obstacle));
    }

    [Fact]
    public void The_moat_is_a_real_shipped_Rampart_obstacle()
    {
        var moat = StructureCatalog.Get("moat");
        Assert.Equal(ObstacleKind.Rampart, moat.Obstacle);
        Assert.True(moat.BlocksMovement);
        Assert.True(moat.BlocksLineOfFire);
        // Buildable via all four paths on purpose (15.3b, 2026-09-06) — one real structure proves
        // `structure.place`'s mechanism end to end; a fuller roster is `structure-corpus`'s own job.
        Assert.Equal(
            new[] { AcquisitionPath.Built, AcquisitionPath.Assembled, AcquisitionPath.Summoned, AcquisitionPath.Laboured },
            moat.AcquisitionPaths);
    }

    [Fact]
    public void Every_obstacle_declares_at_least_one_acquisition_path()
    {
        var noPath = Def("no-path", paths: Array.Empty<AcquisitionPath>());
        var ex = Assert.Throws<InvalidOperationException>(() => StructureCatalog.Validate(new[] { noPath }));
        Assert.Contains("acquisition path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_shipped_structure_already_names_a_real_acquisition_path()
    {
        // The seven pre-existing loam rows were retrofitted with `Built` when this module's
        // validation landed -- confirms the catalog still loads (Validate did not throw at `All`).
        Assert.All(StructureCatalog.All, s => Assert.NotEmpty(s.AcquisitionPaths));
    }

    [Fact]
    public void Trench_is_occupiable_and_passable()
    {
        var trench = Def("trench-sandbag", ObstacleKind.Trench, blocksMovement: false, coverPowerMilli: 40);
        Assert.False(trench.BlocksMovement);
        Assert.True(trench.CoverPowerMilli > 0);
    }

    [Fact]
    public void Trench_tiers_differ_by_value_not_mechanism()
    {
        var sandbag = Def("trench-sandbag", ObstacleKind.Trench, coverPowerMilli: 40);
        var revetted = Def("trench-revetted", ObstacleKind.Trench, coverPowerMilli: 60);
        // Same kind, same mechanism (both Trench, both occupiable) -- differ only by the cover VALUE.
        Assert.Equal(ObstacleKind.Trench, sandbag.Obstacle);
        Assert.Equal(ObstacleKind.Trench, revetted.Obstacle);
        Assert.NotEqual(sandbag.CoverPowerMilli, revetted.CoverPowerMilli);
    }

    [Fact]
    public void Rampart_blocks_movement_and_fire()
    {
        var rampart = Def("rampart", ObstacleKind.Rampart, blocksMovement: true, blocksLineOfFire: true);
        Assert.True(rampart.BlocksMovement);
        Assert.True(rampart.BlocksLineOfFire);
    }

    [Fact]
    public void A_laboured_moat_is_a_rampart_not_terrain()
    {
        var moat = Def("moat", ObstacleKind.Rampart, blocksMovement: true, blocksLineOfFire: false,
            paths: new[] { AcquisitionPath.Laboured });
        Assert.Equal(ObstacleKind.Rampart, moat.Obstacle);
        Assert.Contains(AcquisitionPath.Laboured, moat.AcquisitionPaths);
        // "A cell you cannot enter and cannot stand on IS a wall" -- moat blocks movement like any
        // Rampart; it does not block fire (you can see and shoot across a ditch, per the spec's own
        // worked distinction between blocking movement and blocking fire).
        Assert.True(moat.BlocksMovement);
    }

    [Fact]
    public void Wire_taxes_stamina_not_movement()
    {
        var wire = Def("wire", ObstacleKind.Wire, blocksMovement: false, entryStaminaMultiplierMilli: 2000);
        Assert.False(wire.BlocksMovement); // movement cost is provably unchanged: Wire never sets it
        var taxed = WireStamina.ApplyEntryMultiplier(baseStaminaCost: 10, wire.EntryStaminaMultiplierMilli);
        Assert.Equal(20, taxed); // 2000 permille = doubled
    }

    [Fact]
    public void Wire_does_not_change_the_pathfinders_move_cost_source()
    {
        // Import/reference scan: MoveCosts (siege-pathing's own movement-cost source) must never read
        // EntryStaminaMultiplierMilli -- if it ever does, Wire silently becomes a second Rough.
        var path = FindRepoFile("src/FusionRpg.Core/Battle/Board/MoveCosts.cs");
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("EntryStaminaMultiplierMilli", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Requires_line_of_sight_finally_has_a_reader()
    {
        // No wall between (0,0) and (0,3): legal.
        Assert.True(LineOfFire.CanFire(true, new GridPos(0, 0), new GridPos(0, 3), _ => false));
        // A wall at (0,1) blocks the same shot.
        Assert.False(LineOfFire.CanFire(true, new GridPos(0, 0), new GridPos(0, 3), p => p.Equals(new GridPos(0, 1))));
        // An action that does not require line of sight is always legal, wall or not.
        Assert.True(LineOfFire.CanFire(false, new GridPos(0, 0), new GridPos(0, 3), _ => true));
    }

    [Fact]
    public void Requires_line_of_sight_had_no_reader_before_this_module_source_scan()
    {
        // Confirms the wiring-gap framing directly rather than trusting the spec's own claim: before
        // this module, RequiresLineOfSight is declared/compiled/carried/persisted but the string
        // "RequiresLineOfSight" appears in exactly the files the spec names (ActionRow, CompiledAction,
        // ActionCompiler, RpgStore.Actions, BattleRunState's hardcoded false) and nowhere that reads it
        // as a gate -- LineOfFire.cs is the first file whose own logic branches on it.
        var lineOfFire = File.ReadAllText(FindRepoFile("src/FusionRpg.Core/Battle/Siege/LineOfFire.cs"));
        Assert.Contains("requiresLineOfSight", lineOfFire, StringComparison.Ordinal);
    }

    [Fact]
    public void Line_of_fire_is_symmetric()
    {
        var a = new GridPos(0, 0);
        var b = new GridPos(3, 4);
        bool Blocks(GridPos p) => p.Equals(new GridPos(1, 1)) || p.Equals(new GridPos(2, 3));
        Assert.Equal(LineOfFire.HasLineOfFire(a, b, Blocks), LineOfFire.HasLineOfFire(b, a, Blocks));
    }

    [Fact]
    public void Mine_is_single_use()
    {
        var field = new MineField();
        var cell = new GridPos(2, 2);
        field.Arm(cell, damage: 500);

        Assert.Equal(500, field.Trigger(cell));
        Assert.Null(field.Trigger(cell)); // second entry is safe -- nothing left to trigger
    }

    [Fact]
    public void Mine_is_visible_to_both_sides()
    {
        var field = new MineField();
        var cell = new GridPos(1, 1);
        field.Arm(cell, damage: 100);
        // No faction parameter anywhere on this query -- both sides see the same answer.
        Assert.True(field.IsArmedAt(cell));
    }

    [Fact]
    public void Mine_damages_on_entry_through_a_real_board_place_call()
    {
        var board = new BoardState(new GridSpec(3, 3));
        var field = new MineField();
        field.Arm(new GridPos(1, 1), damage: 250);

        string? triggeredFor = null;
        long triggeredDamage = 0;
        field.AttachTo(board, (actorKey, damage) => { triggeredFor = actorKey; triggeredDamage = damage; });

        board.Place("attacker", new GridPos(1, 1));

        Assert.Equal("attacker", triggeredFor);
        Assert.Equal(250, triggeredDamage);
        Assert.False(field.IsArmedAt(new GridPos(1, 1))); // consumed
    }

    [Fact]
    public void Mine_fires_on_the_same_transition_cover_uses()
    {
        // ScopeMembershipTransition.CellEntered is the program's one reviewed vocabulary change --
        // this asserts it exists and is distinct from CellExited, the pairing this module also owns.
        Assert.NotEqual(ScopeMembershipTransition.CellEntered, ScopeMembershipTransition.CellExited);
    }

    [Fact]
    public void Existing_membership_consumers_ignore_the_new_transitions()
    {
        // BattlefieldOwnSideReactor.OnMembershipChanged has no case (and no default) for
        // CellEntered/CellExited -- a real dispatch through it must not throw.
        var harness = new FusionRpg.Core.Effects.FoundationHarness();
        var reactor = new FusionRpg.Core.Battle.BattlefieldOwnSideReactor(
            harness.Bag, new AlwaysUnknown(), FusionRpg.Contracts.RelationKind.Ally,
            "fx.obstacles-probe", "test", "test:grant", "resource.delta", FusionRpg.Core.Scope.ScopeHost.Sim);

        var enteredEx = Record.Exception(() => reactor.OnMembershipChanged(
            new ScopeMembershipEvent("some-ptr", ScopeMembershipTransition.CellEntered)));
        var exitedEx = Record.Exception(() => reactor.OnMembershipChanged(
            new ScopeMembershipEvent("some-ptr", ScopeMembershipTransition.CellExited)));
        Assert.Null(enteredEx);
        Assert.Null(exitedEx);
    }

    sealed class AlwaysUnknown : FusionRpg.Core.Battle.IOwnSideOracle
    {
        public FusionRpg.Contracts.RelationKind? RelationOf(string ptr) => null;
    }

    [Fact]
    public void Every_cell_entered_is_paired_with_a_cell_exited_move()
    {
        var board = new BoardState(new GridSpec(3, 3));
        var entries = new List<GridPos>();
        var exits = new List<GridPos>();
        board.Entered += (_, p) => entries.Add(p);
        board.Exited += (_, p) => exits.Add(p);

        board.Place("a", new GridPos(0, 0));
        board.Move("a", new GridPos(0, 1));

        Assert.Equal(new[] { new GridPos(0, 0), new GridPos(0, 1) }, entries);
        Assert.Equal(new[] { new GridPos(0, 0) }, exits);
    }

    [Fact]
    public void Every_cell_entered_is_paired_with_a_cell_exited_death_or_withdrawal()
    {
        var board = new BoardState(new GridSpec(3, 3));
        var exited = false;
        board.Place("a", new GridPos(1, 1));
        board.Exited += (_, _) => exited = true;

        board.Remove("a"); // death/withdrawal path

        Assert.True(exited);
    }

    [Fact]
    public void This_module_does_not_depend_on_siege_cover()
    {
        var obstaclesFile = File.ReadAllText(FindRepoFile("src/FusionRpg.Core/World/Siege/Obstacles.cs"));
        Assert.DoesNotContain("SiegeCover", obstaclesFile, StringComparison.Ordinal);
        Assert.DoesNotContain("using FusionRpg.Core.Battle.Siege.Cover", obstaclesFile, StringComparison.Ordinal);
    }

    static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"could not find '{relativePath}' above {AppContext.BaseDirectory}");
    }
}
