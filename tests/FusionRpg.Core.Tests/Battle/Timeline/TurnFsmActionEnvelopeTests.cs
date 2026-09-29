using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// B5 / T2c — the action envelope driven for real: commit, wind-up, published resolve, recovery,
/// cooldown, fizzle, and interrupt.
///
/// Up to here the FSM was exercised by hand-written transitions, which proves the table but not the
/// lifecycle. These tests run the envelope through a queue and a clock, because every bug this task
/// exists to prevent — a slot leaked on the fizzle branch, a resolve that fires after its action was
/// interrupted, a cooldown that never starts — is a *timing* bug and invisible to a scripted
/// transition sequence.
/// </summary>
public class TurnFsmActionEnvelopeTests
{
    /// <summary>
    /// A minimal stand-in for the kernel loop that T4/T5 will own: advance to the next event, hand
    /// each drained event to the runner, repeat. It is deliberately dumb — its only job is to be a
    /// clock and a mailman, so that everything these tests assert is the runner's behaviour and not
    /// the harness's.
    /// </summary>
    sealed class Rig
    {
        public readonly EventQueue Queue = new(64);
        public readonly SimulationClock Clock = new();
        public readonly ActionSlots Slots;
        public readonly CooldownLedger Cooldowns = new();
        public readonly ActionRunner Runner;
        public readonly List<string> Log = new();

        readonly Dictionary<string, ActorTurnMachine> _actors = new(StringComparer.Ordinal);
        readonly HashSet<string> _dead = new(StringComparer.Ordinal);
        readonly NextEventAdvance _advance = new();
        readonly List<ScheduledEvent> _buffer = new(32);

        public Rig(int width = 4, WScope scope = WScope.Global, Func<string, string?, string?>? reselectTarget = null)
        {
            Slots = new ActionSlots(width, scope);
            Runner = new ActionRunner(Queue, Slots, Cooldowns, key => !_dead.Contains(key), reselectTarget: reselectTarget);
        }

        public ActorTurnMachine Add(string key)
        {
            var m = new ActorTurnMachine(key);
            _actors[key] = m;
            return m;
        }

        public ActorTurnMachine Actor(string key) => _actors[key];
        public void Kill(string key) => _dead.Add(key);

        /// <summary>Puts an actor in Ready and commits an intent at the current tick.</summary>
        public CommitRefusal Commit(string actorKey, ActionEnvelope env, string? target = null, string side = "left")
        {
            var m = _actors[actorKey];
            if (m.State == TurnState.Charging) m.TransitionTo(TurnState.Ready);
            return Runner.TryCommit(m, side, new ActionIntent(env.ActionId, target, env), Clock.Now);
        }

        /// <summary>Runs the event loop until the queue empties or <paramref name="untilTick"/> passes.</summary>
        public void Pump(long untilTick = long.MaxValue)
        {
            for (var guard = 0; guard < 10_000; guard++)
            {
                var due = Queue.PeekDueTick();
                if (due is not { } d || d > untilTick) return;

                Clock.TryAdvance(_advance, Queue);
                _buffer.Clear();
                Queue.PopDue(Clock.Now, _buffer);
                for (var i = 0; i < _buffer.Count; i++)
                {
                    var e = _buffer[i];
                    var actor = _actors[e.OwnerKey];
                    switch ((TimelineEventKind)e.Kind)
                    {
                        case TimelineEventKind.Resolve:
                            Log.Add($"{Clock.Now}:{e.OwnerKey}:{Runner.OnResolveDue(actor, e).ToString().ToLowerInvariant()}");
                            break;
                        case TimelineEventKind.Recovery:
                            Runner.OnRecoveryDue(actor, e);
                            Log.Add($"{Clock.Now}:{e.OwnerKey}:recovered");
                            break;
                    }
                }
            }

            throw new InvalidOperationException("pump did not terminate — the kernel is looping");
        }
    }

    static ActionEnvelope Strike(long windup = 100, long recovery = 50, params long[] offsets) =>
        new()
        {
            ActionId = "strike",
            WindupTicks = windup,
            RecoveryTicks = recovery,
            ResolveOffsets = offsets.Length == 0 ? new long[] { 0 } : offsets
        };

    // ---- the acceptance path ----

