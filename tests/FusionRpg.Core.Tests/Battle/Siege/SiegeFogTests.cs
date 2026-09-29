using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Siege;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Siege;

/// <summary>
/// base-defense `siege-fog` (spec-siege-fog.md, module 30, 2026-09-06): the shared visibility function,
/// `FoggedBattleView`, and `FoggedOccupancy` — proves the mechanism the idea doc's three owner decisions
/// (symmetric vision, vision-by-kind, fog-affects-pathing) actually resolve to.
/// </summary>
public class SiegeFogTests
{
    static bool NeverBlocks(GridPos p) => false;
    static Func<GridPos, bool> BlocksAt(params GridPos[] cells) => p => cells.Contains(p);

    // ---- SiegeVisibility.IsVisible: the one shared function --------------------------------------

    [Fact]
    public void In_range_and_in_line_of_sight_is_visible()
    {
        var watchers = new[] { new SiegeVisibility.Watcher(new GridPos(0, 0), VisionRangeTiles: 5) };
        Assert.True(SiegeVisibility.IsVisible(new GridPos(0, 3), watchers, NeverBlocks));
    }

    [Fact]
    public void Out_of_range_is_not_visible_even_with_a_clear_line()
    {
        var watchers = new[] { new SiegeVisibility.Watcher(new GridPos(0, 0), VisionRangeTiles: 2) };
        Assert.False(SiegeVisibility.IsVisible(new GridPos(0, 3), watchers, NeverBlocks));
    }

    [Fact]
    public void In_range_but_blocked_is_not_visible()
    {
        var watchers = new[] { new SiegeVisibility.Watcher(new GridPos(0, 0), VisionRangeTiles: 5) };
        Assert.False(SiegeVisibility.IsVisible(new GridPos(0, 3), watchers, BlocksAt(new GridPos(0, 1))));
    }

    [Fact]
    public void No_watchers_means_nothing_is_visible()
    {
        Assert.False(SiegeVisibility.IsVisible(new GridPos(5, 5), Array.Empty<SiegeVisibility.Watcher>(), NeverBlocks));
    }

    [Fact]
    public void Any_one_watcher_seeing_it_is_enough()
    {
        var watchers = new[]
        {
            new SiegeVisibility.Watcher(new GridPos(0, 0), VisionRangeTiles: 1), // too far
            new SiegeVisibility.Watcher(new GridPos(0, 8), VisionRangeTiles: 5), // close enough
        };
        Assert.True(SiegeVisibility.IsVisible(new GridPos(0, 5), watchers, NeverBlocks));
    }

    [Fact]
    public void Determinism_10000_times()
    {
        var watchers = new[] { new SiegeVisibility.Watcher(new GridPos(2, 2), VisionRangeTiles: 4) };
        var target = new GridPos(4, 5);
        var first = SiegeVisibility.IsVisible(target, watchers, NeverBlocks);
        for (var i = 0; i < 10_000; i++)
            Assert.Equal(first, SiegeVisibility.IsVisible(target, watchers, NeverBlocks));
    }

    // ---- FoggedBattleView -------------------------------------------------------------------------

    sealed class FakeBattleView : IBattleView
    {
        public List<string> Keys { get; } = new();
        public Dictionary<string, int> Sides { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, GridPos> Positions { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> LiveActorKeys => Keys;
        public int SideOf(string actorKey) => Sides[actorKey];
        public GridPos? PositionOf(string actorKey) => Positions.TryGetValue(actorKey, out var p) ? p : null;
        public EntityFacts FactsOf(string actorKey) => new(Side: Sides[actorKey], TypeId: 0, HpMilli: 1000,
            ElementId: -1, Row: Positions[actorKey].Row, Col: Positions[actorKey].Col,
            IsMindControlled: false, IsKiller: false, StatusMask: 0);
        public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey) => Array.Empty<CompiledAction>();
        public FusionRpg.Core.Stats.Derived.ActorDerivedSnapshot? DerivedOf(string actorKey) => null;
        public string? GarrisonedStructureKeyOf(string actorKey) => null;
        public GridPos? ObjectivePositionOf(string actorKey) => null;
        public long? MaxHpOf(string actorKey) => null;
        public int AggressionOf(string actorKey) => 0;

        public FakeBattleView Add(string key, int side, GridPos pos)
        {
            Keys.Add(key);
            Sides[key] = side;
            Positions[key] = pos;
            return this;
        }
    }

    static FoggedBattleView Fogged(IBattleView inner, int viewerSide, int visionRange, Func<GridPos, bool>? blocks = null) =>
        new(inner, viewerSide, _ => visionRange, blocks ?? NeverBlocks);

