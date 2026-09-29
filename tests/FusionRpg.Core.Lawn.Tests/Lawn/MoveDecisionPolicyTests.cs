using FusionRpg.Core.Lawn;
using Xunit;

namespace FusionRpg.Core.Tests.Lawn;

/// <summary>
/// A-M2 lawn-reposition (spec-lawn-reposition.md §4) — MoveDecisionPolicy is the pure, Core-side
/// half of EntityApply.MoveToCell's contract, unit-testable without the injector's game-DLL
/// dependency (the same split EntityWriteGate already uses for combat fields).
/// </summary>
public class MoveDecisionPolicyTests
{
    const int LastCol = 9;
    const int LastRow = 4;

    [Fact]
    public void Out_of_board_destination_clamps_instead_of_throwing()
    {
        var decision = MoveDecisionPolicy.Decide(
            actorAlive: true, actorSpawned: true,
            currentCol: 0, currentRow: 0,
            requestedCol: 99, requestedRow: -5,
            lastCol: LastCol, lastRow: LastRow);

        Assert.Equal(MoveOutcome.Apply, decision.Outcome);
        Assert.Equal(LastCol, decision.Col);
        Assert.Equal(0, decision.Row);
    }

    [Fact]
    public void Clamping_is_deterministic()
    {
        MoveDecision Decide() => MoveDecisionPolicy.Decide(
            actorAlive: true, actorSpawned: true,
            currentCol: 0, currentRow: 0,
            requestedCol: 42, requestedRow: 42,
            lastCol: LastCol, lastRow: LastRow);

        var first = Decide();
        var second = Decide();

        Assert.Equal(first.Outcome, second.Outcome);
        Assert.Equal(first.Col, second.Col);
        Assert.Equal(first.Row, second.Row);
        Assert.Equal(LastCol, first.Col);
        Assert.Equal(LastRow, first.Row);
    }

    [Fact]
    public void Move_to_actors_own_current_cell_skips_without_reaching_apply()
    {
        var decision = MoveDecisionPolicy.Decide(
            actorAlive: true, actorSpawned: true,
            currentCol: 3, currentRow: 2,
            requestedCol: 3, requestedRow: 2,
            lastCol: LastCol, lastRow: LastRow);

        Assert.Equal(MoveOutcome.SkipSameCell, decision.Outcome);
    }

    [Fact]
    public void Same_cell_after_clamping_also_skips()
    {
        // Requested cell is out of board but clamps to the actor's current cell -- still a skip,
        // not an apply-then-noop, because the comparison happens against the CLAMPED destination.
        var decision = MoveDecisionPolicy.Decide(
            actorAlive: true, actorSpawned: true,
            currentCol: LastCol, currentRow: LastRow,
            requestedCol: 999, requestedRow: 999,
            lastCol: LastCol, lastRow: LastRow);

        Assert.Equal(MoveOutcome.SkipSameCell, decision.Outcome);
    }

    [Fact]
    public void Dead_actor_drops_and_is_never_an_apply()
    {
        var decision = MoveDecisionPolicy.Decide(
            actorAlive: false, actorSpawned: true,
            currentCol: 0, currentRow: 0,
            requestedCol: 5, requestedRow: 3,
            lastCol: LastCol, lastRow: LastRow);

        Assert.Equal(MoveOutcome.DropDeadOrUnspawned, decision.Outcome);
    }

    [Fact]
    public void Unspawned_actor_drops_and_is_never_an_apply()
    {
        var decision = MoveDecisionPolicy.Decide(
            actorAlive: true, actorSpawned: false,
            currentCol: 0, currentRow: 0,
            requestedCol: 5, requestedRow: 3,
            lastCol: LastCol, lastRow: LastRow);

        Assert.Equal(MoveOutcome.DropDeadOrUnspawned, decision.Outcome);
    }

    [Fact]
    public void Dead_and_unspawned_is_checked_before_same_cell_so_a_dead_actor_at_the_destination_still_drops()
    {
        // If same-cell were checked first, a dead actor already standing on the requested cell
        // would read as "skip" instead of "drop" -- the wrong counter, and the wrong reason.
        var decision = MoveDecisionPolicy.Decide(
            actorAlive: false, actorSpawned: true,
            currentCol: 3, currentRow: 2,
            requestedCol: 3, requestedRow: 2,
            lastCol: LastCol, lastRow: LastRow);

        Assert.Equal(MoveOutcome.DropDeadOrUnspawned, decision.Outcome);
    }

    [Fact]
    public void Live_actor_moving_to_a_different_in_board_cell_applies()
    {
        var decision = MoveDecisionPolicy.Decide(
            actorAlive: true, actorSpawned: true,
            currentCol: 0, currentRow: 0,
            requestedCol: 4, requestedRow: 3,
            lastCol: LastCol, lastRow: LastRow);

        Assert.Equal(MoveOutcome.Apply, decision.Outcome);
        Assert.Equal(4, decision.Col);
        Assert.Equal(3, decision.Row);
    }
}
