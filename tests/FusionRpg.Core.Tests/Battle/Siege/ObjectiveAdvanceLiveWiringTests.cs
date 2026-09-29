using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World.District;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Siege;

/// <summary>
/// base-defense `siege-ai` R3 (spec-siege-ai.md §4, 2026-09-07, session 5): the FIRST live wiring of
/// "no target in reach -> path toward the objective" through a real `BattleEngine.Resolve` round,
/// proving `BasicAttack.TryDeclareObjectiveAdvance`'s own new branch end to end — the same shape
/// `ConstructionLiveWiringTests.cs` already proved for 15.3d's `TryDeclareBuilt`.
///
/// <para><b>Why a custom `IIntentSource` rather than the default `StubIntentSource`</b>: `StubIntentSource`
/// always names SOME live enemy as `TargetKey` when any exists (nearest by position, or `SourceOrder`
/// with no board) and step 4's own existing "move toward the chosen target" bypasses range entirely —
/// so with a live wave actor on the board, `StubIntentSource` never actually reaches
/// `ActionIntent.None` for a Movement-holding actor; it just walks toward the enemy instead of its own
/// objective (proving A9's OWN dispatch, not R3's). Killing the wave actor first does not work either —
/// `BattleEngine.Resolve` throws on `MaxHp &lt; 1` (`BattleEngine.cs:211`) so it cannot start dead, and
/// the round loop ends the battle the moment one side is wiped, before the mover gets a further turn to
/// prove it. <see cref="AlwaysNoneIntentSource"/> instead simulates "no target in reach" directly and
/// deterministically — exactly the one precondition `TryDeclareObjectiveAdvance` needs — without
/// fighting unrelated default-AI targeting behavior to manufacture it indirectly.</para>
/// </summary>
public class ObjectiveAdvanceLiveWiringTests
{
    /// <summary>Every actor, every tick, has no legal action — the direct simulation of R3's own
    /// literal spec condition, "no target in reach," independent of what `StubIntentSource`'s own
    /// unrelated nearest-enemy targeting would otherwise decide.</summary>
    sealed class AlwaysNoneIntentSource : IIntentSource
    {
        public ActionIntent TryDeclare(string actorKey, long nowTick) => ActionIntent.None;
    }

    /// <summary>The SAME `MovementSkill` shape `MovementActionDispatchTests.cs` (task A9) already
    /// proved drives the real production dispatch — reused verbatim rather than re-derived.</summary>
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

    static BattleActorSetup Mover(string key, long moveRange) => new()
    {
        Key = key, Side = "squad", MaxHp = 1000,
        EquippedActionIds = new[] { "skill.move" },
        ChannelMods = new[] { new BattleChannelMod(DerivedStatChannels.MoveRange, moveRange) },
    };

    static BattleActorSetup Bystander(string key) => new() { Key = key, Side = "wave", MaxHp = 1000 };

    static (GridSpec Spec, BoardState Board) Board()
    {
        var spec = new GridSpec(20, 20);
        var board = new BoardState(spec);
        board.Place("squad:0", new GridPos(10, 5));
        board.Place("wave:0", new GridPos(15, 15)); // never a legal target -- AlwaysNoneIntentSource never names it
        return (spec, board);
    }

    [Fact]
    public void An_actor_with_no_target_in_reach_advances_toward_its_own_objective()
    {
        var (spec, board) = Board();
        var catalog = ActionCatalog.Build(new[] { MovementSkill() });
        var setup = new BattleSetup { Squad = new[] { Mover("squad:0", moveRange: 3) }, Wave = new[] { Bystander("wave:0") } };

        var report = FusionRpg.Core.Battle.BattleEngine.Resolve(
            setup, seed: 1, actionCatalog: catalog, board: board, intentSource: new AlwaysNoneIntentSource(),
            onEffectHostReady: host => host.AttackerEdge = BoardEdge.West);

        Assert.NotNull(report);
        // Attacker's own objective is the Core's own centre: (side/2, side/2) = (10, 10) on a 20-wide board.
        var objective = new GridPos(10, 10);
        var startDistance = GridDistance.Chebyshev(new GridPos(10, 5), objective);
        var finalDistance = GridDistance.Chebyshev(board.Positions["squad:0"], objective);
        Assert.True(finalDistance < startDistance,
            $"expected squad:0 to advance toward {objective}, started {startDistance} away, ended {finalDistance} away at {board.Positions["squad:0"]}");
    }

    [Fact]
    public void With_no_siege_context_wired_the_actor_never_moves_toward_a_phantom_objective()
    {
        // The SAME setup, but onEffectHostReady never sets AttackerEdge -- byte-identical to every
        // battle before this task, including every non-siege battle kind that equips a Movement action.
        var (spec, board) = Board();
        var catalog = ActionCatalog.Build(new[] { MovementSkill() });
        var setup = new BattleSetup { Squad = new[] { Mover("squad:0", moveRange: 3) }, Wave = new[] { Bystander("wave:0") } };

        var report = FusionRpg.Core.Battle.BattleEngine.Resolve(
            setup, seed: 1, actionCatalog: catalog, board: board, intentSource: new AlwaysNoneIntentSource());

        Assert.NotNull(report);
        Assert.Equal(new GridPos(10, 5), board.Positions["squad:0"]); // never moved -- no objective wired
    }

    [Fact]
    public void An_actor_holding_no_Movement_action_is_unaffected()
    {
        // squad:0 holds nothing at all -- TryDeclareObjectiveAdvance's own "holds a Movement-tagged
        // action" gate must refuse, proving the new hook does not fire for an actor it was never meant
        // to touch (step 1 would already return ActionIntent.None here too, but AlwaysNoneIntentSource
        // makes that redundant path irrelevant -- this isolates the hook's OWN gate specifically).
        var (spec, board) = Board();
        var setup = new BattleSetup
        {
            Squad = new[] { new BattleActorSetup { Key = "squad:0", Side = "squad", MaxHp = 1000 } },
            Wave = new[] { Bystander("wave:0") },
        };

        var report = FusionRpg.Core.Battle.BattleEngine.Resolve(
            setup, seed: 1, board: board, intentSource: new AlwaysNoneIntentSource(),
            onEffectHostReady: host => host.AttackerEdge = BoardEdge.West);

        Assert.NotNull(report);
        Assert.Equal(new GridPos(10, 5), board.Positions["squad:0"]); // no held action at all -- never moved
    }
}