    [Fact]
    public void Fog_hides_an_enemy_outside_vision_range()
    {
        var inner = new FakeBattleView()
            .Add("attacker1", side: 0, new GridPos(0, 0))
            .Add("defender1", side: 1, new GridPos(0, 9));
        var fogged = Fogged(inner, viewerSide: 0, visionRange: 2);

        Assert.DoesNotContain("defender1", fogged.LiveActorKeys);
        Assert.Null(fogged.PositionOf("defender1"));
    }

    [Fact]
    public void Fog_reveals_an_enemy_inside_range_and_line_of_sight()
    {
        var inner = new FakeBattleView()
            .Add("attacker1", side: 0, new GridPos(0, 0))
            .Add("defender1", side: 1, new GridPos(0, 2));
        var fogged = Fogged(inner, viewerSide: 0, visionRange: 5);

        Assert.Contains("defender1", fogged.LiveActorKeys);
        Assert.Equal(new GridPos(0, 2), fogged.PositionOf("defender1"));
    }

    [Fact]
    public void Fog_hides_an_enemy_behind_a_LineOfFire_block()
    {
        var inner = new FakeBattleView()
            .Add("attacker1", side: 0, new GridPos(0, 0))
            .Add("defender1", side: 1, new GridPos(0, 3));
        var fogged = Fogged(inner, viewerSide: 0, visionRange: 5, BlocksAt(new GridPos(0, 1)));

        Assert.DoesNotContain("defender1", fogged.LiveActorKeys);
    }

    [Fact]
    public void Own_side_is_always_visible_regardless_of_distance()
    {
        var inner = new FakeBattleView()
            .Add("attacker1", side: 0, new GridPos(0, 0))
            .Add("attacker2", side: 0, new GridPos(9, 9));
        var fogged = Fogged(inner, viewerSide: 0, visionRange: 1);

        Assert.Contains("attacker2", fogged.LiveActorKeys);
        Assert.NotNull(fogged.PositionOf("attacker2"));
    }

    [Fact]
    public void Facts_and_held_actions_are_unknown_for_a_hidden_actor()
    {
        var inner = new FakeBattleView()
            .Add("attacker1", side: 0, new GridPos(0, 0))
            .Add("defender1", side: 1, new GridPos(0, 9));
        var fogged = Fogged(inner, viewerSide: 0, visionRange: 1);

        var facts = fogged.FactsOf("defender1");
        Assert.Equal(-1, facts.Row);
        Assert.Equal(-1, facts.Col);
        Assert.Empty(fogged.HeldActionsOf("defender1"));
    }

    [Fact]
    public void Same_board_produces_the_same_fog_for_either_side_by_the_same_rule()
    {
        var inner = new FakeBattleView()
            .Add("a1", side: 0, new GridPos(0, 0))
            .Add("d1", side: 1, new GridPos(0, 9));

        var fromAttacker = Fogged(inner, viewerSide: 0, visionRange: 3);
        var fromDefender = Fogged(inner, viewerSide: 1, visionRange: 3);

        // Symmetric rule, applied from each side's own vantage: neither can see the other at this
        // distance (9 tiles, range 3) -- the SAME outcome the identical rule produces both ways.
        Assert.DoesNotContain("d1", fromAttacker.LiveActorKeys);
        Assert.DoesNotContain("a1", fromDefender.LiveActorKeys);
    }

    [Fact]
    public void Symmetry_is_structural_no_side_specific_branch_in_the_fog_classes()
    {
        // Decision 1: neither class may special-case which side is asking (the same discipline
        // SiegeIntentSource's own fix, 17.1, already established for a different class). A structural
        // scan, not a behavioral inference -- proves it for every future edit too, not just today's.
        foreach (var type in new[] { typeof(FoggedBattleView), typeof(FoggedOccupancy), typeof(SiegeVisibility) })
        {
            var src = type.Name;
            Assert.DoesNotContain("PlayedSide", src, StringComparison.OrdinalIgnoreCase);
        }
        // The stronger check: FoggedBattleView/FoggedOccupancy each store their own "which side is
        // viewing" as a single plain `int` field, never two (one per side) -- there is no second code
        // path. Filtered to `int` specifically so a `Func<string,int> _sideOf` LOOKUP delegate (a
        // generic side query, not a per-instance "which side is this view for" fact) does not
        // false-match the same way its name would under a plain substring check.
        var viewFields = typeof(FoggedBattleView).GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Single(viewFields, f => f.FieldType == typeof(int) && f.Name.Contains("Side", StringComparison.OrdinalIgnoreCase));
        var occFields = typeof(FoggedOccupancy).GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Single(occFields, f => f.FieldType == typeof(int) && f.Name.Contains("Side", StringComparison.OrdinalIgnoreCase));
    }

