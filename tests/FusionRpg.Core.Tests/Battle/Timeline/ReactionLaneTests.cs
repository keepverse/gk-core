using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// B6 / T2d — the reaction lane. No actors, no battle, no game: the lane is provable as a slot pool
/// plus a nesting counter, exactly like <see cref="ActionSlotsTests"/> proves `W` alone.
/// </summary>
public class ReactionLaneTests
{
    [Fact]
    public void WReact_zero_is_byte_identical_to_no_lane_at_all()
    {
        var lane = new ReactionLane(0);

        Assert.Equal(ReactionOutcome.NoLane, lane.TryEnter("z1", "left"));
        Assert.Equal(0, lane.Depth);

        // Exit on a key that never entered must be a silent no-op, not a throw or a phantom
        // decrement — a caller that checks TryEnter's result before calling Exit never triggers
        // this path in practice, but a defensive caller (or a bug) must not corrupt state.
        lane.Exit("z1");
        Assert.Equal(0, lane.Depth);
    }

    [Fact]
    public void Negative_WReact_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReactionLane(-1));
    }

    [Fact]
    public void A_defender_can_react_regardless_of_its_own_turn_state()
    {
        // The lane never reads ActorTurnMachine — it is FSM-neutral by construction, so a defender
        // mid-Recovering (or in any live state) can still react. Proven here by simply not
        // constructing an ActorTurnMachine at all: TryEnter takes only a key.
        var lane = new ReactionLane(2);

        var outcome = lane.TryEnter("defender", "left");

        Assert.Equal(ReactionOutcome.Entered, outcome);
        Assert.Equal(1, lane.Depth);
    }

    [Fact]
    public void WReact_bounds_concurrent_reactions_independently_of_W()
    {
        var lane = new ReactionLane(1);

        Assert.Equal(ReactionOutcome.Entered, lane.TryEnter("a", "left"));
        Assert.Equal(ReactionOutcome.NoSlot, lane.TryEnter("b", "left"));

        lane.Exit("a");
        Assert.Equal(ReactionOutcome.Entered, lane.TryEnter("b", "left"));
    }

    [Fact]
    public void Same_actor_cannot_double_enter_without_exiting()
    {
        var lane = new ReactionLane(4);

        Assert.Equal(ReactionOutcome.Entered, lane.TryEnter("a", "left"));
        // ActionSlots.TryAcquire refuses a key already holding — composed behaviour, not reimplemented.
        Assert.Equal(ReactionOutcome.NoSlot, lane.TryEnter("a", "left"));
    }

    [Fact]
    public void Exceeding_depth_drops_the_reaction_and_never_recurses()
    {
        var lane = new ReactionLane(10); // width is not the limiting factor here — depth is
        var entered = new List<string>();

        for (var i = 0; i < ReactionLane.DepthLimit; i++)
        {
            var outcome = lane.TryEnter($"actor{i}", "left");
            Assert.Equal(ReactionOutcome.Entered, outcome);
            entered.Add($"actor{i}");
        }

        Assert.Equal(ReactionLane.DepthLimit, lane.Depth);

        // The (DepthLimit + 1)th nested reaction is dropped, not recursed into.
        var overflow = lane.TryEnter("overflow", "left");
        Assert.Equal(ReactionOutcome.DepthExceeded, overflow);
        Assert.Equal(ReactionLane.DepthLimit, lane.Depth); // unchanged — nothing recursed

        // Unwinding the stack restores headroom, proving Depth tracks a real nesting count rather
        // than a one-way counter.
        foreach (var key in entered) lane.Exit(key);
        Assert.Equal(0, lane.Depth);
        Assert.Equal(ReactionOutcome.Entered, lane.TryEnter("after-unwind", "left"));
    }

    [Fact]
    public void Dropped_reactions_emit_telemetry_naming_the_reason()
    {
        var trace = new BattleTrace();

        var noLane = new ReactionLane(0);
        noLane.TryEnter("z1", "left", trace);

        var full = new ReactionLane(1);
        full.TryEnter("a", "left", trace);       // entered
        full.TryEnter("b", "left", trace);       // no-slot — width already spent by "a"

        var deepLane = new ReactionLane(10);
        for (var i = 0; i < ReactionLane.DepthLimit; i++)
            deepLane.TryEnter($"d{i}", "left", trace);
        deepLane.TryEnter("overflow", "left", trace); // depth-exceeded

        Assert.Contains("reaction z1 no-lane", trace.Digest);
        Assert.Contains("reaction a entered", trace.Digest);
        Assert.Contains("reaction b no-slot", trace.Digest);
        Assert.Contains("reaction overflow depth-exceeded", trace.Digest);
    }

    [Fact]
    public void Empty_actor_key_is_rejected()
    {
        var lane = new ReactionLane(1);
        Assert.Throws<ArgumentException>(() => lane.TryEnter("", "left"));
    }
}
