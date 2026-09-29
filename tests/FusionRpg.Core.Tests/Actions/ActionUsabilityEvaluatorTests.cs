using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>T9 (action-todo.md, spec-usability-conditions.md). Six gates, cheapest first,
/// short-circuiting, each with its own typed refusal.</summary>
public class ActionUsabilityEvaluatorTests
{
    static ActionRow Action(ActionEnvelope? envelope = null, ActionTargetSpec? targeting = null,
        int minRange = 0, int maxRange = 0) => new()
    {
        ActionId = "skill.test",
        Name = "Test",
        Kind = ActionKind.Skill,
        ContainerId = "skill.test",
        Envelope = envelope ?? ActionEnvelope.NoOp with { ActionId = "skill.test" },
        Targeting = targeting ?? new ActionTargetSpec(),
        MinRange = minRange,
        MaxRange = maxRange,
    };

    static FactReader Facts(bool conditionTargetAlive = true)
    {
        var self = new EntityFacts(0, 1, 1000, -1, -1, -1, false, false, 0);
        var target = new EntityFacts(1, 2, conditionTargetAlive ? 1000 : 0, -1, -1, -1, false, false, 0);
        return new FactReader(self, target);
    }

    static UsabilityResult Evaluate(
        ActionRow action,
        bool holds = true,
        long nowTick = 0,
        CooldownLedger? ledger = null,
        IStanceCheck? stance = null,
        IAffordabilityCheck? afford = null,
        GridPos? caster = null,
        GridPos? target = null,
        ICompiledPredicate? condition = null)
    {
        var facts = Facts();
        return UsabilityEvaluator.Evaluate(
            "plant:1", action, holds, nowTick,
            ledger ?? new CooldownLedger(),
            stance ?? NoStanceHeld.Instance,
            afford ?? AlwaysAffordable.Instance,
            caster, target,
            condition ?? PredicateCompiler.Always,
            ref facts);
    }

    [Fact]
    public void Every_gate_open_is_usable()
    {
        var result = Evaluate(Action());
        Assert.True(result.IsUsable, result.ToString());
    }

    [Fact]
    public void Gate0_stance_refuses_first()
    {
        var stance = new FixedStance(UsabilityResult.Refuse(UsabilityReason.StanceHeld));
        var result = Evaluate(Action(), holds: false, stance: stance,
            afford: new FixedAfford(UsabilityResult.Refuse(UsabilityReason.CannotAfford, "qi")));
        Assert.Equal(UsabilityReason.StanceHeld, result.Reason);
    }

    [Fact]
    public void Gate1_not_bound_refuses_with_its_own_reason()
    {
        var result = Evaluate(Action(), holds: false);
        Assert.Equal(UsabilityReason.NotBound, result.Reason);
    }

    [Fact]
    public void Gate2_on_cooldown_refuses_with_its_own_reason()
    {
        var envelope = ActionEnvelope.NoOp with
        {
            ActionId = "skill.test", Class = CooldownClass.Specific, CooldownTicks = 1000,
        };
        var ledger = new CooldownLedger();
        ledger.Start("plant:1", envelope, atTick: 0);

        var result = Evaluate(Action(envelope), ledger: ledger, nowTick: 10);
        Assert.Equal(UsabilityReason.OnCooldown, result.Reason);
    }

    [Fact]
    public void An_action_on_cooldown_AND_unaffordable_reports_OnCooldown_proving_gate_order()
    {
        var envelope = ActionEnvelope.NoOp with
        {
            ActionId = "skill.test", Class = CooldownClass.Specific, CooldownTicks = 1000,
        };
        var ledger = new CooldownLedger();
        ledger.Start("plant:1", envelope, atTick: 0);

        var result = Evaluate(Action(envelope), ledger: ledger, nowTick: 10,
            afford: new FixedAfford(UsabilityResult.Refuse(UsabilityReason.CannotAfford, "qi")));

        Assert.Equal(UsabilityReason.OnCooldown, result.Reason);
    }

    [Fact]
    public void Gate3_cannot_afford_carries_the_resource_id()
    {
        var result = Evaluate(Action(),
            afford: new FixedAfford(UsabilityResult.Refuse(UsabilityReason.CannotAfford, "spirit")));
        Assert.Equal(UsabilityReason.CannotAfford, result.Reason);
        Assert.Equal("spirit", result.Detail);
    }

    [Fact]
    public void Gate4_too_close_refuses_below_min_range()
    {
        var result = Evaluate(Action(minRange: 2, maxRange: 5),
            caster: new GridPos(0, 0), target: new GridPos(0, 1));
        Assert.Equal(UsabilityReason.TooClose, result.Reason);
    }

    [Fact]
    public void Gate4_out_of_range_refuses_above_max_range()
    {
        var result = Evaluate(Action(minRange: 0, maxRange: 3),
            caster: new GridPos(0, 0), target: new GridPos(0, 9));
        Assert.Equal(UsabilityReason.OutOfRange, result.Reason);
    }

    [Fact]
    public void Gate4_passes_with_no_board()
    {
        var result = Evaluate(Action(minRange: 5, maxRange: 5), caster: null, target: null);
        Assert.True(result.IsUsable, result.ToString());
    }

    [Fact]
    public void Gate5_condition_failure_refuses()
    {
        var result = Evaluate(Action(), condition: new AlwaysFalse());
        Assert.Equal(UsabilityReason.ConditionFailed, result.Reason);
    }

    // ---- instrumentation: the short-circuit is measurable ------------------------------------------

    [Fact]
    public void FactReader_reads_stay_zero_when_an_earlier_gate_refuses()
    {
        var facts = Facts();
        var result = UsabilityEvaluator.Evaluate(
            "plant:1", Action(), actorHoldsAction: false, nowTick: 0,
            new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
            null, null, new CountingAlwaysTrue(), ref facts);

        Assert.Equal(UsabilityReason.NotBound, result.Reason);
        Assert.Equal(0, facts.Reads);
    }

    [Fact]
    public void FactReader_reads_are_nonzero_only_when_gate5_actually_runs()
    {
        var facts = Facts();
        var counting = new CountingAlwaysTrue();
        var result = UsabilityEvaluator.Evaluate(
            "plant:1", Action(), actorHoldsAction: true, nowTick: 0,
            new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
            null, null, counting, ref facts);

        Assert.True(result.IsUsable, result.ToString());
        Assert.True(counting.Invoked);
    }

    [Fact]
    public void Evaluation_allocates_zero_bytes_once_warm()
    {
        var action = Action();
        var ledger = new CooldownLedger();

        for (var i = 0; i < 1000; i++) Evaluate(action, ledger: ledger); // JIT + warm

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) Evaluate(action, ledger: ledger);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    // ---- test doubles --------------------------------------------------------------------------------

    // `FixedStance` lives in `FixedStance.cs` since CAI3.1 promoted it out of this nesting, so the
    // run-state seam test can reuse it instead of writing a second stance fake.

    sealed class FixedAfford : IAffordabilityCheck
    {
        readonly UsabilityResult _result;
        public FixedAfford(UsabilityResult result) => _result = result;
        public UsabilityResult Check(string actorKey, string actionId) => _result;
    }

    sealed class AlwaysFalse : ICompiledPredicate
    {
        public bool Evaluate(ref FactReader facts) => false;
    }

    sealed class CountingAlwaysTrue : ICompiledPredicate
    {
        public bool Invoked { get; private set; }
        public bool Evaluate(ref FactReader facts) { Invoked = true; return true; }
    }
}