    [Fact]
    public void A_non_zero_windup_action_traverses_commit_resolve_and_recover()
    {
        var rig = new Rig();
        var a = rig.Add("a");

        Assert.Equal(CommitRefusal.None, rig.Commit("a", Strike(windup: 100, recovery: 50), target: "b"));
        Assert.Equal(TurnState.Committed, a.State);

        // Wind-up is real time, not a formality: nothing has resolved yet at tick 99.
        rig.Pump(untilTick: 99);
        Assert.Equal(TurnState.Committed, a.State);
        Assert.Empty(rig.Log);

        rig.Pump(untilTick: 100);
        Assert.Equal(new[] { "100:a:resolved" }, rig.Log);
        Assert.Equal(TurnState.Recovering, a.State);

        rig.Pump();
        Assert.Equal(new[] { "100:a:resolved", "150:a:recovered" }, rig.Log);
        Assert.Equal(TurnState.Charging, a.State);
    }

    [Fact]
    public void CurrentEnvelope_returns_the_committed_envelope_while_the_run_is_active()
    {
        // A18f (spec-action-dispatch-generalization.md T55.1): mirrors CurrentTarget's own shape --
        // a caller applying the resolved hit needs to know WHICH envelope this actor actually
        // committed, not re-read a hardcoded field.
        var rig = new Rig();
        rig.Add("a");
        var envelope = Strike(windup: 100, recovery: 50);

        Assert.Null(rig.Runner.CurrentEnvelope("a"));   // nothing committed yet
        rig.Commit("a", envelope, target: "b");
        Assert.Equal(envelope, rig.Runner.CurrentEnvelope("a"));

        rig.Pump();   // resolve, then recover -- the run ends
        Assert.Null(rig.Runner.CurrentEnvelope("a"));   // same lifetime as CurrentTarget
    }

    [Fact]
    public void The_slot_is_held_across_windup_and_released_when_resolution_ends()
    {
        var rig = new Rig();
        rig.Add("a");
        rig.Commit("a", Strike(windup: 100, recovery: 50), target: "b");

        Assert.True(rig.Slots.Holds("a"));
        rig.Pump(untilTick: 99);
        Assert.True(rig.Slots.Holds("a"));   // the whole point of W: wind-up occupies the slot

        rig.Pump(untilTick: 100);
        Assert.False(rig.Slots.Holds("a"));  // released on leaving Resolving, not on leaving Recovering
        Assert.Equal(TurnState.Recovering, rig.Actor("a").State);
    }

    [Fact]
    public void A_slot_free_action_runs_at_width_one_while_another_actor_holds_the_only_slot()
    {
        var rig = new Rig(width: 1);
        rig.Add("holder");
        rig.Add("mover");

        Assert.Equal(CommitRefusal.None, rig.Commit("holder", Strike(windup: 500), target: "x"));
        Assert.Equal(1, rig.Slots.Held);

        // Movement and periodic pulses must not queue behind a swing. Without SlotConsuming = false
        // this is CommitRefusal.NoSlot, and at W = 1 only one actor on the board could ever move.
        var walk = new ActionEnvelope { ActionId = "walk", WindupTicks = 10, RecoveryTicks = 0, SlotConsuming = false };
        Assert.Equal(CommitRefusal.None, rig.Commit("mover", walk, target: null, side: "right"));

        Assert.Equal(TurnState.Committed, rig.Actor("mover").State);
        Assert.False(rig.Slots.Holds("mover"));
        Assert.Equal(1, rig.Slots.Held);   // still just the holder's

        rig.Pump(untilTick: 10);
        Assert.Contains("10:mover:resolved", rig.Log);
    }

    [Fact]
    public void A_slot_taking_action_is_refused_when_the_width_is_exhausted()
    {
        // The contrast that keeps the test above honest: same width, same board, only the
        // SlotConsuming flag differs.
        var rig = new Rig(width: 1);
        rig.Add("holder");
        rig.Add("other");
        rig.Commit("holder", Strike(windup: 500), target: "x");

        Assert.Equal(CommitRefusal.NoSlot, rig.Commit("other", Strike(windup: 10), target: "x", side: "right"));
        Assert.Equal(TurnState.Ready, rig.Actor("other").State);
    }

    // ---- fizzle ----

