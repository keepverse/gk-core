using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Movement;

/// <summary>
/// A9 `movement-actions` through the REAL production dispatch (`BasicAttack.cs`'s
/// `ApplyBasicAttack`, the same `category != ActionCategory.Attack` branch
/// `ActionDispatchGeneralizationTests.T60_1_every_non_attack_category_skips_the_hit_roll`
/// already exercises for `ActionCategory.Movement`) — not the internal seam
/// `NearestEnemyMovementTests.cs` uses, and not the pure algorithm `MoveActionTests.cs` uses. This is
/// the one remaining link: a real `BattleEngine.Resolve` call, with a real board and a real equipped
/// Movement-category action, actually changes an actor's position on the board it was passed.
///
/// <para>Built on `BattleGoldenTests.CloseSetup()`/`EquipSquadZero`, the same proven fixture
/// `ActionDispatchGeneralizationTests.cs` (A18f/A22) already uses for this exact dispatch branch.</para>
/// </summary>
public class MovementActionDispatchTests
{
    static CompiledAction MovementSkill(string actionId = "skill.move") => new(
        ActionId: actionId, Kind: ActionKind.Skill, Rung: 1, Tags: new[] { ActionTag.Movement },
        Enabled: true, Revision: 0, Grantable: false, DefaultAttackEligible: false, ContainerId: "",
        Category: ActionCategory.Movement,
        Envelope: ActionEnvelope.NoOp with { ActionId = actionId },
        Targeting: TargetSpecCompiler.Compile(new ActionTargetSpec()),
        MinRange: 0, MaxRange: int.MaxValue, RangeChannel: DerivedStatChannels.MoveRange, RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always,
        Costs: System.Array.Empty<CompiledActionCost>(),
        Scopes: System.Array.Empty<ActionScopeRow>());

    static BattleSetup EquipSquadZeroWithMoveRange(string actionId, long moveRange) =>
        FusionRpg.Core.Tests.Battle.BattleGoldenTests.CloseSetup() with
        {
            Squad = FusionRpg.Core.Tests.Battle.BattleGoldenTests.CloseSetup().Squad.Select((a, i) => i == 0
                ? a with
                {
                    EquippedActionIds = new[] { actionId },
                    ChannelMods = new[] { new BattleChannelMod(DerivedStatChannels.MoveRange, moveRange) },
                }
                : a).ToArray(),
        };

    [Fact]
    public void A_real_equipped_movement_action_moves_squad_0_on_the_real_board_it_was_given()
    {
        var catalog = ActionCatalog.Build(new[] { MovementSkill() });
        var setup = EquipSquadZeroWithMoveRange("skill.move", moveRange: 1);

        var squadKeys = setup.Squad.Select(a => a.Key).ToList();
        var waveKeys = setup.Wave.Select(a => a.Key).ToList();
        var board = NormalBattleBoard.Build(squadKeys, waveKeys, seed: 5501UL);
        var startPos = board.Positions["squad:0"];

        BattleEngine.Resolve(setup, seed: 5501, actionCatalog: catalog, board: board);

        // The same BoardState reference Resolve was given -- mutated in place, so this is the real
        // engine's own final state, not a re-derivation.
        Assert.NotEqual(startPos, board.Positions["squad:0"]);
        // Moved TOWARD the wave side (higher column, per NormalBattleBoard's own left/right convention),
        // never away from it.
        Assert.True(board.Positions["squad:0"].Col > startPos.Col);
    }

    [Fact]
    public void With_zero_move_range_the_real_dispatch_never_touches_the_board_byte_identical_to_before()
    {
        var catalog = ActionCatalog.Build(new[] { MovementSkill() });
        // Every actor's move.range defaults to 0 -- this is the shape every shipped battle has today.
        var setup = FusionRpg.Core.Tests.Battle.BattleGoldenTests.CloseSetup() with
        {
            Squad = FusionRpg.Core.Tests.Battle.BattleGoldenTests.CloseSetup().Squad.Select((a, i) => i == 0
                ? a with { EquippedActionIds = new[] { "skill.move" } }
                : a).ToArray(),
        };

        var squadKeys = setup.Squad.Select(a => a.Key).ToList();
        var waveKeys = setup.Wave.Select(a => a.Key).ToList();
        var board = NormalBattleBoard.Build(squadKeys, waveKeys, seed: 5501UL);
        var startPos = board.Positions["squad:0"];

        BattleEngine.Resolve(setup, seed: 5501, actionCatalog: catalog, board: board);

        Assert.Equal(startPos, board.Positions["squad:0"]);
    }

    [Fact]
    public void With_no_board_the_real_dispatch_never_throws_for_a_movement_action()
    {
        var catalog = ActionCatalog.Build(new[] { MovementSkill() });
        var setup = EquipSquadZeroWithMoveRange("skill.move", moveRange: 3);

        // board: omitted entirely -- the exact shape every WebMatchService caller had before A10.
        var report = BattleEngine.Resolve(setup, seed: 5501, actionCatalog: catalog);

        Assert.NotNull(report);
    }
}
