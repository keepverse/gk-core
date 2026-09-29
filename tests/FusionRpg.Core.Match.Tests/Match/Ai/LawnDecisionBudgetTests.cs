using System;
using System.Collections.Generic;
using FusionRpg.Core.Match.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Match.Ai;

/// <summary>
/// combat-ai `lawn-cast-trigger` (module 19, CAI4.7, spec-lawn-cast-trigger.md §7) — the per-frame
/// decision budget and its overflow carry. Spec test row 7.
/// </summary>
public class LawnDecisionBudgetTests
{
    const int DueActors = 300;

    static List<string> Keys(int count)
    {
        var keys = new List<string>(count);
        for (var i = 0; i < count; i++) keys.Add("ptr." + i.ToString("D4"));
        return keys;
    }

    [Fact]
    public void More_due_actors_than_the_budget_are_served_fifo_over_successive_frames()
    {
        const int budgetPerFrame = 8;
        const int due = 20;
        var keys = Keys(due);
        var budget = new LawnDecisionBudget(budgetPerFrame);
        foreach (var key in keys) budget.Offer(key, dueTick: 0);

        var frameSizes = new List<int>();
        var served = new List<string>();
        var buffer = new List<string>();
        for (var frame = 0; frame < 5; frame++)
        {
            buffer.Clear();
            var count = budget.Take(buffer);
            frameSizes.Add(count);
            served.AddRange(buffer);
        }

        Assert.Equal(new[] { budgetPerFrame, budgetPerFrame, due - 2 * budgetPerFrame, 0, 0 }, frameSizes);
        // The surplus is carried, not dropped: every due actor is served, in the queue's own order.
        Assert.Equal(keys, served);
        Assert.Equal(0, budget.PendingCount);
    }

    [Fact]
    public void The_whole_queue_is_served_in_order_and_nobody_waits_longer_than_ceil_due_over_budget()
    {
        const int budgetPerFrame = 8;
        var keys = Keys(DueActors);
        var budget = new LawnDecisionBudget(budgetPerFrame);
        foreach (var key in keys) budget.Offer(key, dueTick: 0);

        var served = new List<string>();
        var frames = 0;
        var lastServedFrame = new Dictionary<string, int>(StringComparer.Ordinal);
        var buffer = new List<string>();
        while (budget.PendingCount > 0)
        {
            frames++;
            buffer.Clear();
            if (budget.Take(buffer) == 0) break;
            foreach (var key in buffer)
            {
                served.Add(key);
                lastServedFrame[key] = frames;
            }
        }

        Assert.Equal(keys, served); // FIFO by (due tick, ordinal ptr), never board order
        var bound = (DueActors + budgetPerFrame - 1) / budgetPerFrame;
        foreach (var key in keys) Assert.True(lastServedFrame[key] <= bound);
    }

    [Fact]
    public void An_earlier_due_tick_is_served_before_an_ordinally_smaller_ptr()
    {
        var budget = new LawnDecisionBudget(decisionsPerFrame: 1);
        budget.Offer("ptr.zz", dueTick: 5);
        budget.Offer("ptr.aa", dueTick: 9);

        var served = new List<string>();
        Assert.Equal(1, budget.Take(served));
        Assert.Equal(new[] { "ptr.zz" }, served.ToArray());

        served.Clear();
        Assert.Equal(1, budget.Take(served));
        Assert.Equal(new[] { "ptr.aa" }, served.ToArray());
    }

    [Fact]
    public void Offering_the_same_actor_twice_neither_duplicates_it_nor_moves_it_in_line()
    {
        var budget = new LawnDecisionBudget(decisionsPerFrame: 2);
        budget.Offer("ptr.b", dueTick: 0);
        budget.Offer("ptr.a", dueTick: 0);
        budget.Offer("ptr.b", dueTick: 0); // re-offered every frame by a host that does not track it
        budget.Offer("ptr.b", dueTick: 99);

        Assert.Equal(2, budget.PendingCount);
        var served = new List<string>();
        Assert.Equal(2, budget.Take(served));
        Assert.Equal(new[] { "ptr.a", "ptr.b" }, served.ToArray());
    }

    [Fact]
    public void A_dead_actor_is_dropped_from_the_queue()
    {
        var budget = new LawnDecisionBudget(decisionsPerFrame: 8);
        budget.Offer("ptr.a", dueTick: 0);
        budget.Offer("ptr.b", dueTick: 0);

        Assert.True(budget.Remove("ptr.a"));
        Assert.False(budget.Remove("ptr.a"));
        Assert.Equal(1, budget.PendingCount);

        var served = new List<string>();
        Assert.Equal(1, budget.Take(served));
        Assert.Equal(new[] { "ptr.b" }, served.ToArray());
    }

    [Fact]
    public void Clear_drops_everything_waiting()
    {
        var budget = new LawnDecisionBudget(decisionsPerFrame: 1);
        budget.Offer("ptr.a", dueTick: 0);
        budget.Offer("ptr.b", dueTick: 0);

        budget.Clear();

        Assert.Equal(0, budget.PendingCount);
        Assert.Equal(0, budget.Take(new List<string>()));
    }

    [Fact]
    public void A_non_positive_budget_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LawnDecisionBudget(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LawnDecisionBudget(-1));
    }

    [Fact]
    public void The_default_budget_is_the_structural_per_frame_cap()
    {
        // A closed, code-owned structural constant -- the per-frame WORK cap tunables-ssot.md §1
        // exempts and requires to say so, not a magnitude and not a progression number.
        Assert.Equal(8, LawnDecisionBudget.DecisionsPerFrame);

        var budget = new LawnDecisionBudget();
        for (var i = 0; i < 64; i++) budget.Offer("probe." + i.ToString("D2"), dueTick: 0);
        var served = new List<string>();
        Assert.Equal(LawnDecisionBudget.DecisionsPerFrame, budget.Take(served));
    }
}