    [Fact]
    public void An_early_bound_action_onto_a_dead_target_fizzles_and_still_pays_full_recovery()
    {
        var rig = new Rig();
        var a = rig.Add("a");
        var env = Strike(windup: 100, recovery: 50) with { Commitment = Commitment.EarlyBound };

        rig.Commit("a", env, target: "b");
        rig.Kill("b");                       // dies during the wind-up

        rig.Pump(untilTick: 100);
        Assert.Equal(new[] { "100:a:fizzled" }, rig.Log);
        Assert.Equal(TurnState.Recovering, a.State);
        Assert.False(rig.Slots.Holds("a"));  // a leaked slot here deadlocks W = 1

        rig.Pump();
        Assert.Equal(new[] { "100:a:fizzled", "150:a:recovered" }, rig.Log);
    }

    [Fact]
    public void A_late_bound_action_onto_a_dead_target_still_resolves()
    {
        // Without this the fizzle test above proves only that something happened at tick 100.
        // `battle-tempo` `commitment-binding` (2026-09-05): LateBound resolving here needs a
        // re-selection delegate configured -- ActionRunner's own contract (D6) is that LateBound
        // WITHOUT one fizzles too (nothing to re-target onto), which `CommitmentBindingTests`
        // covers directly (`WithNoReselectionDelegateConfiguredLateBoundGracefullyFizzles`). This
        // test's own purpose is the ORIGINAL contrast against the fizzle test above -- EarlyBound
        // fizzles unconditionally, LateBound resolves BY RE-TARGETING onto a live fallback -- so a
        // reselect delegate redirecting "b" to a live "c" is what actually demonstrates it.
        var rig = new Rig(reselectTarget: (actorKey, deadTarget) => "c");
        var env = Strike(windup: 100) with { Commitment = Commitment.LateBound };

        rig.Add("a");
        rig.Add("c");
        rig.Commit("a", env, target: "b");
        rig.Kill("b");

        rig.Pump(untilTick: 100);
        Assert.Equal(new[] { "100:a:resolved" }, rig.Log);
    }

    [Fact]
    public void A_fizzle_at_width_one_frees_the_slot_for_the_next_actor()
    {
        var rig = new Rig(width: 1);
        rig.Add("a");
        rig.Add("b");
        rig.Commit("a", Strike(windup: 100, recovery: 0) with { Commitment = Commitment.EarlyBound }, target: "t");
        rig.Kill("t");

        rig.Pump(untilTick: 100);
        Assert.Equal(0, rig.Slots.Held);
        Assert.Equal(CommitRefusal.None, rig.Commit("b", Strike(windup: 10), target: "a", side: "right"));
    }

    // ---- multi-hit ----

    [Fact]
    public void A_multi_hit_action_resolves_once_per_offset_at_the_declared_ticks()
    {
        var rig = new Rig();
        rig.Add("a");
        rig.Commit("a", Strike(windup: 100, recovery: 20, offsets: new long[] { 0, 30, 70 }), target: "b");

        rig.Pump();
        Assert.Equal(
            new[] { "100:a:resolved", "130:a:resolved", "170:a:resolved", "190:a:recovered" },
            rig.Log);
    }

    [Fact]
    public void A_combo_whose_target_dies_mid_way_fizzles_only_its_remaining_hits()
    {
        var rig = new Rig();
        rig.Add("a");
        var env = Strike(windup: 100, recovery: 20, offsets: new long[] { 0, 30, 70 })
            with { Commitment = Commitment.EarlyBound };
        rig.Commit("a", env, target: "b");

        rig.Pump(untilTick: 100);
        Assert.Equal(new[] { "100:a:resolved" }, rig.Log);

        rig.Kill("b");
        rig.Pump();

        // The third hit is cancelled outright rather than firing into a corpse, and recovery runs
        // from the fizzle — full duration, but no waiting out the hits that never landed.
        Assert.Equal(new[] { "100:a:resolved", "130:a:fizzled", "150:a:recovered" }, rig.Log);
        Assert.Equal(0, rig.Queue.Count);
    }

