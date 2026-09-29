using System;
using System.Collections.Generic;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Match.Ai;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Match.Ai;

/// <summary>
/// combat-ai `lawn-cast-activation` (module 18, CAI4.6, spec-lawn-cast-activation.md §1) — the PURE
/// half of the module: the ordered plan, its total refusal, and the activation event it builds. Spec
/// test rows 1 and 2.
///
/// <para>Rows 3, 4 and 5 are NOT here, and each for a path outside this lane's fence:
/// <c>EffectEventDto.CastOrigin</c> and the two charge/counter refusals it drives are
/// <c>gk-core/src/FusionRpg.Contracts/**</c> + <c>gk-fusion/src/FusionRpg.Injector/**</c>, and <c>Fire</c>'s depth guard is
/// the injector fire site. See `tasks/reports/CAI4.6.md`.</para>
/// </summary>
public class LawnCastPlanTests
{
    const string Actor = "ptr.caster";
    const string Target = "ptr.target";
    const long CooldownTicks = 20;

    static ActionEnvelope Envelope(string actionId) =>
        ActionEnvelope.NoOp with
        {
            ActionId = actionId,
            Class = CooldownClass.Specific,
            CooldownTicks = CooldownTicks,
        };

    static ActionIntent Intent(string actionId = "act.skill") => new(actionId, Target, Envelope(actionId));

    /// <summary>A real <see cref="CostLedger"/> over a real pool — production collaborators, never a
    /// fake, so a shortfall is the ledger's own answer.</summary>
    static CostLedger LedgerFor(long stamina, params (string ActionId, int Amount)[] costs)
    {
        var rows = new Dictionary<string, IReadOnlyList<ActionCostRow>>(StringComparer.Ordinal);
        foreach (var (actionId, amount) in costs)
        {
            rows[actionId] = new[]
            {
                new ActionCostRow(actionId, "stamina", ValueSpec.Of(amount), ActionCostTiming.OnCommit),
            };
        }

        var stored = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var id in DerivedStatChannels.ResourceIds) stored[id] = 1000;
        stored["stamina"] = stamina;
        var pools = ActorResourcePools.FromStored(stored, atTick: 0);
        // The ledger's affordability read needs a REAL derived snapshot: `resource.max.<id>` is what the
        // pool is measured against, and an empty modifier list would make every pool read as zero.
        var modifiers = new List<DerivedModifier>();
        foreach (var id in DerivedStatChannels.ResourceIds)
            modifiers.Add(new DerivedModifier(DerivedStatChannels.ResourceMax(id), DerivedModifierOp.Flat, 1000.0, SourceId: "test"));
        var derived = new DerivedComposer(DerivedStatRegistry.CreateDefault()).Compose(modifiers);

