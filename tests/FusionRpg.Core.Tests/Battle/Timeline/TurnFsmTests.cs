using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>T2a — the per-actor state machine. No actors, no battle, no game.</summary>
public class TurnFsmTests
{
    static ActorTurnMachine M() => new("squad:0");

    [Fact]
    public void The_action_cycle_runs_end_to_end()
    {
        var m = M();
        Assert.Equal(TurnState.Charging, m.State);
        m.TransitionTo(TurnState.Ready);
        m.TransitionTo(TurnState.Committed);
        Assert.True(m.IsMidAction);
        m.TransitionTo(TurnState.Resolving);
        Assert.True(m.IsMidAction);
        m.TransitionTo(TurnState.Recovering);
        Assert.False(m.IsMidAction);
        m.TransitionTo(TurnState.Charging);
    }

    [Fact]
    public void Illegal_transitions_throw_and_name_both_states()
    {
        var m = M();
        var ex = Assert.Throws<IllegalTurnTransitionException>(() => m.TransitionTo(TurnState.Resolving));
        Assert.Equal(TurnState.Charging, ex.From);
        Assert.Equal(TurnState.Resolving, ex.To);
        Assert.Contains("squad:0", ex.Message, StringComparison.Ordinal);
        Assert.Equal(TurnState.Charging, m.State);   // failed transition does not move the machine
    }

    [Fact]
    public void An_immortal_can_be_downed_and_revived_without_throwing()
    {
        // THE reason Downed exists. Drawn as Resolving -> Dead (terminal), the revive would
        // attempt Dead -> Charging, which is illegal, which throws — crashing the byte-identity
        // gate instead of reporting a hash diff.
        var m = M();
        m.TransitionTo(TurnState.Ready);
        m.TransitionTo(TurnState.Committed);
        m.TransitionTo(TurnState.Resolving);

        m.TransitionTo(TurnState.Downed);      // hp <= 0 mid-resolution
        Assert.True(m.IsPresent);              // still targetable, not gone
        Assert.False(m.CanAct);                // ...but it cannot take a turn while downed
        m.TransitionTo(TurnState.Charging);    // immortal veto
        Assert.True(m.CanAct);
    }

    /// <summary>Walks a machine to any state by a legal route, so no state can be silently skipped.</summary>
    static ActorTurnMachine At(TurnState target)
    {
        var m = new ActorTurnMachine("x");
        switch (target)
        {
            case TurnState.Charging: break;
            case TurnState.Ready: m.TransitionTo(TurnState.Ready); break;
            case TurnState.Committed:
                m.TransitionTo(TurnState.Ready); m.TransitionTo(TurnState.Committed); break;
            case TurnState.Resolving:
                m.TransitionTo(TurnState.Ready); m.TransitionTo(TurnState.Committed);
                m.TransitionTo(TurnState.Resolving); break;
            case TurnState.Recovering:
                m.TransitionTo(TurnState.Ready); m.TransitionTo(TurnState.Committed);
                m.TransitionTo(TurnState.Resolving); m.TransitionTo(TurnState.Recovering); break;
            case TurnState.Downed: m.TransitionTo(TurnState.Downed); break;
            case TurnState.Dead:
                m.TransitionTo(TurnState.Downed); m.TransitionTo(TurnState.Dead); break;
            case TurnState.Withdrawn: m.TransitionTo(TurnState.Withdrawn); break;
            default: throw new ArgumentOutOfRangeException(nameof(target), target, "unhandled state");
        }

        Assert.Equal(target, m.State);
        return m;
    }

    [Fact]
    public void Downed_is_the_one_state_where_present_and_can_act_disagree()
    {
        // If these two ever agree everywhere, one of them is redundant API.
        //
        // An earlier draft used TryTransitionTo and `continue`d when a state was not reachable in
        // one hop — which silently skipped Committed, Resolving, and Recovering while looking
        // exhaustive. The walk above reaches every state, and the count assert below makes a
        // future added state fail loudly instead of quietly going untested.
        var checkedStates = 0;
        foreach (var s in Enum.GetValues<TurnState>())
        {
            var m = At(s);
            Assert.Equal(s == TurnState.Downed, m.IsPresent && !m.CanAct);
            checkedStates++;
        }

        Assert.Equal(Enum.GetValues<TurnState>().Length, checkedStates);
    }