    [Theory]
    [InlineData(-1L, 0L)]
    [InlineData(0L, -5L)]
    [InlineData(30L, 10L)]
    public void Resolve_offsets_must_be_non_negative_and_ordered(long first, long second)
    {
        // Offsets out of order would schedule hit 2 before hit 1, so "cancel the remaining hits"
        // would cancel the wrong ones. Refuse loudly rather than resolve out of sequence.
        var rig = new Rig();
        rig.Add("a");
        var env = Strike(windup: 10, offsets: new[] { first, second });

        Assert.Throws<ArgumentException>(() => rig.Commit("a", env, target: "b"));
    }

    // ---- interrupt: why the resolve is published ----

    [Fact]
    public void An_interrupt_cancels_the_published_resolve_so_it_never_fires()
    {
        var rig = new Rig();
        var a = rig.Add("a");
        rig.Commit("a", Strike(windup: 100, recovery: 50) with { Interruptible = Interruptible.OnCC }, target: "b");
        Assert.Equal(1, rig.Queue.Count);

        var result = rig.Runner.Interrupt(a, 40, InterruptCause.CrowdControl);

        Assert.True(result.Broken);
        Assert.Equal(0, rig.Queue.Count);      // the whole reason the handle is published
        Assert.Equal(TurnState.Charging, a.State);
        Assert.False(rig.Slots.Holds("a"));

        rig.Pump();
        Assert.Empty(rig.Log);                 // no resolve, no recovery
    }

    [Fact]
    public void An_interrupt_cancels_every_outstanding_hit_of_a_combo()
    {
        var rig = new Rig();
        var a = rig.Add("a");
        rig.Commit("a", Strike(windup: 100, offsets: new long[] { 0, 30, 70 }), target: "b");
        Assert.Equal(3, rig.Queue.Count);

        rig.Runner.Interrupt(a, 0, InterruptCause.CrowdControl);
        Assert.Equal(0, rig.Queue.Count);
    }

    [Theory]
    [InlineData(Interruptible.Never, InterruptCause.CrowdControl, false)]
    [InlineData(Interruptible.Never, InterruptCause.Damage, false)]
    [InlineData(Interruptible.OnCC, InterruptCause.CrowdControl, true)]
    [InlineData(Interruptible.OnCC, InterruptCause.Damage, false)]
    [InlineData(Interruptible.OnDamage, InterruptCause.Damage, true)]
    [InlineData(Interruptible.OnDamage, InterruptCause.CrowdControl, true)]
    public void Interruptibility_gates_which_causes_can_break_an_action(
        Interruptible policy, InterruptCause cause, bool expected)
    {
        // OnDamage also yields to CC: a stun stops a swing whatever the envelope says about damage.
        // Stated as a table so the asymmetry is a decision on the record rather than an accident.
        var rig = new Rig();
        var a = rig.Add("a");
        rig.Commit("a", Strike(windup: 100) with { Interruptible = policy }, target: "b");

        Assert.Equal(expected, rig.Runner.Interrupt(a, 10, cause).Broken);
        Assert.Equal(expected ? TurnState.Charging : TurnState.Committed, a.State);
    }

    [Fact]
    public void An_action_already_resolving_cannot_be_interrupted()
    {
        // "Resolving is atomic with respect to the clock" — a mid-combo interrupt would break that.
        var rig = new Rig();
        var a = rig.Add("a");
        rig.Commit("a", Strike(windup: 100, offsets: new long[] { 0, 50 })
            with { Interruptible = Interruptible.OnCC }, target: "b");

        rig.Pump(untilTick: 100);
        Assert.Equal(TurnState.Resolving, a.State);
        Assert.False(rig.Runner.Interrupt(a, 100, InterruptCause.CrowdControl).Broken);
    }

    [Fact]
    public void An_interrupt_reports_the_readiness_refund_without_applying_it()
    {
        // T3 owns readiness; this module owns only the number, so the seam is explicit rather than
        // a silently ignored envelope field.
        var rig = new Rig();
        var a = rig.Add("a");
        rig.Commit("a", Strike(windup: 100) with { InterruptRefundMilli = 400 }, target: "b");

        Assert.Equal(400, rig.Runner.Interrupt(a, 10, InterruptCause.CrowdControl).RefundMilli);
    }

    // ---- cooldowns ----