        return new CostLedger(rows, _ => pools, _ => derived, (_, _) => 1, () => 0L);
    }

    // ---- row 1: the order, and the total refusal -------------------------------------------

    [Fact]
    public void An_insufficient_funds_outcome_starts_no_cooldown_and_produces_no_event()
    {
        var ledger = LedgerFor(stamina: 0, ("act.skill", 80));
        var cooldowns = new CooldownLedger();

        var plan = LawnCastPlan.Build(Actor, Intent(), nowTick: 100, ledger, cooldowns);

        Assert.False(plan.Fired);
        Assert.Equal(LawnCastOutcome.InsufficientFunds, plan.Outcome);
        Assert.Null(plan.Event);
        Assert.Equal("stamina", plan.ShortfallResourceId);
        // Nothing below the payment happened: no cooldown was armed, so a refused cast never leaves a
        // cooldown behind (this is what swapping steps 1 and 2 would break).
        Assert.True(cooldowns.IsReady(Actor, Envelope("act.skill"), 100));
        Assert.Equal(0, cooldowns.ReadyAt(Actor, Envelope("act.skill")));
    }

    [Fact]
    public void A_paid_cast_starts_the_cooldown_and_builds_the_activation_event()
    {
        var ledger = LedgerFor(stamina: 1000, ("act.skill", 80));
        var cooldowns = new CooldownLedger();

        var plan = LawnCastPlan.Build(Actor, Intent(), nowTick: 100, ledger, cooldowns);

        Assert.True(plan.Fired);
        Assert.Equal(LawnCastOutcome.Cast, plan.Outcome);
        Assert.Null(plan.ShortfallResourceId);

        // The event is battle's own activation raise, field for field.
        var activation = plan.Event!;
        Assert.Equal(FusionRpg.Contracts.EffectTriggers.OnActivate, activation.Trigger);
        Assert.Equal(Actor, activation.ActorPtr);
        Assert.Equal(Target, activation.TargetPtr); // the POST-DECISION target, never re-picked
        Assert.Equal(100, activation.Tick);
        Assert.Equal(1, activation.HitCount);

        // And the cost was really paid, once, through the ledger.
        Assert.False(cooldowns.IsReady(Actor, Envelope("act.skill"), 100));
        Assert.Equal(100 + CooldownTicks, cooldowns.ReadyAt(Actor, Envelope("act.skill")));
    }

    [Fact]
    public void A_paid_cast_charges_exactly_once_at_commit()
    {
        var ledger = LedgerFor(stamina: 100, ("act.skill", 80));
        var cooldowns = new CooldownLedger();

        var first = LawnCastPlan.Build(Actor, Intent(), nowTick: 0, ledger, cooldowns);
        var second = LawnCastPlan.Build(Actor, Intent(), nowTick: 1, ledger, cooldowns);

        Assert.True(first.Fired);
        // 100 - 80 = 20 left, so the second cast cannot afford another 80: the first really paid.
        Assert.False(second.Fired);
        Assert.Equal(LawnCastOutcome.InsufficientFunds, second.Outcome);
        Assert.Equal("stamina", second.ShortfallResourceId);
    }

    [Fact]
    public void A_zero_cost_action_casts_and_starts_its_cooldown()
    {
        var ledger = LedgerFor(stamina: 0); // no rows at all for this action
        var cooldowns = new CooldownLedger();

        var plan = LawnCastPlan.Build(Actor, Intent("act.free"), nowTick: 7, ledger, cooldowns);

        Assert.True(plan.Fired);
        Assert.Equal(7, plan.Event!.Tick);
        Assert.Equal(7 + CooldownTicks, cooldowns.ReadyAt(Actor, Envelope("act.free")));
    }

    [Fact]
    public void An_intent_whose_envelope_names_a_different_action_is_refused_loudly()
    {
        // The plan pays `ActionId` and arms `Envelope`, so a mismatched pair would charge one action and
        // cool another. This is what the owed order path (module 20, which looks an envelope up by id) is
        // most likely to get wrong, and a silent mis-charge is worse than a thrown programming error.
        var ledger = LedgerFor(stamina: 1000, ("act.skill", 80));
        var cooldowns = new CooldownLedger();
        var mismatched = new ActionIntent("act.skill", Target, Envelope("act.other"));

        var ex = Assert.Throws<ArgumentException>(
            () => LawnCastPlan.Build(Actor, mismatched, nowTick: 0, ledger, cooldowns));

        // The message names both, because "something mismatched" is not actionable.
        Assert.Contains("act.skill", ex.Message);
        Assert.Contains("act.other", ex.Message);
        // And nothing was charged or cooled on the way to the refusal.
        Assert.True(cooldowns.IsReady(Actor, Envelope("act.skill"), 0));
    }

    [Fact]
    public void Declaring_nothing_yields_None_rather_than_throwing()
    {
        var ledger = LedgerFor(stamina: 1000);
        var cooldowns = new CooldownLedger();

        var plan = LawnCastPlan.Build(Actor, ActionIntent.None, nowTick: 0, ledger, cooldowns);

        Assert.Equal(LawnCastOutcome.None, plan.Outcome);
        Assert.False(plan.Fired);
        Assert.Null(plan.Event);
        Assert.Null(plan.ShortfallResourceId);
    }

    [Fact]
    public void The_plan_reads_no_clock_of_its_own()
    {
        // The tick is supplied, never read: the same inputs at two different `nowTick` values differ
        // ONLY in the tick the event carries, which is what keeps the lawn on one time base.
        var ledger = LedgerFor(stamina: 1000, ("act.skill", 80));
        var cooldowns = new CooldownLedger();

        var early = LawnCastPlan.Build(Actor, Intent(), nowTick: 10, ledger, cooldowns);
        var late = LawnCastPlan.Build(Actor, Intent(), nowTick: 999, ledger, cooldowns);

        Assert.Equal(10, early.Event!.Tick);
        Assert.Equal(999, late.Event!.Tick);
        Assert.Equal(early.Event.Trigger, late.Event.Trigger);
        Assert.Equal(early.Event.ActorPtr, late.Event.ActorPtr);
    }

    [Fact]
    public void An_empty_actor_or_a_null_ledger_is_a_caller_bug()
    {
        var ledger = LedgerFor(stamina: 1000);
        var cooldowns = new CooldownLedger();

        Assert.Throws<ArgumentException>(() => LawnCastPlan.Build("", Intent(), 0, ledger, cooldowns));
        Assert.Throws<ArgumentNullException>(() => LawnCastPlan.Build(Actor, Intent(), 0, null!, cooldowns));
        Assert.Throws<ArgumentNullException>(() => LawnCastPlan.Build(Actor, Intent(), 0, ledger, null!));
    }
}