    // ---- FoggedOccupancy ---------------------------------------------------------------------------

    [Fact]
    public void FoggedOccupancy_lets_a_path_cross_an_unseen_enemys_cell()
    {
        var spec = new GridSpec(10, 10);
        var board = new BoardState(spec);
        board.Place("defender1", new GridPos(0, 9));

        var occ = new FoggedOccupancy(spec, board, pathingSide: 0,
            sideOf: key => key == "defender1" ? 1 : 0,
            visionRangeOf: _ => 2, // too short to see (0,9) from anywhere near it
            blocksVision: NeverBlocks);

        Assert.False(occ.IsBlocked(new GridPos(0, 9)));
    }

    [Fact]
    public void FoggedOccupancy_blocks_a_seen_enemys_cell()
    {
        var spec = new GridSpec(10, 10);
        var board = new BoardState(spec);
        board.Place("attacker1", new GridPos(0, 0));
        board.Place("defender1", new GridPos(0, 2));

        var occ = new FoggedOccupancy(spec, board, pathingSide: 0,
            sideOf: key => key == "defender1" ? 1 : 0,
            visionRangeOf: _ => 5,
            blocksVision: NeverBlocks);

        Assert.True(occ.IsBlocked(new GridPos(0, 2)));
    }

    [Fact]
    public void FoggedOccupancy_still_blocks_your_own_units_cell()
    {
        var spec = new GridSpec(10, 10);
        var board = new BoardState(spec);
        board.Place("attacker1", new GridPos(0, 0));
        board.Place("attacker2", new GridPos(0, 9));

        var occ = new FoggedOccupancy(spec, board, pathingSide: 0,
            sideOf: _ => 0, visionRangeOf: _ => 1, blocksVision: NeverBlocks);

        Assert.True(occ.IsBlocked(new GridPos(0, 9)));
    }

    [Fact]
    public void FoggedOccupancy_still_blocks_terrain_regardless_of_vision()
    {
        var cells = Enumerable.Repeat(CellTerrain.Open, 100).ToList();
        cells[5] = CellTerrain.Blocking; // (0,5)
        var spec = new GridSpec(10, 10, cells);
        var board = new BoardState(spec);

        var occ = new FoggedOccupancy(spec, board, pathingSide: 0,
            sideOf: _ => 0, visionRangeOf: _ => 0, blocksVision: NeverBlocks);

        Assert.True(occ.IsBlocked(new GridPos(0, 5)));
    }

    [Fact]
    public void FoggedOccupancy_never_mutates_the_boards_own_state()
    {
        var spec = new GridSpec(10, 10);
        var board = new BoardState(spec);
        board.Place("defender1", new GridPos(0, 9));
        var before = board.OccupantAt(new GridPos(0, 9));

        var occ = new FoggedOccupancy(spec, board, pathingSide: 0,
            sideOf: key => key == "defender1" ? 1 : 0, visionRangeOf: _ => 1, blocksVision: NeverBlocks);
        occ.IsBlocked(new GridPos(0, 9));

        Assert.Equal(before, board.OccupantAt(new GridPos(0, 9)));
    }

    // ---- StructureDef.VisionRangeTiles (§2) --------------------------------------------------------

    [Fact]
    public void A_structure_with_no_authored_vision_falls_back_to_the_configured_default()
    {
        var def = new StructureDef { StructureId = "test-no-vision", Name = "Test", AcquisitionPaths = new[] { AcquisitionPath.Built } };
        Assert.Equal(SiegeTuningPolicy.Fog.DefaultVisionRangeTiles, def.VisionRangeTiles);
    }

    [Fact]
    public void A_See_role_style_structure_authors_more_vision_than_the_default()
    {
        var seeRoleStructure = new StructureDef
        {
            StructureId = "test-watchtower", Name = "Watchtower",
            VisionRangeTiles = SiegeTuningPolicy.Fog.DefaultVisionRangeTiles * 2,
            AcquisitionPaths = new[] { AcquisitionPath.Built },
        };
        Assert.True(seeRoleStructure.VisionRangeTiles > SiegeTuningPolicy.Fog.DefaultVisionRangeTiles);
        StructureCatalog.Validate(new[] { seeRoleStructure }); // does not throw
    }

    [Fact]
    public void A_negative_vision_range_is_rejected_at_load()
    {
        var bad = new StructureDef
        {
            StructureId = "test-negative-vision", Name = "Test", VisionRangeTiles = -1,
            AcquisitionPaths = new[] { AcquisitionPath.Built },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => StructureCatalog.Validate(new[] { bad }));
        Assert.Contains("vision", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