    [Fact]
    public void A_cooldown_blocks_the_next_commit_until_it_expires()
    {
        var rig = new Rig();
        rig.Add("a");
        var env = Strike(windup: 0, recovery: 0) with
        {
            Class = CooldownClass.Specific, CooldownTicks = 1000, StartsAt = CooldownStart.Commit
        };

        Assert.Equal(CommitRefusal.None, rig.Commit("a", env, target: "b"));
        rig.Pump();
        Assert.Equal(TurnState.Charging, rig.Actor("a").State);

        Assert.Equal(CommitRefusal.OnCooldown, rig.Commit("a", env, target: "b"));
        Assert.Equal(TurnState.Ready, rig.Actor("a").State);   // refused, not transitioned

        Assert.True(rig.Cooldowns.IsReady("a", env, 1000));
        Assert.False(rig.Cooldowns.IsReady("a", env, 999));
    }

    [Theory]
    [InlineData(CooldownStart.Commit, 1000L)]
    [InlineData(CooldownStart.Resolve, 1100L)]
    [InlineData(CooldownStart.RecoveryEnd, 1150L)]
    public void The_cooldown_start_point_decides_when_it_expires(CooldownStart startsAt, long readyAt)
    {
        // Three games, three answers — so which one this action uses is declared, not assumed.
        var rig = new Rig();
        rig.Add("a");
        var env = Strike(windup: 100, recovery: 50) with
        {
            Class = CooldownClass.Specific, CooldownTicks = 1000, StartsAt = startsAt
        };

        rig.Commit("a", env, target: "b");
        rig.Pump();

        Assert.Equal(readyAt, rig.Cooldowns.ReadyAt("a", env));
    }

    [Fact]
    public void A_category_cooldown_is_shared_and_a_specific_one_is_not()
    {
        var ledger = new CooldownLedger();
        var fire = new ActionEnvelope
        {
            ActionId = "fireball", Class = CooldownClass.Category, CooldownKey = "spell", CooldownTicks = 500
        };
        var frost = fire with { ActionId = "frostbolt" };
        var kick = new ActionEnvelope { ActionId = "kick", Class = CooldownClass.Specific, CooldownTicks = 500 };
        var punch = kick with { ActionId = "punch" };

        ledger.Start("a", fire, 0);
        Assert.False(ledger.IsReady("a", frost, 100));   // same category
        Assert.True(ledger.IsReady("b", frost, 100));    // different actor

        ledger.Start("a", kick, 0);
        Assert.True(ledger.IsReady("a", punch, 100));    // Specific keys on the action id
    }

    [Fact]
    public void A_category_cooldown_without_a_key_is_refused_loudly()
    {
        var ledger = new CooldownLedger();
        var bad = new ActionEnvelope { ActionId = "x", Class = CooldownClass.Category, CooldownTicks = 100 };

        Assert.Throws<ArgumentException>(() => ledger.Start("a", bad, 0));
        Assert.Throws<ArgumentException>(() => ledger.IsReady("a", bad, 0));
    }

    [Fact]
    public void A_cooldown_keeps_running_while_its_owner_is_suspended()
    {
        // The stated rule the spec asks for. Cooldowns are absolute ticks on the simulation clock,
        // so a stunned actor's cooldowns keep counting. The alternative — pausing them — needs a
        // stored remainder, which is precisely the cached-remainder design the audit rejected for
        // crowd control, and it would go stale on all the same mutations.
        var ledger = new CooldownLedger();
        var env = new ActionEnvelope { ActionId = "x", Class = CooldownClass.Specific, CooldownTicks = 1000 };

        ledger.Start("a", env, 0);
        Assert.Equal(1000, ledger.ReadyAt("a", env));
        Assert.True(ledger.IsReady("a", env, 1000));
    }

