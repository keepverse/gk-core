using FusionRpg.Core.Actions.Defence;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T25 (action-todo.md, spec-defence-actions.md §2.1): "guard consumes a slot to RAISE, then
/// releases it. The status persists, not the slot." Proved against the real `ActionRunner`/
/// `ActionSlots` (T1–T12), at the exact width (`W = 1`) the spec names as the failure case for an
/// indefinite hold: "at W = 1 an indefinite hold freezes the entire board."
/// </summary>
public class DefenceActionStanceSlotTests
{
    static ActionEnvelope RaiseEnvelope(string actionId) => new()
    {
        ActionId = actionId,
        WindupTicks = 0,
        ResolveOffsets = new long[] { 0 },
        RecoveryTicks = 0,
        SlotConsuming = true, // the raise itself DOES take a slot -- spec S2.1
    };

    [Fact]
    public void AtWidthOneOneActorGuardsWhileAnotherActs()
    {
        var queue = new EventQueue(8);
        var slots = new ActionSlots(width: 1, WScope.Global);
        var cooldowns = new CooldownLedger();
        var runner = new ActionRunner(queue, slots, cooldowns, _ => true);
        var catalog = new StatusCatalog();
        var stance = new StanceRuntime(catalog);
        var statuses = new StatusRuntime(catalog, (_, _) => ActorDerivedSnapshot.Empty);

        var guarder = new ActorTurnMachine("wave:0");
        guarder.TransitionTo(TurnState.Ready);
        var attacker = new ActorTurnMachine("wave:1");
        attacker.TransitionTo(TurnState.Ready);

        // wave:0 raises guard: commit takes the ONE slot, an instant (0-windup, 0-resolve) action
        // resolves it and releases the slot the same tick.
        var raiseEnvelope = RaiseEnvelope("act.guard-raise");
        var commitRaise = runner.TryCommit(guarder, "wave", new ActionIntent("act.guard-raise", null, raiseEnvelope), nowTick: 0);
        Assert.Equal(CommitRefusal.None, commitRaise);
        Assert.True(slots.Holds("wave:0")); // held DURING the raise's own resolve

        var resolveEvent = new ScheduledEvent(0, 0, "wave:0", (int)TimelineEventKind.Resolve, 0);
        runner.OnResolveDue(guarder, resolveEvent);
        Assert.False(slots.Holds("wave:0")); // released the instant the raise resolves

        stance.Raise(statuses, "wave:0", "act.guard-release", Array.Empty<StatusStatMod>(), DateTimeOffset.UnixEpoch);

        // The held STATUS persists, but it holds no slot at all -- proven by the OTHER actor
        // successfully taking the (now free) single slot.
        Assert.True(stance.IsHeld("wave:0"));
        var attackEnvelope = new ActionEnvelope { ActionId = "act.attack", SlotConsuming = true, ResolveOffsets = new long[] { 0 } };
        var commitAttack = runner.TryCommit(attacker, "wave", new ActionIntent("act.attack", "target", attackEnvelope), nowTick: 1);

        Assert.Equal(CommitRefusal.None, commitAttack); // NOT NoSlot -- the board is not frozen
        Assert.True(slots.Holds("wave:1"));
    }

    [Fact]
    public void APlantedSlotConsumingHoldWouldFreezeTheBoardAtWidthOne()
    {
        // The counter-example the acceptance line names directly: if the HELD state (not just the
        // raise) wrongly held the slot, a second actor could never commit at W=1. This test plants
        // exactly that bug -- the raise's slot deliberately never released (resolve never called) --
        // and confirms the board DOES freeze, which is what proves the real flow above is actually
        // doing something (a test that could never fail either way would prove nothing).
        var queue = new EventQueue(8);
        var slots = new ActionSlots(width: 1, WScope.Global);
        var cooldowns = new CooldownLedger();
        var runner = new ActionRunner(queue, slots, cooldowns, _ => true);

        var guarder = new ActorTurnMachine("wave:0");
        guarder.TransitionTo(TurnState.Ready);
        var attacker = new ActorTurnMachine("wave:1");
        attacker.TransitionTo(TurnState.Ready);

        var raiseEnvelope = RaiseEnvelope("act.guard-raise");
        runner.TryCommit(guarder, "wave", new ActionIntent("act.guard-raise", null, raiseEnvelope), nowTick: 0);
        // Deliberately NOT resolving -- simulates a buggy "hold consumes the slot" implementation.

        var attackEnvelope = new ActionEnvelope { ActionId = "act.attack", SlotConsuming = true, ResolveOffsets = new long[] { 0 } };
        var commitAttack = runner.TryCommit(attacker, "wave", new ActionIntent("act.attack", "target", attackEnvelope), nowTick: 1);

        Assert.Equal(CommitRefusal.NoSlot, commitAttack); // the board IS frozen under the planted bug
    }
}