    [Fact]
    public void Death_is_a_decision_reached_only_through_downed()
    {
        var m = M();
        Assert.False(TurnTransitions.IsLegal(TurnState.Charging, TurnState.Dead));
        Assert.False(TurnTransitions.IsLegal(TurnState.Resolving, TurnState.Dead));
        Assert.True(TurnTransitions.IsLegal(TurnState.Downed, TurnState.Dead));

        m.TransitionTo(TurnState.Downed);
        m.TransitionTo(TurnState.Dead);
        Assert.False(m.IsPresent);
        Assert.False(m.CanAct);
        Assert.True(TurnTransitions.IsTerminal(m.State));
    }

    [Theory]
    [InlineData(TurnState.Charging)]
    [InlineData(TurnState.Ready)]
    [InlineData(TurnState.Committed)]
    [InlineData(TurnState.Resolving)]
    [InlineData(TurnState.Recovering)]
    public void Retreat_is_reachable_from_every_live_state(TurnState from)
    {
        // The engine checks retreat after EVERY dispatch, mid-round, so an actor in any of these
        // can withdraw today. The original diagram drew only Charging -> Withdrawn, and since
        // illegal transitions throw, that omission would have been a runtime crash.
        Assert.True(TurnTransitions.IsLegal(from, TurnState.Withdrawn));
    }

    [Theory]
    [InlineData(TurnState.Charging)]
    [InlineData(TurnState.Ready)]
    [InlineData(TurnState.Committed)]
    [InlineData(TurnState.Resolving)]
    [InlineData(TurnState.Recovering)]
    public void Any_live_state_can_be_downed(TurnState from)
    {
        Assert.True(TurnTransitions.IsLegal(from, TurnState.Downed));
    }

    [Fact]
    public void Terminal_states_have_no_exits()
    {
        foreach (var to in Enum.GetValues<TurnState>())
        {
            Assert.False(TurnTransitions.IsLegal(TurnState.Dead, to), $"Dead -> {to} must be illegal");
            Assert.False(TurnTransitions.IsLegal(TurnState.Withdrawn, to), $"Withdrawn -> {to} must be illegal");
        }
    }

    [Fact]
    public void A_passed_turn_returns_to_charging_without_taking_a_slot()
    {
        // No legal intent: the actor must not sit in Ready holding a slot, or a W=1 battle
        // deadlocks — the queue drains and the clock stops.
        var m = M();
        m.TransitionTo(TurnState.Ready);
        m.TransitionTo(TurnState.Charging);
        Assert.False(m.IsMidAction);
    }

    /// <summary>
    /// The meta-test: the table IS the diagram. A drawing omission became a runtime throw once
    /// already in this design, so the two are bound together rather than kept in sync by hand.
    /// </summary>
    [Fact]
    public void Transition_table_matches_the_documented_diagram()
    {
        var documented = new HashSet<string>(StringComparer.Ordinal)
        {
            "Charging->Ready", "Ready->Committed", "Committed->Resolving",
            "Resolving->Recovering", "Recovering->Charging",
            "Committed->Charging", "Ready->Charging",
            "Charging->Downed", "Ready->Downed", "Committed->Downed",
            "Resolving->Downed", "Recovering->Downed",
            "Downed->Charging", "Downed->Dead",
            "Charging->Withdrawn", "Ready->Withdrawn", "Committed->Withdrawn",
            "Resolving->Withdrawn", "Recovering->Withdrawn", "Downed->Withdrawn"
        };

        var actual = new HashSet<string>(
            TurnTransitions.All.Select(t => $"{t.From}->{t.To}"), StringComparer.Ordinal);

        Assert.Equal(documented.OrderBy(s => s, StringComparer.Ordinal),
                     actual.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void There_is_no_incapacitated_state()
    {
        // CC is a derived read from StatusRuntime at the seat check, never stored here — two
        // sources of truth for one duration is how a 1000 ms stun becomes 999 ms on replay.
        Assert.DoesNotContain(Enum.GetNames<TurnState>(), n =>
            n.Contains("Incapacit", StringComparison.OrdinalIgnoreCase));
    }
}