    [Fact]
    public void An_interrupted_action_now_charges_its_cooldown_by_default()
    {
        // action-todo.md T12 (spec-basic-attack-adoption.md §5, decision D3): InterruptCooldownMilli
        // defaults to 1000 -- full cooldown -- replacing the previous behaviour of starting none at
        // all. A resolve-scoped cooldown had nothing to charge before the interrupt; now the
        // interrupt itself starts it.
        var rig = new Rig();
        var a = rig.Add("a");
        var env = Strike(windup: 100) with
        {
            Class = CooldownClass.Specific, CooldownTicks = 1000, StartsAt = CooldownStart.Resolve
        };
        Assert.Equal(1000, env.InterruptCooldownMilli); // the default, asserted so this test states it

        rig.Commit("a", env, target: "b");
        rig.Runner.Interrupt(a, 10, InterruptCause.CrowdControl);

        Assert.False(rig.Cooldowns.IsReady("a", env, 10));
        Assert.True(rig.Cooldowns.IsReady("a", env, 1010)); // full 1000 ticks from the interrupt tick

        // Contrast: a commit-scoped cooldown was already started at commit, before the interrupt.
        var atCommit = env with { StartsAt = CooldownStart.Commit };
        var b = rig.Add("b2");
        rig.Commit("b2", atCommit, target: "x");
        rig.Runner.Interrupt(b, 10, InterruptCause.CrowdControl);
        Assert.False(rig.Cooldowns.IsReady("b2", atCommit, 10));
    }

    [Fact]
    public void InterruptCooldownMilli_at_zero_restores_the_old_free_interrupt_behaviour()
    {
        // Additive and opt-out: a spec that wants the pre-T12 behaviour back sets this to 0.
        var rig = new Rig();
        var a = rig.Add("a");
        var env = Strike(windup: 100) with
        {
            Class = CooldownClass.Specific, CooldownTicks = 1000, StartsAt = CooldownStart.Resolve,
            InterruptCooldownMilli = 0,
        };

        rig.Commit("a", env, target: "b");
        rig.Runner.Interrupt(a, 10, InterruptCause.CrowdControl);

        Assert.True(rig.Cooldowns.IsReady("a", env, 10));
    }

    [Fact]
    public void InterruptCooldownMilli_is_inert_for_a_zero_cooldown_envelope()
    {
        // "Additive and inert for a zero envelope" -- every action adopted so far has CooldownTicks
        // at zero, so this field changes nothing for them.
        var rig = new Rig();
        var a = rig.Add("a");
        var env = Strike(windup: 100); // CooldownTicks defaults to 0

        rig.Commit("a", env, target: "b");
        var result = rig.Runner.Interrupt(a, 10, InterruptCause.CrowdControl);

        Assert.True(result.Broken);
        Assert.True(rig.Cooldowns.IsReady("a", env, 10));
    }

    [Fact]
    public void A_fizzled_action_still_pays_its_cooldown()
    {
        var rig = new Rig();
        rig.Add("a");
        var env = Strike(windup: 100, recovery: 50) with
        {
            Commitment = Commitment.EarlyBound,
            Class = CooldownClass.Specific, CooldownTicks = 1000, StartsAt = CooldownStart.Resolve
        };

        rig.Commit("a", env, target: "b");
        rig.Kill("b");
        rig.Pump();

        Assert.False(rig.Cooldowns.IsReady("a", env, 1000));
        Assert.Equal(1100, rig.Cooldowns.ReadyAt("a", env));
    }

    // ---- guards on the seam itself ----

    [Fact]
    public void Committing_from_a_state_other_than_ready_is_refused_rather_than_throwing()
    {
        var rig = new Rig();
        var a = rig.Add("a");
        rig.Commit("a", Strike(windup: 100), target: "b");

        Assert.Equal(CommitRefusal.NotReady, rig.Runner.TryCommit(
            a, "left", new ActionIntent("strike", "b", Strike()), 0));
    }

    [Fact]
    public void An_empty_intent_is_refused_rather_than_dereferenced()
    {
        var rig = new Rig();
        var a = rig.Add("a");
        a.TransitionTo(TurnState.Ready);

        Assert.Equal(CommitRefusal.NoIntent, rig.Runner.TryCommit(a, "left", ActionIntent.None, 0));
    }

    [Fact]
    public void A_zero_length_envelope_still_completes_a_full_cycle()
    {
        // NoOp proves plumbing and nothing else, which is exactly why it is worth checking that the
        // degenerate case does not stall: every offset lands on the commit tick.
        var rig = new Rig();
        rig.Add("a");
        rig.Commit("a", ActionEnvelope.NoOp, target: "b");

        rig.Pump();
        Assert.Equal(new[] { "0:a:resolved", "0:a:recovered" }, rig.Log);
        Assert.Equal(TurnState.Charging, rig.Actor("a").State);
    }
}
